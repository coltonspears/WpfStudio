using System.Globalization;
using Microsoft.Diagnostics.Runtime;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Object-browser children read from captured memory: declared fields, array elements, and collection-aware
/// views of List, Dictionary, HashSet, Queue, Stack and ObservableCollection. Getters are never invoked.</summary>
public sealed partial class ClrHeapSnapshot
{
    public MemoryObjectChildren GetChildren(MemoryChildrenRequest request, CancellationToken token = default)
    {
        var description = Analysis.Describe(request.ObjectId);
        var obj = _runtime.Heap.GetObject(Analysis.AddressOf(request.ObjectId));
        var skip = Math.Max(0, request.Skip); var take = Math.Clamp(request.Take, 1, 500);
        var type = obj.Type;
        if (type is null) return new(request.ObjectId, "Object", description.Type, 0, skip, []);
        if (!request.Raw)
        {
            if (type.IsString)
            {
                var text = obj.AsString(4096) ?? "<unreadable>";
                return new(request.ObjectId, "String", Quote(text, 4096), 0, 0, [new("Length", "System.Int32", text.Length.ToString(CultureInfo.InvariantCulture), "Field")]);
            }
            if (obj.IsArray) return ArrayChildren(request.ObjectId, obj.AsArray(), skip, take, token);
            if (CollectionChildren(request.ObjectId, obj, type, skip, take, token) is { } collection) return collection;
        }
        var items = new List<MemoryChildItem>();
        var targets = new List<MemoryChildItem>();
        if (obj.IsDelegate && !request.Raw)
            foreach (var target in obj.AsDelegate().EnumerateDelegateTargets().Take(20))
            {
                var method = target.Method?.Signature ?? target.Method?.Name ?? "<unknown method>";
                targets.Add(target.TargetObject.IsNull
                    ? new("Method", "static", method, "Delegate")
                    : Reference(target.TargetObject.Type?.Name ?? "object", "Target", target.TargetObject, "Delegate") with { Value = method });
            }
        var fields = type.Fields;
        var total = targets.Count + fields.Length;
        foreach (var item in targets.Skip(skip).Take(take)) items.Add(item);
        foreach (var field in fields.Skip(Math.Max(0, skip - targets.Count)).Take(take - items.Count))
        {
            token.ThrowIfCancellationRequested();
            items.Add(Field(field, obj.Address, false));
        }
        var preview = obj.IsDelegate ? "Delegate → " + (targets.FirstOrDefault()?.Value ?? MemoryLabels.ShortType(description.Type)) : "{" + MemoryLabels.ShortType(description.Type) + "}";
        return new(request.ObjectId, obj.IsDelegate ? "Delegate" : "Object", preview, total, skip, items);
    }

    private MemoryObjectChildren ArrayChildren(int id, ClrArray array, int skip, int take, CancellationToken token)
    {
        var length = array.Rank == 1 ? array.Length : 0;
        var component = array.Type.ComponentType;
        var items = new List<MemoryChildItem>();
        for (var i = skip; i < length && items.Count < take; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            items.Add(Element(array, component, i, $"[{i}]", "Element"));
        }
        var preview = $"{MemoryLabels.ShortType(array.Type.Name ?? "array")} · Length = {array.Length:N0}" + (array.Rank > 1 ? $" · rank {array.Rank}" : "");
        try
        {
            // Bytes and characters read better as a hex dump or text than one element per row.
            if (component?.ElementType == ClrElementType.UInt8 && length > 0)
                preview += " · " + Convert.ToHexString(array.ReadValues<byte>(0, Math.Min(length, 24)) ?? []).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b) + (length > 24 ? " …" : "");
            else if (component?.ElementType == ClrElementType.Char && length > 0)
                preview += " · " + Quote(new string(array.ReadValues<char>(0, Math.Min(length, 120)) ?? []), 120);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { }
        return new(id, "Array", preview, length, skip, items);
    }

