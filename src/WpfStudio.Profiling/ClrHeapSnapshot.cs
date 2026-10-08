using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Owns one immutable OS snapshot or dump and its DAC. Must be accessed serially, in the profiling worker.</summary>
public sealed partial class ClrHeapSnapshot : IDisposable
{
    private static readonly string[] DisposedFieldNames =
        ["_disposed", "disposed", "_isDisposed", "isDisposed", "m_disposed", "m_isDisposed", "_disposedValue", "disposedValue", "<IsDisposed>k__BackingField", "<Disposed>k__BackingField"];
    private readonly DataTarget _target;
    private readonly ClrRuntime _runtime;
    private readonly Dictionary<ulong, int> _addresses;
    public HeapAnalysis Analysis { get; }
    public HeapSummary Summary { get; }
    /// <summary>Wall-clock milliseconds per load stage, in order, for diagnosing slow captures.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> LoadTimings { get; private init; } = [];

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
            var snapshot = DataTarget.CreateSnapshotAndAttach(process.Id, options);
            // The snapshot is immutable, so its memory is read in cached pages instead of one system call per read.
            try { target = new DataTarget(new CachingDataReader(snapshot, Environment.Is64BitProcess ? 512L << 20 : 96L << 20), options); }
            catch { snapshot.Dispose(); throw; }
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
            var timings = new List<KeyValuePair<string, double>>(); var clock = Stopwatch.StartNew();
            void Stage(string name) { timings.Add(new(name, clock.Elapsed.TotalMilliseconds)); clock.Restart(); }
            Stage("Open runtime");
            if (!heap.CanWalkHeap) throw new InvalidDataException("The CLR heap is not walkable in this capture. Capture a full dump when the runtime is in a consistent state.");
            var notes = new HashSet<string>(StringComparer.Ordinal); var complete = true;
            if (target.ClrVersions.Length > 1)
                notes.Add($"This capture contains {target.ClrVersions.Length} runtimes. The analysis covers runtime index {request.RuntimeIndex} only.");
            var types = new List<HeapType>(); var typeIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeByMethodTable = new Dictionary<ulong, int>();
            var objects = new List<HeapObject>(); var addresses = new Dictionary<ulong, int>();
            var extras = new HeapExtras { PointerSize = target.DataReader.PointerSize };
            var probes = new Dictionary<ulong, TypeProbe>();
            long stringChars = 0; var stringsScanned = 0;
            var pointerSize = target.DataReader.PointerSize; var reader = target.DataReader;
            // Static names label reference slots while the heap is walked, so they are resolved first.
            var staticNames = ReadStaticNames(runtime, token, notes);
            var dependents = ReadDependentHandles(runtime, notes);
            Stage("Static names and handles");
            var labels = new List<string>(); var labelIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var fieldLabels = new Dictionary<(ulong MethodTable, int Offset), int>();
            // Targets are resolved after the walk: a reference can point at an object that has not been enumerated yet.
            var edges = new List<HeapEdge>(); var edgeTargets = new List<ulong>();
            var buffer = new byte[64 * 1024]; var referencesFull = false;
            var stringBuffer = new byte[4096]; var stringLookup = extras.Strings.GetAlternateLookup<ReadOnlySpan<char>>();
            ClrSegment? segment = null;
            foreach (var obj in heap.EnumerateObjects(carefully: true))
            {
                token.ThrowIfCancellationRequested();
                if (!obj.IsValid) { Incomplete("Some object headers are missing or invalid."); continue; }
                if (segment is null || !segment.ObjectRange.Contains(obj.Address)) segment = heap.GetSegmentByAddress(obj.Address);
                if (obj.IsFree)
                {
                    extras.FreeBytes = checked(extras.FreeBytes + (long)obj.Size);
                    if (segment?.GetGeneration(obj.Address) == Generation.Large) extras.LargeFreeBytes += (long)obj.Size;
                    continue;
                }
                if (objects.Count >= request.MaxObjects) { Incomplete($"Object limit reached ({request.MaxObjects:N0}). Increase the analysis budget to include the rest of the heap."); break; }
                var type = obj.Type!;
                if (!typeByMethodTable.TryGetValue(type.MethodTable, out var typeId))
                {
                    var name = type.Name ?? "<unknown type>";
                    var module = Path.GetFileName(type.Module.Name ?? "<unknown module>"); var key = module + "|" + name;
                    // Distinct method tables can share a name (an assembly loaded twice); they stay one type, as before.
                    if (!typeIds.TryGetValue(key, out typeId)) { typeId = types.Count; typeIds.Add(key, typeId); types.Add(new(key, name, module, obj.IsDelegate)); }
                    typeByMethodTable.Add(type.MethodTable, typeId);
                }
                var id = objects.Count;
                addresses.Add(obj.Address, id);
                objects.Add(new(obj.Address, typeId, checked((long)obj.Size), segment is null ? HeapGeneration.Unknown : ToGeneration(segment.GetGeneration(obj.Address))));
                if (!probes.TryGetValue(type.MethodTable, out var probe)) probes[type.MethodTable] = probe = TypeProbe.For(type, pointerSize);
                try { Observe(obj, id, probe); }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { }
                var readable = referencesFull || ReadReferences(obj, type, id);
                if (!readable || heap.IsObjectCorrupted(obj.Address, out _))
                    Incomplete("Missing or inconsistent object/reference data was detected. Use a full dump for reliable retention estimates.");
            }
            if (objects.Count == 0) notes.Add("No allocated managed objects were enumerated.");
            Stage("Objects and references");
            // Resolve and compact: references outside the enumerated heap are dropped.
            var kept = 0;
            for (var i = 0; i < edges.Count; i++)
            {
                if (!addresses.TryGetValue(edgeTargets[i], out var to)) { Incomplete("Some referenced objects were outside the enumerated heap."); continue; }
                edges[kept++] = edges[i] with { To = to };
            }
            edges.RemoveRange(kept, edges.Count - kept);
            edgeTargets.Clear(); edgeTargets.TrimExcess();
            Stage("Resolve references");
            var roots = new List<HeapRoot>();
            for (var i = 0; i < objects.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (objects[i].Generation != HeapGeneration.Frozen) continue;
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
            if (runtimeRootCount == 0 && objects.Exists(o => o.Generation != HeapGeneration.Frozen))
                Incomplete("No runtime GC roots were available. This capture cannot establish collection eligibility for ordinary heap objects.");
            if (extras.StringsTruncated) notes.Add("Duplicate-string detection compared a bounded sample of strings.");
            Stage("Roots");
            var graph = new HeapGraph(objects.ToArray(), types.ToArray(), edges.ToArray(), labels.ToArray(), roots.ToArray(), complete);
            Stage("Graph");
            var analysis = new HeapAnalysis(graph, token);
            timings.AddRange(analysis.Timings); clock.Restart();
            long reachableBytes = 0, unreachableBytes = 0;
            for (var i = 0; i < objects.Count; i++)
                if (analysis.Reachable[i]) reachableBytes = checked(reachableBytes + objects[i].Size);
                else unreachableBytes = checked(unreachableBytes + objects[i].Size);
            var insights = HeapInsights.Compute(graph, analysis, extras, token);
            Stage("Findings");
            var family = info.Flavor == ClrFlavor.Desktop ? ".NET Framework" : ".NET";
            var summary = new HeapSummary(source, $"{family} {info.Version}", target.DataReader.Architecture.ToString(),
                DateTimeOffset.UtcNow, objects.Count, checked(reachableBytes + unreachableBytes), reachableBytes, unreachableBytes,
                extras.FreeBytes, edges.Count, roots.Count, complete, notes.ToArray(), analysis.SummarizeTypes(),
                analysis.SummarizeGenerations(), analysis.SummarizeRootKinds(), insights, analysis.GetTopRetainers());
            Stage("Summaries");
            return new(target, runtime, addresses, analysis, summary) { LoadTimings = timings };

            void Incomplete(string note) { complete = false; notes.Add(note); }

            void Observe(ClrObject obj, int id, TypeProbe probe)
            {
                if (probe.IsString)
                {
                    if (obj.Size > 4096 || stringsScanned >= 2_000_000 || stringChars > 64_000_000 || extras.Strings.Count >= 500_000)
                    { if (obj.Size > 4096 || extras.Strings.Count >= 500_000 || stringsScanned >= 2_000_000) extras.StringsTruncated = true; return; }
                    // Compare the captured characters in place; only the first copy of each value becomes a string.
                    var size = (int)obj.Size; var header = pointerSize + sizeof(int);
                    if (size < header || reader.Read(obj.Address, stringBuffer.AsSpan(0, size)) != size) return;
                    var length = BitConverter.ToInt32(stringBuffer, pointerSize);
                    if (length < 0 || header + 2L * length > size) return;
                    var text = MemoryMarshal.Cast<byte, char>(stringBuffer.AsSpan(header, 2 * Math.Min(length, 2048)));
                    stringsScanned++; stringChars += text.Length;
                    if (!stringLookup.TryGetValue(text, out var group)) extras.Strings[new string(text)] = group = new() { Sample = id };
                    group.Count++; group.Bytes += (long)obj.Size;
                }
                else if (probe.IsReferenceArray)
                {
                    var length = obj.AsArray().Length;
                    if (length >= 32) extras.ReferenceArrayLengths[id] = length;
                }
                else if (probe.Disposed is { } field && field.Read<bool>(obj.Address, false))
                {
                    extras.Disposed.Add(id);
                    if (probe.IsWindow) extras.ClosedWindows.Add(id);
                }
            }

            // Reads one object's references with its GC descriptor over a single memory read, falling back to ClrMD's
            // field enumeration for very large objects. Returns false when the object's memory could not be read.
            bool ReadReferences(ClrObject obj, ClrType type, int from)
            {
                if (type.ContainsPointers)
                {
                    var size = obj.Size;
                    if (size <= MaxDirectReadBytes)
                    {
                        if ((int)size > buffer.Length) buffer = new byte[Math.Max((int)size, buffer.Length * 2)];
                        if (reader.Read(obj.Address, buffer.AsSpan(0, (int)size)) != (int)size) return false;
                        foreach (var (address, offset) in type.GCDesc.WalkObject(buffer, (int)size))
                            if (address != 0 && !Add(from, address, LabelFor(obj.Address, type, offset), false)) return true;
                    }
                    else
                    {
                        try
                        {
                            foreach (var reference in obj.EnumerateReferencesWithFields(carefully: true, considerDependantHandles: false))
                            {
                                if (reference.Object.Address == 0) continue;
                                var label = reference.Offset >= 0 && staticNames.TryGetValue(obj.Address + (ulong)pointerSize + (ulong)reference.Offset, out var staticName)
                                    ? Intern(staticName) : Intern(ReferenceLabel(reference, obj.IsArray, pointerSize));
                                if (!Add(from, reference.Object.Address, label, false)) return true;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { return false; }
                    }
                }
                if (dependents.TryGetValue(obj.Address, out var values))
                    foreach (var value in values)
                        if (!Add(from, value, Intern(DependentHandleLabel), true)) return true;
                return true;
            }

            bool Add(int from, ulong to, int label, bool dependent)
            {
                if (edges.Count >= request.MaxReferences)
                {
                    referencesFull = true;
                    Incomplete($"Reference limit reached ({request.MaxReferences:N0}). Increase the analysis budget for full retention coverage.");
                    return false;
                }
                edges.Add(new(from, -1, label, dependent)); edgeTargets.Add(to);
                return true;
            }

            // Labels match ReferenceLabel. GC-descriptor offsets include the method table; ClrMD reference offsets do not.
            int LabelFor(ulong address, ClrType type, int offset)
            {
                if (staticNames.Count != 0 && staticNames.TryGetValue(address + (ulong)offset, out var staticName)) return Intern(staticName);
                var dataOffset = offset - pointerSize;
                if (type.IsArray)
                    // Array slots carry their index instead of a string; elements start after the (pointer-sized) length.
                    return dataOffset >= pointerSize && (dataOffset - pointerSize) % pointerSize == 0
                        ? ~((dataOffset - pointerSize) / pointerSize)
                        : Intern($"Array/reference slot +0x{dataOffset:X}");
                if (fieldLabels.TryGetValue((type.MethodTable, offset), out var cached)) return cached;
                var label = Intern(ReferenceLabel(ClrReference.CreateFromFieldOrArray(default, type, dataOffset), false, pointerSize));
                fieldLabels.Add((type.MethodTable, offset), label);
                return label;
            }

            int Intern(string label)
            {
                if (!labelIds.TryGetValue(label, out var labelId)) { labelId = labels.Count; labelIds.Add(label, labelId); labels.Add(label); }
                return labelId;
            }
        }
        catch { runtime?.Dispose(); DisposeTarget(target); throw; }
    }

    private const int MaxDirectReadBytes = 8 << 20;
    private const string DependentHandleLabel = "Dependent handle: value retained while key is alive";

    private static HeapGeneration ToGeneration(Generation generation) => generation switch
    {
        Generation.Generation0 => HeapGeneration.Generation0,
        Generation.Generation1 => HeapGeneration.Generation1,
        Generation.Generation2 => HeapGeneration.Generation2,
        Generation.Large => HeapGeneration.Large,
        Generation.Pinned => HeapGeneration.Pinned,
        Generation.Frozen => HeapGeneration.Frozen,
        _ => HeapGeneration.Unknown
    };

    /// <summary>Dependent handles keep their value alive while the key (primary) is alive: key address -> values.</summary>
    private static Dictionary<ulong, List<ulong>> ReadDependentHandles(ClrRuntime runtime, HashSet<string> notes)
    {
        var result = new Dictionary<ulong, List<ulong>>();
        try
        {
            foreach (var handle in runtime.EnumerateHandles())
            {
                if (handle.HandleKind != ClrHandleKind.Dependent || handle.Object.Address == 0 || handle.Dependent.Address == 0) continue;
                if (!result.TryGetValue(handle.Object.Address, out var values)) result[handle.Object.Address] = values = [];
                values.Add(handle.Dependent.Address);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        { notes.Add("Some dependent handles could not be read. Values kept alive by ConditionalWeakTable may appear unrooted."); }
        return result;
    }

    private sealed class TypeProbe
    {
        public ClrInstanceField? Disposed { get; private init; }
        public bool IsWindow { get; private init; }
        public bool IsString { get; private init; }
        public bool IsReferenceArray { get; private init; }

        public static TypeProbe For(ClrType type, int pointerSize)
        {
            if (type.IsString) return new() { IsString = true };
            if (type.IsArray)
                return new() { IsReferenceArray = type.ComponentType?.IsObjectReference ?? (type.ContainsPointers && type.ComponentSize == pointerSize) };
            ClrInstanceField? disposed = null;
            foreach (var name in DisposedFieldNames)
                if (FindField(type, name) is { ElementType: ClrElementType.Boolean } field) { disposed = field; break; }
            var window = false;
            for (var t = type; t is not null && !window; t = t.BaseType) window = t.Name == "System.Windows.Window";
            return new() { Disposed = disposed, IsWindow = window };
        }
    }

    internal static ClrInstanceField? FindField(ClrType? type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.GetFieldByName(name) is { } field) return field;
        return null;
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

    private static string ReferenceLabel(ClrReference reference, bool isArray, int pointerSize)
    {
        if (reference.IsDependentHandle) return DependentHandleLabel;
        // Reference-array elements start after the method table and the (pointer-sized) length.
        if (reference.Field is null && isArray && reference.Offset >= pointerSize && (reference.Offset - pointerSize) % pointerSize == 0)
            return $"[{(reference.Offset - pointerSize) / pointerSize}]";
        if (reference.Field is null) return $"Array/reference slot +0x{reference.Offset:X}";
        var names = new List<string> { reference.Field.Name ?? "<field>" }; var inner = reference.InnerField;
        while (inner is ClrReference child && names.Count < 8)
        { names.Add(child.Field?.Name ?? "<field>"); inner = child.InnerField; }
        return string.Join(".", names);
    }

    public MemoryObjectDetails Inspect(int id, CancellationToken token = default)
    {
        var description = Analysis.Describe(id); var address = Analysis.AddressOf(id);
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
                else fields.Add(new(field.Name ?? "<field>", field.Type?.Name ?? field.ElementType.ToString(), ReadScalar(field, obj.Address, false)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            { fields.Add(new(field.Name ?? "<field>", field.ElementType.ToString(), "<unreadable in this capture>")); }
        }
        return Analysis.Inspect(id, preview, fields, allFields.Length > 128, token);
    }

    private static string ReadScalar(ClrInstanceField field, ulong address, bool interior)
    {
        var text = field.ElementType switch
        {
            ClrElementType.Boolean => field.Read<bool>(address, interior) ? "true" : "false",
            ClrElementType.Char => "'" + field.Read<char>(address, interior) + "'",
            ClrElementType.Int8 => field.Read<sbyte>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt8 => field.Read<byte>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int16 => field.Read<short>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt16 => field.Read<ushort>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int32 => field.Read<int>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt32 => field.Read<uint>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int64 => field.Read<long>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt64 => field.Read<ulong>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Float => field.Read<float>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.Double => field.Read<double>(address, interior).ToString(CultureInfo.InvariantCulture),
            ClrElementType.NativeInt or ClrElementType.Pointer or ClrElementType.FunctionPointer => "0x" + field.Read<nint>(address, interior).ToString("X", CultureInfo.InvariantCulture),
            ClrElementType.NativeUInt => "0x" + field.Read<nuint>(address, interior).ToString("X", CultureInfo.InvariantCulture),
            _ => "<inline value; reference members appear in outgoing references>"
        };
        return field.Type?.IsEnum == true ? EnumName(field.Type, text) : text;
    }

    private static string EnumName(ClrType type, string numeric)
    {
        try
        {
            if (!long.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return numeric;
            foreach (var (name, raw) in type.AsEnum().EnumerateValues())
                if (raw is not null && Convert.ToInt64(raw, CultureInfo.InvariantCulture) == value) return $"{name} ({numeric})";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return numeric;
    }

    public void Dispose() { _runtime.Dispose(); DisposeTarget(_target); }

    /// <summary>Also releases a caching reader's snapshot, whether or not the data target disposes its reader.</summary>
    private static void DisposeTarget(DataTarget target) { var reader = target.DataReader; target.Dispose(); (reader as IDisposable)?.Dispose(); }
}
