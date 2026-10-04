using WpfStudio.Contracts.Profiling;
using WpfStudio.Profiling;

namespace WpfStudio.Profiling.Tests;

public sealed class HeapExplorationTests
{
    private static readonly HeapType[] Types =
    [
        new("m|System.Object[]", "System.Object[]", "System.Private.CoreLib.dll"),
        new("app|System.Collections.Generic.List<App.Page>", "System.Collections.Generic.List<App.Page>", "System.Private.CoreLib.dll"),
        new("app|App.Page[]", "App.Page[]", "System.Private.CoreLib.dll"),
        new("app|App.Page", "App.Page", "App.dll"),
        new("m|System.EventHandler", "System.EventHandler", "System.Private.CoreLib.dll", IsDelegate: true),
        new("app|App.Window", "App.MainWindow", "App.dll"),
    ];

    /// <summary>Statics holder (0) -[Static Cache.Pages]-> List (1) -[_items]-> Page[] (2) -> pages 3 and 4.
    /// Handler (6), rooted by a strong handle, keeps page 5 alive. Page 7 is unrooted.</summary>
    private static HeapAnalysis CacheAndEventFixture(out HeapGraph graph)
    {
        graph = new HeapGraph(
            [Obj(1, 0, 40), Obj(2, 1, 32), Obj(3, 2, 48), Obj(4, 3, 1000), Obj(5, 3, 2000), Obj(6, 3, 500), Obj(7, 4, 64), Obj(8, 3, 300)],
            Types,
            [new(0, 1, 0), new(1, 2, 1), new(2, 3, 2), new(2, 4, 2), new(6, 5, 3)],
            ["Static App.Cache.Pages", "_items", "Array/reference slot +0x10", "_target"],
            [new(0, "PinnedHandle (slot 0x1)", "PinnedHandle", IsPinned: true), new(6, "StrongHandle (slot 0x2)", "StrongHandle")]);
        return new HeapAnalysis(graph);
    }

    [Fact]
    public void RetentionFlowShowsStaticFieldsAsRootsAndCountsEveryInstance()
    {
        var analysis = CacheAndEventFixture(out _);
        var flow = analysis.GetRetentionFlow(new(TypeKey: "app|App.Page"));
        Assert.Equal(4, flow.Instances); Assert.Equal(1, flow.Unrooted);
        var target = Assert.Single(flow.Nodes, n => n.Kind == "Target");
        Assert.Equal(4, target.Count); Assert.Equal(0, target.Level);
        var cache = Assert.Single(flow.Nodes, n => n.Kind == "Static");
        Assert.Equal("Cache.Pages", cache.Label); Assert.Equal("Static App.Cache.Pages", cache.Detail); Assert.Equal(2, cache.Count); Assert.Equal(3, cache.Level);
        var handle = Assert.Single(flow.Nodes, n => n.Kind == "Root");
        Assert.Equal("Strong handle", handle.Label); Assert.Equal(1, handle.Count);
        Assert.Equal(1, Assert.Single(flow.Nodes, n => n.Kind == "Unrooted").Count);
        // The pinned statics holder array is folded into the static-field root and never shown as an owner.
        Assert.DoesNotContain(flow.Nodes, n => n.Label.Contains("Object[]"));
        var list = Assert.Single(flow.Nodes, n => n.Label.StartsWith("List<"));
        Assert.Equal(2, Assert.Single(flow.Links, l => l.FromId == cache.Id && l.ToId == list.Id).Count);
        var array = Assert.Single(flow.Nodes, n => n.Label == "Page[]");
        Assert.Contains("_items", Assert.Single(flow.Links, l => l.FromId == list.Id && l.ToId == array.Id).Label);
        // Flow is conserved through every owner: what enters a node leaves it.
        foreach (var node in flow.Nodes.Where(n => n.Kind == "Owner"))
            Assert.Equal(flow.Links.Where(l => l.ToId == node.Id).Sum(l => l.Count), flow.Links.Where(l => l.FromId == node.Id).Sum(l => l.Count));
    }

