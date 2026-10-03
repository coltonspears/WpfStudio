using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

public readonly record struct HeapObject(ulong Address, int TypeId, long Size, string Generation);
public sealed record HeapType(string Key, string Name, string Module, bool IsDelegate = false);
public readonly record struct HeapEdge(int From, int To, int LabelId, bool IsDependent = false);
public sealed record HeapRoot(int ObjectId, string Label, string Kind, bool IsPinned = false, bool IsPermanent = false);

/// <summary>Immutable strong-reference graph. Dependent handles are key -> value edges, never GC roots.
/// Weak handles are deliberately excluded. All addresses and identities belong to one snapshot.</summary>
public sealed class HeapGraph
{
    public HeapGraph(HeapObject[] objects, HeapType[] types, HeapEdge[] edges, string[] labels,
        HeapRoot[] roots, bool isComplete = true)
    {
        Objects = objects; Types = types; Edges = edges; Labels = labels; Roots = roots; IsComplete = isComplete;
        foreach (var obj in objects)
            if ((uint)obj.TypeId >= types.Length || obj.Size < 0) throw new ArgumentException("Invalid heap object.");
        foreach (var edge in edges)
            if ((uint)edge.From >= objects.Length || (uint)edge.To >= objects.Length || (uint)edge.LabelId >= labels.Length)
                throw new ArgumentException("Invalid heap reference.");
        foreach (var root in roots)
            if ((uint)root.ObjectId >= objects.Length) throw new ArgumentException("Invalid heap root.");
        Outgoing = BuildIndex(objects.Length, edges, false);
        Incoming = BuildIndex(objects.Length, edges, true);
        RootIndexes = roots.Select((r, i) => (r.ObjectId, i)).GroupBy(x => x.ObjectId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray());
    }

    public HeapObject[] Objects { get; }
    public HeapType[] Types { get; }
    public HeapEdge[] Edges { get; }
    public string[] Labels { get; }
    public HeapRoot[] Roots { get; }
    public bool IsComplete { get; }
    public EdgeIndex Outgoing { get; }
    public EdgeIndex Incoming { get; }
    public IReadOnlyDictionary<int, int[]> RootIndexes { get; }

    private static EdgeIndex BuildIndex(int count, HeapEdge[] edges, bool incoming)
    {
        var offsets = new int[count + 1];
        foreach (var edge in edges) offsets[(incoming ? edge.To : edge.From) + 1]++;
        for (var i = 1; i <= count; i++) offsets[i] += offsets[i - 1];
        var cursor = (int[])offsets.Clone();
        var indexes = new int[edges.Length];
        for (var i = 0; i < edges.Length; i++) indexes[cursor[incoming ? edges[i].To : edges[i].From]++] = i;
        return new(offsets, indexes);
    }
}

public sealed record EdgeIndex(int[] Offsets, int[] Indexes)
{
    public ReadOnlySpan<int> For(int node) => Indexes.AsSpan(Offsets[node], Offsets[node + 1] - Offsets[node]);
}