    private MemoryChildItem Element(ClrArray array, ClrType? component, int index, string name, string kind)
    {
        try
        {
            if (component is null || component.IsObjectReference || array.Type.ComponentType is null && array.Type.ContainsPointers)
                return Reference(component?.Name ?? "object", name, array.GetObjectValue(index), kind);
            if (component.IsPrimitive || component.IsEnum)
                return new(name, component.Name ?? component.ElementType.ToString(), ReadArrayScalar(array, component, index), kind);
            var value = array.GetStructValue(index);
            return new(name, component.Name ?? "struct", StructPreview(value), kind);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        { return new(name, component?.Name ?? "", "<unreadable in this capture>", kind); }
    }

    private static string ReadArrayScalar(ClrArray array, ClrType component, int index)
    {
        var text = component.ElementType switch
        {
            ClrElementType.Boolean => array.GetValue<bool>(index) ? "true" : "false",
            ClrElementType.Char => "'" + array.GetValue<char>(index) + "'",
            ClrElementType.Int8 => array.GetValue<sbyte>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt8 => array.GetValue<byte>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int16 => array.GetValue<short>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt16 => array.GetValue<ushort>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int32 => array.GetValue<int>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt32 => array.GetValue<uint>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int64 => array.GetValue<long>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt64 => array.GetValue<ulong>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Float => array.GetValue<float>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Double => array.GetValue<double>(index).ToString(CultureInfo.InvariantCulture),
            ClrElementType.NativeInt => "0x" + array.GetValue<nint>(index).ToString("X", CultureInfo.InvariantCulture),
            ClrElementType.NativeUInt => "0x" + array.GetValue<nuint>(index).ToString("X", CultureInfo.InvariantCulture),
            _ => "<value>"
        };
        return component.IsEnum ? EnumName(component, text) : text;
    }

    private MemoryObjectChildren? CollectionChildren(int id, ClrObject obj, ClrType type, int skip, int take, CancellationToken token)
    {
        var name = type.Name ?? "";
        var items = new List<MemoryChildItem>();
        if (skip == 0) items.Add(new("Raw view", name, "Declared fields", "Raw", id, HasChildren: true));
        // Dictionary<TKey, TValue>: modern _entries/_count, Framework entries/count. Free entries are skipped.
        if (name.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal))
        {
            var entries = ReadObject(obj, "_entries") ?? ReadObject(obj, "entries");
            var count = ReadInt(obj, "_count") ?? ReadInt(obj, "count") ?? 0;
            var free = ReadInt(obj, "_freeCount") ?? ReadInt(obj, "freeCount") ?? 0;
            var live = Math.Max(0, count - free);
            if (entries is not { IsArray: true } entryArray) return new(id, "Dictionary", $"Count = {live:N0}", live, skip, items);
            var array = entryArray.AsArray(); var seen = 0;
            for (var i = 0; i < Math.Min(count, array.Length) && items.Count < take + (skip == 0 ? 1 : 0); i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var entry = array.GetStructValue(i);
                if (entry.Type is null) break;
                var next = entry.Type.GetFieldByName("next"); var hash = entry.Type.GetFieldByName("hashCode");
                var isFree = next is not null && entry.Type.GetFieldByName("hashCode")?.ElementType == ClrElementType.UInt32
                    ? next.Read<int>(entry.Address, true) < -1
                    : hash is not null && hash.ElementType == ClrElementType.Int32 && hash.Read<int>(entry.Address, true) < 0;
                if (isFree) continue;
                if (seen++ < skip) continue;
                var key = entry.Type.GetFieldByName("key"); var value = entry.Type.GetFieldByName("value");
                if (key is null || value is null) break;
                var keyItem = Field(key, entry.Address, true);
                items.Add(Field(value, entry.Address, true, "[" + keyItem.Value + "]") with { Kind = "Entry" });
            }
            return new(id, "Dictionary", $"Count = {live:N0}", live, skip, items);
        }
        if (name.StartsWith("System.Collections.Generic.HashSet<", StringComparison.Ordinal))
        {
            var modern = ReadObject(obj, "_entries");
            var slots = modern ?? ReadObject(obj, "m_slots");
            var count = modern is not null ? ReadInt(obj, "_count") ?? 0 : ReadInt(obj, "m_lastIndex") ?? 0;
            var live = modern is not null ? count - (ReadInt(obj, "_freeCount") ?? 0) : ReadInt(obj, "m_count") ?? 0;
            if (slots is not { IsArray: true } slotArray) return new(id, "Set", $"Count = {live:N0}", live, skip, items);
            var array = slotArray.AsArray(); var seen = 0;
            for (var i = 0; i < Math.Min(count, array.Length) && items.Count < take + (skip == 0 ? 1 : 0); i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var slot = array.GetStructValue(i);
                if (slot.Type is null) break;
                var next = slot.Type.GetFieldByName("Next"); var hash = slot.Type.GetFieldByName("hashCode");
                var isFree = next is not null ? next.Read<int>(slot.Address, true) < -1 : hash is not null && hash.Read<int>(slot.Address, true) < 0;
                if (isFree) continue;
                if (seen++ < skip) continue;
                var value = slot.Type.GetFieldByName("Value") ?? slot.Type.GetFieldByName("value");
                if (value is null) break;
                items.Add(Field(value, slot.Address, true, $"[{seen - 1}]") with { Kind = "Element" });
            }
            return new(id, "Set", $"Count = {live:N0}", live, skip, items);
        }
        // List<T>, Stack<T>, Queue<T>, ArrayList and wrappers such as ObservableCollection<T> and ReadOnlyCollection<T>.
        var backing = obj; var shape = "List";
        for (var depth = 0; depth < 3; depth++)
        {
            var backingName = backing.Type?.Name ?? "";
            var array = ReadObject(backing, "_items") ?? ReadObject(backing, "_array");
            var size = ReadInt(backing, "_size");
            if (array is { IsArray: true } && size is int length)
            {
                var head = backingName.StartsWith("System.Collections.Generic.Queue<", StringComparison.Ordinal) ? ReadInt(backing, "_head") ?? 0 : 0;
                var stack = backingName.StartsWith("System.Collections.Generic.Stack<", StringComparison.Ordinal);
                var elements = array.Value.AsArray(); var component = elements.Type.ComponentType;
                for (var i = skip; i < length && items.Count < take + (skip == 0 ? 1 : 0); i++)
                {
                    if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                    var physical = stack ? length - 1 - i : (head + i) % Math.Max(1, elements.Length);
                    items.Add(Element(elements, component, physical, $"[{i}]", "Element"));
                }
                return new(id, shape, $"Count = {length:N0}", length, skip, items);
            }
            // Collection<T>-style wrappers keep the real list in "items" or "list".
            var inner = ReadObject(backing, "items") ?? ReadObject(backing, "list") ?? ReadObject(backing, "_list");
            if (inner is not { } next || next.Address == backing.Address) break;
            backing = next;
        }
        return null;
    }

