using WpfStudio.Contracts.Profiling;
using WpfStudio.Profiling;

namespace WpfStudio.Profiling.Tests;

public sealed class HeapAnalysisTests
{
    [Fact]
    public void SharedChildSurvivesRemovalOfOneOwner()
    {
        // Two rooted owners share one child. Only owner's exclusively owned subtree is retained.
        var analysis = Analyze([10, 20, 100, 40], [(0, 2), (1, 2), (0, 3)], [0, 1]);
        Assert.Equal(new long[] { 50, 20, 100, 40 }, analysis.RetainedBytes);
        var release = analysis.EstimateRelease(new(0));
        Assert.Equal(50, release.ReclaimableBytes);
        Assert.Equal(2, release.ReclaimableObjects);
        Assert.DoesNotContain(2, release.ObjectSample);
        Assert.False(release.SelectedObjectRemainsReachable);
    }

    [Fact]
    public void ParallelReferencesAreRemovedIndividually()
    {
        var analysis = Analyze([10, 100], [(0, 1), (0, 1)], [0]);
        var release = analysis.EstimateRelease(new(1, ReferenceId: 0));
        Assert.Equal(0, release.ReclaimableBytes);
        Assert.True(release.SelectedObjectRemainsReachable);
        Assert.Contains(release.RemainingRootPath!.References, r => r.Id == 1);
        Assert.Equal(100, analysis.EstimateRelease(new(1)).ReclaimableBytes);
    }

    [Fact]
    public void RemovingOneRootDoesNotRemoveAnotherRootToSameObject()
    {
        var analysis = Analyze([10, 100], [(0, 1)], [0, 0]);
        var release = analysis.EstimateRelease(new(0, ReferenceId: 1));
        Assert.Equal(0, release.ReclaimableBytes);
        Assert.True(release.SelectedObjectRemainsReachable);
        Assert.Equal(2, release.RemainingRootPath!.References[0].Id);
    }

    [Fact]
    public void UnrootedCycleIsAlreadyCollectibleAndNotCountedAsNewlyReleased()
    {
        var analysis = Analyze([10, 100, 200], [(1, 2), (2, 1)], [0]);
        Assert.Equal(new[] { true, false, false }, analysis.Reachable);
        Assert.Equal(0, analysis.RetainedBytes[1]);
        Assert.Equal(0, analysis.EstimateRelease(new(1)).ReclaimableBytes);
        Assert.Empty(analysis.GetRootPaths(1).Paths);
    }

    [Fact]
    public void RootedCycleIsReclaimedTogetherWhenLastRootIsRemoved()
    {
        var analysis = Analyze([10, 100, 200], [(0, 1), (1, 2), (2, 1)], [0]);
        Assert.Equal(new long[] { 310, 300, 200 }, analysis.RetainedBytes);
        var release = analysis.EstimateRelease(new(1, ReferenceId: 0));
        Assert.Equal(300, release.ReclaimableBytes);
        Assert.Equal(2, release.ReclaimableObjects);
    }

    [Fact]
    public void DependentHandleValueLivesOnlyWhileItsKeyIsRooted()
    {
        var graph = new HeapGraph([new(1, 0, 10, "Gen2"), new(2, 0, 100, "Gen2")],
            [new("T", "T", "fixture")], [new(0, 1, 0, IsDependent: true)], ["dependent"], [new(0, "key", "Strong")]);
        var analysis = new HeapAnalysis(graph);
        Assert.Equal(110, analysis.EstimateRelease(new(0)).ReclaimableBytes);
        var unrooted = new HeapAnalysis(new HeapGraph(graph.Objects, graph.Types, graph.Edges, graph.Labels, []));
        Assert.False(unrooted.Reachable[1]);
    }

    [Fact]
    public void FrozenObjectsRemainRootedWhenOwnersAreRemoved()
    {
        var graph = new HeapGraph([new(1, 0, 10, "Gen2"), new(2, 0, 100, "Frozen"), new(3, 0, 20, "Gen2"), new(4, 0, 200, "Gen2")],
            [new("T", "T", "fixture")], [new(0, 1, 0), new(0, 2, 0), new(1, 3, 0)], ["field"],
            [new(0, "owner", "Strong"), new(1, "frozen", "Frozen segment", IsPermanent: true)]);
        var analysis = new HeapAnalysis(graph);
        Assert.Equal(30, analysis.RetainedBytes[0]); Assert.Equal(0, analysis.RetainedBytes[1]);
        Assert.Equal(30, analysis.EstimateRelease(new(0)).ReclaimableBytes);
        var frozen = analysis.EstimateRelease(new(1));
        Assert.True(frozen.SelectedObjectRemainsReachable); Assert.Equal(0, frozen.ReclaimableBytes);
        Assert.True(frozen.RemainingRootPath!.References[0].IsPermanent);
        Assert.Equal(0, analysis.RetainedBytes[3]);
        Assert.True(analysis.EstimateRelease(new(3)).SelectedObjectRemainsReachable);
        Assert.Throws<ArgumentException>(() => analysis.EstimateRelease(new(1, ReferenceId: 4)));
        Assert.Throws<ArgumentException>(() => analysis.EstimateRelease(new(3, ReferenceId: 2)));
    }

