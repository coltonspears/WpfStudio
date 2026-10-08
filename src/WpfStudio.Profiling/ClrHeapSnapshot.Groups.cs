using System.Globalization;
using Microsoft.Diagnostics.Runtime;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Value grouping reads captured memory: strings by content, arrays by their raw elements, and other objects by
/// their field values (references compare by identity), so duplicate instances fall into one group.</summary>
public sealed partial class ClrHeapSnapshot
{
    private const int MaxHashedArrayBytes = 8 << 20;

    public MemoryInstanceGroups GetInstanceGroups(MemoryGroupRequest request, CancellationToken token = default)
    {
        if (request.By != "Value") return Analysis.GetInstanceGroups(request, token);
        var instances = Analysis.InstancesOf(request.TypeKey);
        var sampled = instances[..Math.Min(instances.Length, Math.Clamp(request.MaxInstances, 1, 200_000))];
        var groups = new Dictionary<string, HeapAnalysis.InstanceGroupBuilder>(StringComparer.Ordinal);
        var buffer = new byte[64 * 1024];
        for (var i = 0; i < sampled.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var id = sampled[i];
            string key, title;
            try { (key, title) = ValueSignature(id, buffer); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { (key, title) = ("unreadable", "Unreadable in this capture"); }
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new(key, title, "", "Value", []);
            Analysis.AddToGroup(group, id);
        }
        var unique = new HeapAnalysis.InstanceGroupBuilder("unique", "Unique values", "Instances whose value appears once in the sample.", "Unique", []);
        var result = new List<HeapAnalysis.InstanceGroupBuilder>();
        foreach (var group in groups.Values)
        {
            if (group.Members.Count == 1 && group.Key != "unreadable")
            {
                unique.Members.Add(group.Members[0]); unique.Retained += group.Retained; unique.Shallow += group.Shallow;
                continue;
            }
            // Every copy after the first is waste: one shared instance would do.
            group.Wasted = group.Shallow - group.Shallow / group.Members.Count;
            group.Detail = $"{group.Members.Count:N0} identical instances";
            result.Add(group);
        }
        if (unique.Members.Count > 0) result.Add(unique);
        var duplicates = result.Count(g => g.Kind == "Value");
        return Analysis.FinishGroups(request, "Value", instances.Length, sampled.Length, result,
            _ => duplicates == 0 ? "No duplicate values" : $"{duplicates:N0} duplicated value{(duplicates == 1 ? "" : "s")}");
    }

    private (string Key, string Title) ValueSignature(int id, byte[] buffer)
    {
        var address = Analysis.AddressOf(id);
        var obj = _runtime.Heap.GetObject(address);
        var type = obj.Type;
        if (type is null) return ("unreadable", "Unreadable in this capture");
        if (type.IsString)
        {
            var text = obj.AsString(1024) ?? "";
            return ("s:" + text, Quote(text, 96));
        }
        if (obj.IsArray)
        {
            var array = obj.AsArray();
            var bytes = (long)array.Length * Math.Max(1, type.ComponentSize);
            if (bytes > MaxHashedArrayBytes) return ($"len:{array.Length}", $"Length {array.Length:N0} (contents not compared)");
            var hash = HashMemory(address + (ulong)(2 * _target.DataReader.PointerSize), bytes, buffer);
            return ($"arr:{array.Length}:{hash:X16}", array.Length == 0 ? "Empty" : $"Length {array.Length:N0} · same contents");
        }
        var key = new System.Text.StringBuilder();
        var shown = new List<string>();
        foreach (var field in type.Fields.Take(48))
        {
            string part, readable;
            if (field.IsObjectReference)
            {
                var value = field.ReadObject(address, false);
                if (value.IsNull) part = readable = "null";
                else if (value.Type?.IsString == true) { var text = value.AsString(256) ?? ""; part = "\"" + text; readable = Quote(text, 32); }
                else { part = "@" + value.Address.ToString("X", CultureInfo.InvariantCulture); readable = "{" + MemoryLabels.ShortType(value.Type?.Name ?? "object") + "}"; }
            }
            else
            {
                var size = Math.Clamp(field.Size, 0, buffer.Length);
                var span = buffer.AsSpan(0, size);
                var read = size == 0 ? 0 : _target.DataReader.Read(field.GetAddress(address, false), span);
                part = Convert.ToHexString(span[..read]);
                readable = field.ElementType == ClrElementType.Struct ? "{…}" : ReadScalar(field, address, false);
            }
            key.Append(part).Append('\u001f');
            if (shown.Count < 4) shown.Add($"{field.Name} = {readable}");
        }
        var title = shown.Count == 0 ? "{no fields}" : "{" + string.Join(", ", shown) + (type.Fields.Length > 4 ? ", …" : "") + "}";
        return ("f:" + key, title);
    }

    /// <summary>FNV-1a over captured memory, read in chunks.</summary>
    private ulong HashMemory(ulong address, long length, byte[] buffer)
    {
        var hash = 14695981039346656037UL;
        for (long offset = 0; offset < length;)
        {
            var chunk = (int)Math.Min(buffer.Length, length - offset);
            var read = _target.DataReader.Read(address + (ulong)offset, buffer.AsSpan(0, chunk));
            if (read <= 0) { hash ^= (ulong)offset; break; }
            for (var i = 0; i < read; i++) { hash ^= buffer[i]; hash *= 1099511628211UL; }
            offset += read;
        }
        return hash ^ (ulong)length;
    }
}
