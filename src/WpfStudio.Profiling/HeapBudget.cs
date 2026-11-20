using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>How much of a heap one capture reads. A zero object or reference limit means none: the capture reads the whole
/// heap. Whatever the limits, the walk stops before the worker would run out of memory, because a worker that dies mid-capture
/// leaves the user with nothing, while one that stops early leaves an analysis marked incomplete.</summary>
internal sealed class HeapBudget
{
    // Estimated peak bytes per object and per reference, summed over the walk and the analysis: the lists, the address map,
    // the graph's indexes, reachability, dominators and their temporaries. Rounded up for headroom.
    internal const long BytesPerObject = 240, BytesPerReference = 40;
    private const long Gigabyte = 1L << 30;

    private HeapBudget(int objects, int references, int roots, long memoryBytes, bool explicitObjects, bool explicitReferences, bool is64Bit)
    {
        Objects = objects; References = references; Roots = roots; MemoryBytes = memoryBytes;
        _explicitObjects = explicitObjects; _explicitReferences = explicitReferences; _is64Bit = is64Bit;
    }

    private readonly bool _explicitObjects, _explicitReferences, _is64Bit;
    public int Objects { get; }
    public int References { get; }
    public int Roots { get; }
    /// <summary>The estimated memory the capture may use in this worker.</summary>
    public long MemoryBytes { get; }

    public static HeapBudget For(HeapCaptureRequest request) => For(request, AvailableMemoryBytes(), Environment.Is64BitProcess);

    internal static HeapBudget For(HeapCaptureRequest request, long availableMemoryBytes, bool is64Bit)
    {
        // Ids are ints and every per-object or per-reference table is one array, so neither count can pass Array.MaxLength.
        var arrayLimit = Array.MaxLength;
        if (request.MaxObjects is < 0 or (> 0 and < 1_000) || request.MaxReferences is < 0 or (> 0 and < 1_000))
            throw new ArgumentOutOfRangeException(nameof(request), "Heap limits must be zero (the whole heap) or at least 1,000 objects and 1,000 references.");
        var objects = request.MaxObjects == 0 ? arrayLimit : Math.Min(request.MaxObjects, arrayLimit);
        var references = request.MaxReferences == 0 ? arrayLimit : Math.Min(request.MaxReferences, arrayLimit);
        // An explicit reference budget also bounds the root list, as it always has; the whole heap keeps every root.
        var roots = request.MaxReferences == 0 ? arrayLimit : Math.Min(references, 1_000_000);
        // A 64-bit worker may use a little over half the machine's memory. A 32-bit worker has a 4 GB address space at best,
        // shared with the runtime, the DAC and the capture's page cache.
        var share = availableMemoryBytes <= 0 ? 8 * Gigabyte : availableMemoryBytes * 6 / 10;
        var memory = is64Bit ? Math.Max(share, 2 * Gigabyte) : Math.Min(share, 3 * Gigabyte / 2);
        return new(objects, references, roots, memory, request.MaxObjects != 0, request.MaxReferences != 0, is64Bit);
    }

    /// <summary>The same limits with a different memory allowance, so tests can reach the memory limit on a small heap.</summary>
    internal HeapBudget WithMemory(long memoryBytes) =>
        new(Objects, References, Roots, memoryBytes, _explicitObjects, _explicitReferences, _is64Bit);

    public bool Exceeds(long objects, long references) => objects * BytesPerObject + references * BytesPerReference > MemoryBytes;

    public string ObjectLimitNote() => _explicitObjects
        ? $"Object limit reached ({Objects:N0}). Raise the object budget in Options, or clear Limit analysis, to include the rest of the heap."
        : $"The heap has more objects than one analysis can index ({Objects:N0}); the rest were left out.";

    public string ReferenceLimitNote() => _explicitReferences
        ? $"Reference limit reached ({References:N0}). Raise the reference budget in Options, or clear Limit analysis, for full retention coverage."
        : $"The heap has more references than one analysis can index ({References:N0}); the rest were left out.";

    public string MemoryLimitNote() =>
        $"The heap is larger than this worker can analyze in memory (about {MemoryBytes / (double)Gigabyte:N1} GB); the rest of the heap was left out." +
        (_is64Bit ? " The worker uses at most about 60% of this machine's memory." : " 32-bit captures are analyzed by a 32-bit worker, which can address only a few gigabytes.");

    private static long AvailableMemoryBytes()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return 0; }
    }
}
