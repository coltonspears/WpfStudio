using System.Globalization;

namespace WpfStudio.Profiling;

/// <summary>GC generation of an object. Names match ClrMD's <c>Generation</c> and are the strings sent to the UI.</summary>
public enum HeapGeneration : byte { Generation0, Generation1, Generation2, Large, Pinned, Frozen, Unknown }

/// <param name="Generation">Stored as a byte so millions of objects carry no string per object.</param>
public readonly record struct HeapObject(ulong Address, int TypeId, long Size, HeapGeneration Generation = HeapGeneration.Generation2);
public sealed record HeapType(string Key, string Name, string Module, bool IsDelegate = false);
/// <param name="LabelId">An index into <see cref="HeapGraph.Labels"/>, or the bitwise complement of an array index, so array
/// slots carry no label string until one is displayed.</param>
public readonly record struct HeapEdge(int From, int To, int LabelId, bool IsDependent = false);
public sealed record HeapRoot(int ObjectId, string Label, string Kind, bool IsPinned = false, bool IsPermanent = false);

/// <summary>Immutable strong-reference graph. Dependent handles are key -> value edges, never GC roots.
/// Weak handles are deliberately excluded. All addresses and identities belong to one snapshot.</summary>
public sealed class HeapGraph
{
    private static readonly string[] GenerationNames = Enum.GetNames<HeapGeneration>();

    public HeapGraph(HeapObject[] objects, HeapType[] types, HeapEdge[] edges, string[] labels,
        HeapRoot[] roots, bool isComplete = true)
    {
        Objects = objects; Types = types; Edges = edges; Labels = labels; Roots = roots; IsComplete = isComplete;
        foreach (var obj in objects)
            if ((uint)obj.TypeId >= types.Length || obj.Size < 0) throw new ArgumentException("Invalid heap object.");
        foreach (var edge in edges)
            if ((uint)edge.From >= objects.Length || (uint)edge.To >= objects.Length || edge.LabelId >= labels.Length)
                throw new ArgumentException("Invalid heap reference.");
        foreach (var root in roots)
            if ((uint)root.ObjectId >= objects.Length) throw new ArgumentException("Invalid heap root.");
        Outgoing = BuildIndex(objects.Length, edges.Length, i => edges[i].From);
        Incoming = BuildIndex(objects.Length, edges.Length, i => edges[i].To);
        RootIndex = BuildIndex(objects.Length, roots.Length, i => roots[i].ObjectId);
    }

    public HeapObject[] Objects { get; }
    public HeapType[] Types { get; }
    public HeapEdge[] Edges { get; }
    public string[] Labels { get; }
    public HeapRoot[] Roots { get; }
    public bool IsComplete { get; }
    public EdgeIndex Outgoing { get; }
    public EdgeIndex Incoming { get; }
    /// <summary>Root indexes per object, in root order.</summary>
    public EdgeIndex RootIndex { get; }

    public ReadOnlySpan<int> RootsOf(int objectId) => RootIndex.For(objectId);
    public bool IsRooted(int objectId) => RootIndex.Offsets[objectId + 1] != RootIndex.Offsets[objectId];
    public string Label(int labelId) => labelId >= 0 ? Labels[labelId] : $"[{(~labelId).ToString(CultureInfo.InvariantCulture)}]";
    public static bool IsArrayIndexLabel(int labelId) => labelId < 0;
    public static string GenerationName(HeapGeneration generation) => GenerationNames[(int)generation];

    private static EdgeIndex BuildIndex(int count, int items, Func<int, int> key)
    {
        var offsets = new int[count + 1];
        for (var i = 0; i < items; i++) offsets[key(i) + 1]++;
        for (var i = 1; i <= count; i++) offsets[i] += offsets[i - 1];
        var cursor = (int[])offsets.Clone();
        var indexes = new int[items];
        for (var i = 0; i < items; i++) indexes[cursor[key(i)]++] = i;
        return new(offsets, indexes);
    }
}

public sealed record EdgeIndex(int[] Offsets, int[] Indexes)
{
    public ReadOnlySpan<int> For(int node) => Indexes.AsSpan(Offsets[node], Offsets[node + 1] - Offsets[node]);
}