    [Fact]
    public void RetentionFlowCanHideRootKindsAndFoldsWideColumns()
    {
        var analysis = CacheAndEventFixture(out _);
        var hidden = analysis.GetRetentionFlow(new(TypeKey: "app|App.Page", HiddenRootKinds: ["StrongHandle"]));
        Assert.Equal(1, Assert.Single(hidden.Nodes, n => n.Kind == "Hidden").Count);
        Assert.DoesNotContain(hidden.Nodes, n => n.Kind == "Root");

        // Twelve instances each held by an owner of a different type fold into "other types" beyond the per-level limit.
        var types = Enumerable.Range(0, 13).Select(i => new HeapType($"t{i}", $"T{i}", "App.dll")).ToArray();
        var objects = new List<HeapObject>(); var edges = new List<HeapEdge>(); var roots = new List<HeapRoot>();
        for (var i = 0; i < 12; i++) { objects.Add(Obj((ulong)i + 1, i + 1, 10)); roots.Add(new(i, "h", "StrongHandle")); }
        for (var i = 0; i < 12; i++) { objects.Add(Obj((ulong)i + 100, 0, 10)); edges.Add(new(i, 12 + i, 0)); }
        var wide = new HeapAnalysis(new HeapGraph(objects.ToArray(), types, edges.ToArray(), ["f"], roots.ToArray()));
        var flow = wide.GetRetentionFlow(new(TypeKey: "t0", MaxNodesPerLevel: 5));
        Assert.True(flow.IsTruncated);
        Assert.Equal(5, flow.Nodes.Count(n => n.Level == 1));
        Assert.Equal(8, Assert.Single(flow.Nodes, n => n.Kind == "Other" && n.Level == 1).Count);
        Assert.Equal(12, Assert.Single(flow.Nodes, n => n.Kind == "Root").Count);
        Assert.Equal(8, flow.Links.Where(l => l.FromId == flow.Nodes.Single(n => n.Kind == "Other").Id).Sum(l => l.Count));
    }

    [Fact]
    public void InstancesHeldByTwoStaticsAppearOnBothBranchesAndInRootPaths()
    {
        // One runtime holder (0) keeps two statics: a cache list and an event handler that both reach page 3.
        var graph = new HeapGraph(
            [Obj(1, 0, 40), Obj(2, 1, 32), Obj(3, 2, 48), Obj(4, 3, 1000), Obj(5, 4, 64)],
            Types,
            [new(0, 1, 0), new(1, 2, 1), new(2, 3, 2), new(0, 4, 4), new(4, 3, 3)],
            ["Static App.Cache.Pages", "_items", "[0]", "_target", "Static App.Publisher.Changed"],
            [new(0, "PinnedHandle (slot 0x1)", "PinnedHandle", IsPinned: true)]);
        var analysis = new HeapAnalysis(graph);
        var flow = analysis.GetRetentionFlow(new(TypeKey: "app|App.Page"));
        var statics = flow.Nodes.Where(n => n.Kind == "Static").Select(n => n.Label).Order().ToArray();
        Assert.Equal(new[] { "Cache.Pages", "Publisher.Changed" }, statics);
        Assert.Equal(1, Assert.Single(flow.Nodes, n => n.Kind == "Target").Count);
        Assert.Equal("[…]", Assert.Single(flow.Links, l => l.ToId == flow.Nodes.Single(n => n.Kind == "Target").Id && l.Label.StartsWith('[')).Label);
        var (paths, _) = analysis.GetRootPaths(3);
        Assert.Equal(2, paths.Length);
        Assert.All(paths, p => Assert.StartsWith("Static ", p.References[1].Label));
        // At the top of the dominator tree the holder is replaced by the static fields it keeps.
        var top = analysis.GetDominators(new(GroupByType: false));
        Assert.DoesNotContain(top.Nodes, n => n.Object?.Id == 0);
        Assert.Contains(top.Nodes, n => n.Via == "Static App.Cache.Pages");
        Assert.DoesNotContain(analysis.GetTopRetainers(), o => o.Id == 0);
    }

