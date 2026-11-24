using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling.Tests;

public sealed class ProfilingHardeningTests
{
    [Fact]
    public void ClosedWindowsAndDisposedInsightsDeduplicateDominatorAncestors()
    {
        // Root -> Outer (100 B, retained 300 B) -> Inner (200 B, retained 200 B).
        // Both Outer and Inner are closed windows / disposed objects; total retained bytes must be 300, not 500.
        var graph = new HeapGraph(
            [
                new(0x1000, 0, 100, HeapGeneration.Generation2),
                new(0x2000, 0, 200, HeapGeneration.Generation2)
            ],
            [new("app|MyApp.MyWindow", "MyApp.MyWindow", "MyApp.dll")],
            [new(0, 1, 0)],
            ["_child"],
            [new(0, "Static MyApp.Host.Current", "Strong")]);
        var analysis = new HeapAnalysis(graph);
        var extras = new HeapExtras();
        extras.ClosedWindows.Add(0);
        extras.ClosedWindows.Add(1);

        var closedInsight = HeapInsights.Compute(graph, analysis, extras).Single(i => i.Id == "closed-windows");
        Assert.Equal(2, closedInsight.Count);
        Assert.Equal(300, closedInsight.Bytes);

        var disposedExtras = new HeapExtras();
        disposedExtras.Disposed.Add(0);
        disposedExtras.Disposed.Add(1);
        var disposedInsight = HeapInsights.Compute(graph, analysis, disposedExtras).Single(i => i.Id == "disposed-alive");
        Assert.Equal(2, disposedInsight.Count);
        Assert.Equal(300, disposedInsight.Bytes);
        Assert.Equal(300, Assert.Single(disposedInsight.Items).Bytes);
    }

    [Fact]
    public void EventHandlerRetentionDeduplicatesDominatorAncestors()
    {
        // Root -> Delegate (16) -> SubscriberA (100) -> Delegate2 (16) -> SubscriberB (200)
        // Both SubscriberA (retained 316) and SubscriberB (retained 200) are dominated by delegates.
        // Because SubscriberA dominates SubscriberB, combined retained bytes must be 316, not 516.
        var graph = new HeapGraph(
            [
                new(0x1000, 0, 16, HeapGeneration.Generation2),
                new(0x2000, 1, 100, HeapGeneration.Generation2),
                new(0x3000, 0, 16, HeapGeneration.Generation2),
                new(0x4000, 1, 200, HeapGeneration.Generation2)
            ],
            [
                new("sys|System.Action", "System.Action", "System.Private.CoreLib.dll", IsDelegate: true),
                new("app|MyApp.Subscriber", "MyApp.Subscriber", "MyApp.dll")
            ],
            [new(0, 1, 0), new(1, 2, 1), new(2, 3, 0)],
            ["_target", "_handler"],
            [new(0, "Strong root", "Strong")]);
        var analysis = new HeapAnalysis(graph);

        var insight = HeapInsights.Compute(graph, analysis, new HeapExtras()).Single(i => i.Id == "event-handlers");
        Assert.Equal(2, insight.Count);
        Assert.Equal(316, insight.Bytes);
    }

    [Fact]
    public void InstancePathsRespectHiddenRootKindsForStaticEdges()
    {
        // Object 0 (static holder, rooted only by "HiddenKind") --Static MyApp.Cache.Items--> Object 2 (Target)
        // Object 1 (normal owner, rooted by "Strong") --_item--> Object 2 (Target)
        var graph = new HeapGraph(
            [
                new(0x1000, 0, 32, HeapGeneration.Generation2),
                new(0x2000, 1, 32, HeapGeneration.Generation2),
                new(0x3000, 2, 64, HeapGeneration.Generation2)
            ],
            [
                new("sys|System.Object[]", "System.Object[]", "System.Private.CoreLib.dll"),
                new("app|MyApp.Owner", "MyApp.Owner", "MyApp.dll"),
                new("app|MyApp.Target", "MyApp.Target", "MyApp.dll")
            ],
            [new(0, 2, 0), new(1, 2, 1)],
            ["Static MyApp.Cache.Items", "_item"],
            [
                new(0, "Hidden static holder", "HiddenKind"),
                new(1, "Strong root", "Strong")
            ]);
        var analysis = new HeapAnalysis(graph);

        var flow = analysis.GetRetentionFlow(new MemoryFlowRequest(ObjectId: 2, HiddenRootKinds: ["HiddenKind"]));
        Assert.DoesNotContain(flow.Nodes, n => n.Detail.Contains("MyApp.Cache.Items", StringComparison.Ordinal));
        Assert.Contains(flow.Nodes, n => n.Label == "Owner");
    }

    [Fact]
    public void FlowNodeMergeDeduplicatesPerInstanceBytesWhenFolding()
    {
        // Target instance (id 4, size 100) is reachable from 3 distinct owner types (ids 1, 2, 3), all rooted by Strong roots.
        // With MaxNodesPerLevel = 2, 2 of the 3 owners fold into one "Other" node; the shared target instance's 100 bytes
        // must only be counted once in the folded node, not twice (200).
        var graph = new HeapGraph(
            [
                new(0x1000, 0, 10, HeapGeneration.Generation2),
                new(0x2000, 1, 10, HeapGeneration.Generation2),
                new(0x3000, 2, 10, HeapGeneration.Generation2),
                new(0x4000, 3, 100, HeapGeneration.Generation2)
            ],
            [
                new("app|OwnerA", "OwnerA", "MyApp.dll"),
                new("app|OwnerB", "OwnerB", "MyApp.dll"),
                new("app|OwnerC", "OwnerC", "MyApp.dll"),
                new("app|Target", "Target", "MyApp.dll")
            ],
            [new(0, 3, 0), new(1, 3, 0), new(2, 3, 0)],
            ["_target"],
            [
                new(0, "Static MyApp.A.Root", "Strong"),
                new(1, "Static MyApp.B.Root", "Strong"),
                new(2, "Static MyApp.C.Root", "Strong")
            ]);
        var analysis = new HeapAnalysis(graph);

        var flow = analysis.GetRetentionFlow(new MemoryFlowRequest(ObjectId: 3, MaxNodesPerLevel: 2));
        var other = Assert.Single(flow.Nodes, n => n.Kind == "Other" && n.Level == 1);
        Assert.Equal(1, other.Count);
        Assert.Equal(100, other.Bytes);
    }
}
