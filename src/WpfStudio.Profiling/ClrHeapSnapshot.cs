using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Runtime;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Owns one immutable OS snapshot or dump and its DAC. Must be accessed serially, in the profiling worker.</summary>
public sealed class ClrHeapSnapshot : IDisposable
{
    private readonly DataTarget _target;
    private readonly ClrRuntime _runtime;
    private readonly Dictionary<ulong, int> _addresses;
    public HeapAnalysis Analysis { get; }
    public HeapSummary Summary { get; }

    private ClrHeapSnapshot(DataTarget target, ClrRuntime runtime, Dictionary<ulong, int> addresses,
        HeapAnalysis analysis, HeapSummary summary)
    { _target = target; _runtime = runtime; _addresses = addresses; Analysis = analysis; Summary = summary; }

    public static ClrHeapSnapshot Open(HeapCaptureRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if ((request.DumpPath is null) == (request.ProcessId is null)) throw new ArgumentException("Choose either a dump or a running process.");
        if (request.MaxObjects is < 1_000 or > 5_000_000 || request.MaxReferences is < 1_000 or > 30_000_000)
            throw new ArgumentOutOfRangeException(nameof(request), "Heap limits must be between 1,000 and 5,000,000 objects, and 1,000 and 30,000,000 references.");
        var options = new DataTargetOptions
        {
            // Mapping an entire dump consumes scarce x86 virtual address space.
            UseLockFreeMemoryMapReader = Environment.Is64BitProcess,
            SymbolCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio", "Symbols"),
            Limits = new DataTargetLimits()
        };
        DataTarget target; string source;
        if (request.DumpPath is string path)
        {
            path = Path.GetFullPath(path);
            target = DataTarget.LoadDump(path, options); source = path;
        }
        else
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Live heap snapshots currently require Windows.");
            using var process = Process.GetProcessById(request.ProcessId!.Value);
            _ = process.Handle; // Retain the original process identity through capture.
            if (request.ProcessStartTimeUtcTicks is not long start || process.StartTime.ToUniversalTime().Ticks != start)
                throw new InvalidOperationException("The process exited or its identity changed. Refresh the process list and select it again.");
            source = $"{process.ProcessName} (PID {process.Id})";
            target = DataTarget.CreateSnapshotAndAttach(process.Id, options);
        }
        ClrRuntime? runtime = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (target.DataReader.Architecture != System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)
                throw new InvalidOperationException($"This capture is {target.DataReader.Architecture}. Use a profiling worker with the same architecture.");
            if (target.ClrVersions.Length == 0) throw new InvalidDataException("No supported CLR was found. Use a full managed-process dump containing the heap.");
            if ((uint)request.RuntimeIndex >= target.ClrVersions.Length) throw new ArgumentException("The requested CLR runtime index is not present in this capture.");
            var info = target.ClrVersions[request.RuntimeIndex];
            if (request.DumpPath is not null) ValidateDumpArchitecture(target, info);
            try { runtime = string.IsNullOrWhiteSpace(request.DacPath) ? info.CreateRuntime() : info.CreateRuntime(Path.GetFullPath(request.DacPath)); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                throw new InvalidDataException($"Could not load the matching DAC for {info.Flavor} {info.Version} ({target.DataReader.Architecture}). " +
                    "Use Options > Choose DAC to select the matching runtime's mscordacwks.dll or mscordaccore.dll. " +
                    ex.Message, ex);
            }
            var heap = runtime.Heap;
            if (!heap.CanWalkHeap) throw new InvalidDataException("The CLR heap is not walkable in this capture. Capture a full dump when the runtime is in a consistent state.");
            var notes = new HashSet<string>(StringComparer.Ordinal); var complete = true;
            if (target.ClrVersions.Length > 1)
                notes.Add($"This capture contains {target.ClrVersions.Length} runtimes. The analysis covers runtime index {request.RuntimeIndex} only.");
            var types = new List<HeapType>(); var typeIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var objects = new List<HeapObject>(); var addresses = new Dictionary<ulong, int>(); long freeBytes = 0;
            foreach (var obj in heap.EnumerateObjects(carefully: true))
            {
                token.ThrowIfCancellationRequested();
                if (!obj.IsValid) { Incomplete("Some object headers are missing or invalid."); continue; }
                if (obj.IsFree) { freeBytes = checked(freeBytes + (long)obj.Size); continue; }
                if (objects.Count >= request.MaxObjects) { Incomplete($"Object limit reached ({request.MaxObjects:N0}). Increase the analysis budget to include the rest of the heap."); break; }
                var type = obj.Type!; var name = type.Name ?? "<unknown type>";
                var module = Path.GetFileName(type.Module.Name ?? "<unknown module>"); var key = module + "|" + name;
                if (!typeIds.TryGetValue(key, out var typeId)) { typeId = types.Count; typeIds.Add(key, typeId); types.Add(new(key, name, module, obj.IsDelegate)); }
                addresses.Add(obj.Address, objects.Count);
                objects.Add(new(obj.Address, typeId, checked((long)obj.Size), heap.GetSegmentByAddress(obj.Address)?.GetGeneration(obj.Address).ToString() ?? "Unknown"));
                if (heap.IsObjectCorrupted(obj.Address, out _)) Incomplete("Missing or inconsistent object/reference data was detected. Use a full dump for reliable retention estimates.");
            }
            if (objects.Count == 0) notes.Add("No allocated managed objects were enumerated.");
            var staticNames = ReadStaticNames(runtime, token, notes);
            var labels = new List<string>(); var labelIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var edges = new List<HeapEdge>();
            for (var i = 0; i < objects.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (edges.Count >= request.MaxReferences) { Incomplete($"Reference limit reached ({request.MaxReferences:N0}). Increase the analysis budget for full retention coverage."); break; }
                var obj = heap.GetObject(objects[i].Address);
                foreach (var reference in obj.EnumerateReferencesWithFields(carefully: true, considerDependantHandles: true))
                {
                    if (edges.Count >= request.MaxReferences) { Incomplete($"Reference limit reached ({request.MaxReferences:N0}). Increase the analysis budget for full retention coverage."); break; }
                    if (!addresses.TryGetValue(reference.Object.Address, out var to))
                    { if (reference.Object.Address != 0) Incomplete("Some referenced objects were outside the enumerated heap."); continue; }
                    var slot = reference.Offset >= 0 ? obj.Address + (ulong)target.DataReader.PointerSize + (ulong)reference.Offset : 0;
                    var label = staticNames.TryGetValue(slot, out var staticName) ? staticName : ReferenceLabel(reference);
                    if (!labelIds.TryGetValue(label, out var labelId)) { labelId = labels.Count; labelIds.Add(label, labelId); labels.Add(label); }
                    edges.Add(new(i, to, labelId, reference.IsDependentHandle));
                }
            }
            var roots = new List<HeapRoot>();
            for (var i = 0; i < objects.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (objects[i].Generation != nameof(Generation.Frozen)) continue;
                if (roots.Count >= Math.Min(request.MaxReferences, 1_000_000)) { Incomplete("The frozen-object root analysis budget was reached."); break; }
                // Frozen segments are intrinsically alive; ClrMD's handle/stack root list excludes them.
                roots.Add(new(i, $"Frozen segment @ 0x{objects[i].Address:X}", "Frozen segment", IsPermanent: true));
            }
            var runtimeRootCount = 0;
            foreach (var root in heap.EnumerateRoots())
            {
                token.ThrowIfCancellationRequested();
                if (roots.Count >= Math.Min(request.MaxReferences, 1_000_000)) { Incomplete("The GC-root analysis budget was reached."); break; }
                if (root.Object.Address == 0) continue;
                if (!addresses.TryGetValue(root.Object.Address, out var objectId)) { Incomplete("Some GC roots point outside the enumerated heap."); continue; }
                var label = staticNames.TryGetValue(root.Address, out var name) ? name : root is ClrStackRoot stack
                    ? $"Stack: {stack.StackFrame?.ToString() ?? "managed frame"} (slot 0x{root.Address:X})"
                    : $"{root.RootKind} (slot 0x{root.Address:X})";
                roots.Add(new(objectId, label, root.RootKind.ToString(), root.IsPinned));
                runtimeRootCount++;
            }
            if (runtimeRootCount == 0 && objects.Any(o => o.Generation != nameof(Generation.Frozen)))
                Incomplete("No runtime GC roots were available. This capture cannot establish collection eligibility for ordinary heap objects.");
            var graph = new HeapGraph(objects.ToArray(), types.ToArray(), edges.ToArray(), labels.ToArray(), roots.ToArray(), complete);
            var analysis = new HeapAnalysis(graph, token);
            long reachableBytes = 0, unreachableBytes = 0;
            for (var i = 0; i < objects.Count; i++)
                if (analysis.Reachable[i]) reachableBytes = checked(reachableBytes + objects[i].Size);
                else unreachableBytes = checked(unreachableBytes + objects[i].Size);
            var family = info.Flavor == ClrFlavor.Desktop ? ".NET Framework" : ".NET";
            var summary = new HeapSummary(source, $"{family} {info.Version}", target.DataReader.Architecture.ToString(),
                DateTimeOffset.UtcNow, objects.Count, checked(reachableBytes + unreachableBytes), reachableBytes, unreachableBytes,
                freeBytes, edges.Count, roots.Count, complete, notes.ToArray(), analysis.SummarizeTypes());
            return new(target, runtime, addresses, analysis, summary);