    [Fact]
    public void DominatorPagesGroupSiblingInstancesByType()
    {
        var types = new[] { new HeapType("h", "Holder", "App.dll"), new HeapType("a", "App.A", "App.dll"), new HeapType("b", "App.B", "App.dll") };
        var objects = new List<HeapObject> { Obj(1, 0, 10) };
        var edges = new List<HeapEdge>();
        for (var i = 1; i <= 10; i++) { objects.Add(Obj((ulong)i + 1, 1, 100)); edges.Add(new(0, i, 0)); }
        objects.Add(Obj(20, 2, 5000)); edges.Add(new(0, 11, 1));
        var analysis = new HeapAnalysis(new HeapGraph(objects.ToArray(), types, edges.ToArray(), ["_items", "_big"], [new(0, "h", "StrongHandle")]));
        var top = analysis.GetDominators(new());
        var holder = Assert.Single(top.Nodes);
        Assert.Equal(0, holder.Object!.Id); Assert.Equal(11, holder.ChildCount); Assert.Equal("Strong handle", holder.Via);
        var children = analysis.GetDominators(new(ParentId: 0));
        Assert.Equal(2, children.TotalCount);
        Assert.Equal("App.B", children.Nodes[0].Type); Assert.Equal("_big", children.Nodes[0].Via);
        var group = children.Nodes[1];
        Assert.Equal(10, group.Count); Assert.Null(group.Object); Assert.Equal(1000, group.RetainedBytes); Assert.Equal("_items", group.Via);
        var instances = analysis.GetDominators(new(ParentId: 0, TypeKey: "a", Take: 4));
        Assert.Equal(10, instances.TotalCount); Assert.Equal(4, instances.Nodes.Count);
        Assert.Equal(6, instances.OtherCount); Assert.Equal(600, instances.OtherBytes);
        Assert.All(instances.Nodes, n => Assert.NotNull(n.Object));
    }

    [Fact]
    public void TypeRetainedBytesDoNotDoubleCountNestedInstances()
    {
        var types = new[] { new HeapType("a", "App.Node", "App.dll"), new HeapType("b", "App.Leaf", "App.dll") };
        var analysis = new HeapAnalysis(new HeapGraph([Obj(1, 0, 10), Obj(2, 0, 20), Obj(3, 1, 300)], types,
            [new(0, 1, 0), new(1, 2, 0)], ["next"], [new(0, "h", "StrongHandle")]));
        var summary = analysis.SummarizeTypes();
        Assert.Equal(330, summary.Single(t => t.Key == "a").RetainedBytes);
        Assert.Equal(300, summary.Single(t => t.Key == "b").RetainedBytes);
        var composition = analysis.GetRetained(1);
        Assert.Equal(320, composition.Bytes);
        Assert.Equal(new[] { "App.Leaf", "App.Node" }, composition.Types.Select(t => t.Type));
        Assert.Equal(2, analysis.Describe(1).RetainedCount);
    }

    [Fact]
    public void RootPathsAndGraphPreferLongLivedOwnersAndHonorHiddenRootKinds()
    {
        // Object 2 is reachable directly from the finalizer queue and, longer, through a static field.
        var types = new[] { new HeapType("o", "System.Object[]", "System.Private.CoreLib.dll"), new HeapType("c", "App.Cache", "App.dll"), new HeapType("p", "App.Page", "App.dll") };
        var analysis = new HeapAnalysis(new HeapGraph([Obj(1, 0, 10), Obj(2, 1, 20), Obj(3, 2, 30)], types,
            [new(0, 1, 0), new(1, 2, 1)], ["Static App.Cache.Instance", "_page"],
            [new(2, "FinalizerQueue (slot 0x1)", "FinalizerQueue"), new(0, "PinnedHandle (slot 0x2)", "PinnedHandle", IsPinned: true)]));
        var (paths, _) = analysis.GetRootPaths(2);
        Assert.Equal(2, paths.Length);
        Assert.Contains(paths[0].References, r => r.Label.StartsWith("Static "));
        var graph = analysis.BuildGraph(new(2, HiddenRootKinds: ["FinalizerQueue"]));
        Assert.DoesNotContain(graph.References, r => r.Kind == "FinalizerQueue");
        Assert.Contains(graph.References, r => r.Label.StartsWith("Static "));
        var focus = Assert.Single(graph.Nodes, n => n.IsFocus);
        Assert.Equal(2, focus.IncomingCount); Assert.Equal(0, focus.OutgoingCount);
        var owners = analysis.GetNeighbors(new(2, Incoming: true));
        Assert.Contains(owners.Nodes, n => n.Object.Id == 1);
        Assert.All(owners.References, r => Assert.Equal(2, r.ToId));
    }