    private MemoryChildItem Field(ClrInstanceField field, ulong address, bool interior, string? name = null)
    {
        var label = name ?? field.Name ?? "<field>";
        try
        {
            if (field.IsObjectReference) return Reference(field.Type?.Name ?? "object", label, field.ReadObject(address, interior), "Field");
            if (field.ElementType == ClrElementType.Struct)
                return new(label, field.Type?.Name ?? "struct", StructPreview(field.ReadStruct(address, interior)), "Field");
            return new(label, field.Type?.Name ?? field.ElementType.ToString(), ReadScalar(field, address, interior), "Field");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        { return new(label, field.Type?.Name ?? field.ElementType.ToString(), "<unreadable in this capture>", "Field"); }
    }

    private MemoryChildItem Reference(string declaredType, string name, ClrObject value, string kind)
    {
        if (value.IsNull) return new(name, declaredType, "null", kind, IsNull: true);
        int? id = _addresses.TryGetValue(value.Address, out var found) ? found : null;
        var info = id is int known ? Analysis.Describe(known) : null;
        var typeName = value.Type?.Name ?? declaredType;
        return new(name, typeName, Preview(value), kind, id, info?.RetainedBytes ?? 0, info?.ShallowBytes ?? (long)value.Size,
            HasChildren: id is not null && value.Type?.IsString != true);
    }

    private static string Preview(ClrObject value)
    {
        try
        {
            var type = value.Type;
            if (type is null) return $"0x{value.Address:X}";
            if (type.IsString) return Quote(value.AsString(160) ?? "", 160);
            if (value.IsArray) return $"{MemoryLabels.ShortType(type.Name ?? "array")} · Length = {value.AsArray().Length:N0}";
            if (value.IsBoxedValue && type.IsPrimitive && FindField(type, "m_value") is { } boxed) return ReadScalar(boxed, value.Address, false);
            var name = type.Name ?? "";
            if (name.StartsWith("System.Collections.", StringComparison.Ordinal) &&
                (ReadInt(value, "_size") ?? ReadInt(value, "_count") ?? ReadInt(value, "count") ?? ReadInt(value, "m_count")) is int count)
                return $"{MemoryLabels.ShortType(name)} · Count = {count:N0}";
            if (value.IsDelegate) return $"{MemoryLabels.ShortType(name)} → {(value.AsDelegate().GetDelegateTarget()?.Method?.Name ?? "method")}";
            return "{" + MemoryLabels.ShortType(name) + "}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"0x{value.Address:X}"; }
    }

    private static string StructPreview(ClrValueType value)
    {
        var type = value.Type;
        if (type is null || !value.IsValid) return "{struct}";
        try
        {
            switch (type.Name)
            {
                case "System.DateTime":
                    {
                        var field = FindField(type, "_dateData") ?? FindField(type, "dateData");
                        if (field is null) break;
                        var data = field.Read<ulong>(value.Address, true);
                        var ticks = (long)(data & 0x3FFF_FFFF_FFFF_FFFF);
                        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) break;
                        var kind = (data >> 62) switch { 1 => " (UTC)", 2 or 3 => " (local)", _ => "" };
                        return new DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + kind;
                    }
                case "System.TimeSpan":
                    if (FindField(type, "_ticks") is { } span) return TimeSpan.FromTicks(span.Read<long>(value.Address, true)).ToString("c", CultureInfo.InvariantCulture);
                    break;
                case "System.Guid":
                    {
                        var a = FindField(type, "_a"); var b = FindField(type, "_b"); var c = FindField(type, "_c");
                        if (a is null || b is null || c is null) break;
                        var rest = new byte[8];
                        for (var i = 0; i < 8; i++)
                        {
                            var part = FindField(type, "_" + (char)('d' + i));
                            if (part is null) return "{Guid}";
                            rest[i] = part.Read<byte>(value.Address, true);
                        }
                        return new Guid(a.Read<int>(value.Address, true), b.Read<short>(value.Address, true), c.Read<short>(value.Address, true), rest).ToString();
                    }
            }
            var parts = new List<string>();
            foreach (var field in type.Fields.Take(4))
            {
                string text;
                if (field.IsObjectReference)
                {
                    var inner = field.ReadObject(value.Address, true);
                    text = inner.IsNull ? "null" : Preview(inner);
                }
                else if (field.ElementType == ClrElementType.Struct) text = "{…}";
                else text = ReadScalar(field, value.Address, true);
                parts.Add($"{Trim(field.Name ?? "?")} = {text}");
            }
            if (type.Fields.Length > 4) parts.Add("…");
            return parts.Count == 0 ? "{" + MemoryLabels.ShortType(type.Name ?? "struct") + "}" : "{ " + string.Join(", ", parts) + " }";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "{" + MemoryLabels.ShortType(type.Name ?? "struct") + "}"; }

        static string Trim(string field) => field.StartsWith('<') && field.IndexOf('>') is > 1 and var end ? field[1..end] : field.TrimStart('_');
    }

    private static ClrObject? ReadObject(ClrObject obj, string name)
    {
        var field = FindField(obj.Type, name);
        if (field is null || !field.IsObjectReference) return null;
        var value = field.ReadObject(obj.Address, false);
        return value.IsNull ? null : value;
    }

    private static int? ReadInt(ClrObject obj, string name)
    {
        var field = FindField(obj.Type, name);
        return field is { ElementType: ClrElementType.Int32 } ? field.Read<int>(obj.Address, false) : null;
    }

    private static string Quote(string text, int max)
    {
        var single = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return "\"" + (single.Length > max ? single[..max] + "…" : single) + "\"";
    }
}