    [Fact]
    public void IncompleteCoverageNeverProducesAnUnqualifiedEstimate()
    {
        var analysis = Analyze([10, 100], [(0, 1)], [0], complete: false);
        var release = analysis.EstimateRelease(new(1));
        Assert.False(release.IsComplete);
        Assert.Contains("overstate", release.Explanation);
        Assert.Contains(analysis.Inspect(1, "", [], false).Evidence, e => e.Contains("incomplete"));
    }

    [Fact]
    public void DeepHeapDoesNotUseRecursiveGraphWalks()
    {
        const int count = 100_000;
        var analysis = Analyze(Enumerable.Repeat(10L, count).ToArray(),
            Enumerable.Range(0, count - 1).Select(i => (i, i + 1)).ToArray(), [0]);
        Assert.Equal(count * 10L, analysis.RetainedBytes[0]);
        Assert.Equal((count - 1) * 10L, analysis.RetainedBytes[1]);
        Assert.Equal(10, analysis.RetainedBytes[^1]);
        Assert.Equal(count - 2, analysis.ImmediateDominators[^1]);
    }

    [Fact]
    public void DominatorsAgreeWithIndependentReachabilityOracleForRandomGraphs()
    {
        var random = new Random(72);
        for (var trial = 0; trial < 100; trial++)
        {
            const int count = 25;
            var edges = Enumerable.Range(0, 75).Select(_ => (random.Next(count), random.Next(count))).ToArray();
            var roots = new[] { random.Next(count), random.Next(count), random.Next(count) };
            var sizes = Enumerable.Range(0, count).Select(_ => (long)random.Next(1, 1000)).ToArray();
            var analysis = Analyze(sizes, edges, roots);
            var before = Oracle(count, edges, roots, -1);
            for (var removed = 0; removed < count; removed++)
            {
                var after = Oracle(count, edges, roots, removed);
                var bytes = Enumerable.Range(0, count).Where(i => before[i] && !after[i]).Sum(i => sizes[i]);
                Assert.Equal(bytes, analysis.RetainedBytes[removed]);
                Assert.Equal(bytes, analysis.EstimateRelease(new(removed)).ReclaimableBytes);
            }
        }
    }

    [Fact]
    public void GraphAndQueriesAreBoundedAndReferencesRemainValid()
    {
        var analysis = Analyze(Enumerable.Repeat(10L, 1000).ToArray(),
            Enumerable.Range(1, 999).Select(i => (0, i)).ToArray(), [0]);
        var graph = analysis.BuildGraph(new(0, MaxNodes: 20, MaxChildren: 64));
        Assert.Equal(20, graph.Nodes.Count); Assert.True(graph.IsTruncated);
        Assert.All(graph.References, r => Assert.Contains(graph.Nodes, n => n.Object.Id == r.ToId));
        Assert.Equal(500, analysis.GetObjects(new(Take: int.MaxValue)).Objects.Count);
        Assert.Throws<ArgumentException>(() => analysis.EstimateRelease(new(900, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => analysis.Describe(-1));
    }

    private static HeapAnalysis Analyze(long[] sizes, (int From, int To)[] edges, int[] roots, bool complete = true) =>
        new(new HeapGraph(sizes.Select((s, i) => new HeapObject((ulong)i + 1, 0, s, "Gen2")).ToArray(),
            [new("T", "Fixture.Type", "fixture")], edges.Select(e => new HeapEdge(e.From, e.To, 0)).ToArray(),
            ["child"], roots.Select(r => new HeapRoot(r, "root", "Strong")).ToArray(), complete));

    private static bool[] Oracle(int count, (int From, int To)[] edges, int[] roots, int removed)
    {
        var seen = new bool[count]; var pending = new Queue<int>(roots.Where(r => r != removed));
        while (pending.TryDequeue(out var node))
        {
            if (seen[node]) continue; seen[node] = true;
            foreach (var edge in edges.Where(e => e.From == node && e.To != removed)) pending.Enqueue(edge.To);
        }
        return seen;
    }
}