    [Fact]
    public void InsightsExplainEventHandlerRetentionDisposedObjectsAndClosedWindows()
    {
        var analysis = CacheAndEventFixture(out var graph);
        var extras = new HeapExtras();
        extras.Disposed.Add(3);
        var windowGraph = new HeapGraph(graph.Objects.Append(Obj(9, 5, 4000)).ToArray(), Types, graph.Edges.Append(new(1, 8, 1)).ToArray(),
            graph.Labels, graph.Roots);
        var windowAnalysis = new HeapAnalysis(windowGraph);
        extras.ClosedWindows.Add(8); extras.Disposed.Add(8);
        var insights = HeapInsights.Compute(windowGraph, windowAnalysis, extras);
        var windows = Assert.Single(insights, i => i.Id == "closed-windows");
        Assert.Equal("High", windows.Severity); Assert.Equal(8, Assert.Single(windows.Items).ObjectId);
        Assert.Equal("closed-windows", insights[0].Id);
        var disposed = Assert.Single(insights, i => i.Id == "disposed-alive");
        Assert.Equal(1, disposed.Count); Assert.Equal("app|App.Page", disposed.Items[0].TypeKey);
        var events = Assert.Single(insights, i => i.Id == "event-handlers");
        Assert.Equal(1, events.Count); Assert.Equal(5, events.Items[0].ObjectId);
        Assert.DoesNotContain(HeapInsights.Compute(graph, analysis, new HeapExtras()), i => i.Category == "Waste");
        _ = analysis;
    }

    [Fact]
    public void WasteInspectionsMeasureDuplicateStringsAndSparseArrays()
    {
        var types = new[] { new HeapType("s", "System.String", "System.Private.CoreLib.dll"), new HeapType("a", "System.Object[]", "System.Private.CoreLib.dll") };
        var objects = new List<HeapObject> { Obj(1, 1, 8 * 1024 + 24) };
        for (var i = 0; i < 200; i++) objects.Add(Obj((ulong)i + 2, 0, 100));
        var graph = new HeapGraph(objects.ToArray(), types, [new(0, 1, 0)], ["[0]"], [new(0, "h", "StrongHandle")]);
        var analysis = new HeapAnalysis(graph);
        var extras = new HeapExtras { PointerSize = 8 };
        extras.Strings["customer"] = new() { Count = 200, Bytes = 200 * 100, Sample = 1 };
        extras.Strings["unique"] = new() { Count = 1, Bytes = 100, Sample = 2 };
        extras.ReferenceArrayLengths[0] = 8 * 1024;
        var insights = HeapInsights.Compute(graph, analysis, extras);
        var strings = Assert.Single(insights, i => i.Id == "duplicate-strings");
        Assert.Equal(199 * 100, strings.Bytes); Assert.Equal("\"customer\"", Assert.Single(strings.Items).Label);
        var sparse = Assert.Single(insights, i => i.Id == "sparse-arrays");
        Assert.Equal((8 * 1024 - 1) * 8L, sparse.Bytes);
    }

    [Fact]
    public void LabelsShortenGenericTypesAndRecognizeFrameworkModules()
    {
        Assert.Equal("List<Page>", MemoryLabels.ShortType("System.Collections.Generic.List<App.Views.Page>"));
        Assert.Equal("Dictionary<String, List<Int32>>[]", MemoryLabels.ShortType("System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>[]"));
        Assert.Equal("Byte[]", MemoryLabels.ShortType("System.Byte[]"));
        Assert.Equal("Inner", MemoryLabels.ShortType("App.Outer+Inner"));
        Assert.Equal("App.Views", MemoryLabels.Namespace("App.Views.Page"));
        Assert.Equal("System.Collections.Generic", MemoryLabels.Namespace("System.Collections.Generic.List<App.Page>"));
        Assert.True(MemoryLabels.IsFrameworkModule("PresentationFramework.dll"));
        Assert.False(MemoryLabels.IsFrameworkModule("ColtonStack.dll"));
        Assert.Equal("Stack: App.Program.Main", MemoryLabels.RootDisplay("Stack", "Stack: App.Program.Main(System.String[]) (slot 0x1)"));
        Assert.Equal("Cache.Pages", MemoryLabels.ShortStatic("Static App.Data.Cache.Pages"));
        Assert.Equal("Outer.Inner.Items", MemoryLabels.ShortStatic("App.Outer+Inner.Items"));
        Assert.Equal("Cache<Page>.Items", MemoryLabels.ShortStatic("App.Cache<App.Page>.Items"));
    }

    private static HeapObject Obj(ulong address, int type, long size) => new(address, type, size, "Generation2");
}