            void Incomplete(string note) { complete = false; notes.Add(note); }
        }
        catch { runtime?.Dispose(); target.Dispose(); throw; }
    }

    private static void ValidateDumpArchitecture(DataTarget target, ClrInfo info)
    {
        var reader = target.DataReader; var image = info.ModuleInfo.ImageBase;
        // Native WOW64 dumps describe the 64-bit subsystem, even when the CLR is 32-bit.
        // Their thread contexts cannot be read as x86 contexts by ClrMD's dump reader.
        if (reader.Architecture == System.Runtime.InteropServices.Architecture.X64 &&
            reader.Read<ushort>(image) == 0x5a4d && reader.Read<int>(image + 0x3c, out var offset) &&
            offset is > 0 and <= 1_000_000 && reader.Read<uint>(image + (uint)offset) == 0x4550 &&
            reader.Read<ushort>(image + (uint)offset + 4) == 0x14c)
            throw new InvalidDataException("This is a native 64-bit WOW64 dump of a 32-bit CLR. " +
                "Capture a 32-bit full dump with ProcDump -ma (without -64), or capture a live snapshot in WpfStudio. " +
                "Native WOW64 subsystem dumps are not supported by this heap reader.");
    }

    private static Dictionary<ulong, string> ReadStaticNames(ClrRuntime runtime, CancellationToken token, HashSet<string> notes)
    {
        var names = new Dictionary<ulong, string>(); var visited = new HashSet<ulong>(); var inspected = 0;
        try
        {
            foreach (var module in runtime.EnumerateModules())
                foreach (var map in module.EnumerateTypeDefToMethodTableMap())
                {
                    token.ThrowIfCancellationRequested();
                    if (!visited.Add(map.MethodTable)) continue;
                    if (++inspected > 250_000) { notes.Add("Static-field names were limited to 250,000 loaded types; GC-root analysis still uses the runtime's root enumeration."); return names; }
                    var type = runtime.Heap.GetTypeByMethodTable(map.MethodTable);
                    if (type is null) continue;
                    foreach (var field in type.StaticFields.Where(f => f.IsObjectReference))
                        foreach (var domain in runtime.AppDomains)
                        {
                            var address = field.GetAddress(domain);
                            if (address != 0) names.TryAdd(address, $"Static {type.Name}.{field.Name}");
                        }
                }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        { notes.Add("Some static-field names could not be resolved. GC-root paths still show the underlying runtime slots."); }
        return names;
    }

    private static string ReferenceLabel(ClrReference reference)
    {
        if (reference.IsDependentHandle) return "Dependent handle: value retained while key is alive";
        if (reference.Field is null) return $"Array/reference slot +0x{reference.Offset:X}";
        var names = new List<string> { reference.Field.Name ?? "<field>" }; var inner = reference.InnerField;
        while (inner is ClrReference child && names.Count < 8)
        { names.Add(child.Field?.Name ?? "<field>"); inner = child.InnerField; }
        return string.Join(".", names);
    }

    public MemoryObjectDetails Inspect(int id, CancellationToken token = default)
    {
        var description = Analysis.Describe(id); var address = Convert.ToUInt64(description.Address[2..], 16);
        var obj = _runtime.Heap.GetObject(address); var fields = new List<MemoryFieldInfo>(); var preview = description.Type;
        if (obj.IsArray) preview = $"{description.Type} · {obj.AsArray().Length:N0} elements";
        else if (obj.Type?.IsString == true) preview = '"' + (obj.AsString(256) ?? "<unreadable>") + '"';
        if (obj.IsDelegate)
            foreach (var target in obj.AsDelegate().EnumerateDelegateTargets().Take(20))
                fields.Add(new("Delegate target", target.TargetObject.Type?.Name ?? "static", target.Method?.Signature ?? "<unknown method>",
                    _addresses.TryGetValue(target.TargetObject.Address, out var targetId) ? targetId : null));
        var allFields = obj.Type?.Fields ?? [];
        foreach (var field in allFields.Take(128))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (field.IsObjectReference)
                {
                    var value = field.ReadObject(obj.Address, false);
                    var text = value.IsNull ? "null" : value.Type?.IsString == true ? '"' + (value.AsString(200) ?? "<unreadable>") + '"' : $"{value.Type?.Name} @ 0x{value.Address:X}";
                    fields.Add(new(field.Name ?? "<field>", field.Type?.Name ?? field.ElementType.ToString(), text,
                        _addresses.TryGetValue(value.Address, out var childId) ? childId : null));
                }
                else fields.Add(new(field.Name ?? "<field>", field.Type?.Name ?? field.ElementType.ToString(), ReadScalar(field, obj.Address)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            { fields.Add(new(field.Name ?? "<field>", field.ElementType.ToString(), "<unreadable in this capture>")); }
        }
        return Analysis.Inspect(id, preview, fields, allFields.Length > 128, token);
    }

    private static string ReadScalar(ClrInstanceField field, ulong address) => field.ElementType switch
    {
        ClrElementType.Boolean => field.Read<bool>(address, false).ToString(),
        ClrElementType.Char => "'" + field.Read<char>(address, false) + "'",
        ClrElementType.Int8 => field.Read<sbyte>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => field.Read<byte>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int16 => field.Read<short>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => field.Read<ushort>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int32 => field.Read<int>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => field.Read<uint>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int64 => field.Read<long>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => field.Read<ulong>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Float => field.Read<float>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Double => field.Read<double>(address, false).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeInt => "0x" + field.Read<nint>(address, false).ToString("X", CultureInfo.InvariantCulture),
        ClrElementType.NativeUInt => "0x" + field.Read<nuint>(address, false).ToString("X", CultureInfo.InvariantCulture),
        _ => "<inline value; reference members appear in outgoing references>"
    };

    public void Dispose() { _runtime.Dispose(); _target.Dispose(); }
}
