using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

internal static class LayoutTestData
{
    internal static LayoutSnapshot Snapshot(string value) => new(true,
        [new("Render size", value, "Observed after arrange."), new("Margin", "8, 4, 8, 4")],
        [new("render", [new(0, 0), new(100, 0), new(100, 30), new(0, 30)], "Rendered bounds")],
        ["Clip bounds are approximations."]);
}

public sealed partial class DesignerTests
{
    [Fact]
    public async Task DesignerLayoutTracksSelectionAndSourceRevisionAndClearsAfterHostExit()
    {
        var client = new FakePreview { Inspect = request => Task.FromResult(Inspection(request.Version) with { Layout = LayoutTestData.Snapshot("100 × 30") }) };
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("layout-view.xaml");
        await model.OpenAsync(document);
        model.SelectedNode = model.Tree[0];
        Assert.True(model.HasLayout);
        Assert.Equal("100 × 30", model.Layout!.Facts[0].Value);
        Assert.True(model.CanShowLayoutOverlay);
        Assert.Empty(model.LayoutDetails.VisibleOverlays);
        model.ShowLayoutOverlay = true;
        Assert.Single(model.LayoutDetails.VisibleOverlays);

        var pending = new TaskCompletionSource<PreviewInspection>();
        client.Inspect = _ => pending.Task;
        model.SelectedNode = null;
        model.SelectedNode = model.Tree[0];
        Assert.False(model.HasLayout);
        document.Content = "<Button Content=\"Changed\" />";
        pending.SetResult(Inspection(client.Requests.Last().Version) with { Layout = LayoutTestData.Snapshot("STALE") });
        await Task.Yield();
        Assert.Null(model.Layout);
        Assert.Empty(model.LayoutDetails.VisibleOverlays);
        Assert.False(model.CanShowLayoutOverlay);

        client.Inspect = request => Task.FromResult(Inspection(request.Version) with { Layout = LayoutTestData.Snapshot("200 × 40") });
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedNode = model.Tree[0];
        Assert.Equal("200 × 40", model.Layout!.Facts[0].Value);
        client.Exit("Host ended");
        Assert.Null(model.Layout);
        Assert.Empty(model.LayoutDetails.Facts);
        Assert.Empty(model.LayoutDetails.VisibleOverlays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesignerSelectionDoesNotDisplayAnotherNodesLateLayout(bool failed)
    {
        var pending = new TaskCompletionSource<PreviewInspection>();
        var client = new FakePreview
        {
            Render = request => Task.FromResult(Snapshot(request.Version) with { Nodes = [Node(), Node() with { Id = "2" }] }),
            Inspect = request => request.NodeId == "1" ? pending.Task : Task.FromResult(Inspection(request.Version) with
            { Node = Node() with { Id = "2" }, Layout = LayoutTestData.Snapshot("Second") })
        };
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("layout-selection.xaml"));
        model.SelectedNode = model.Tree[0];
        model.SelectedNode = model.Tree[1];
        if (failed) pending.SetException(new InvalidOperationException("Old inspection failed"));
        else pending.SetResult(Inspection(client.Requests.Last().Version) with { Layout = LayoutTestData.Snapshot("First") });
        await Task.Yield();
        Assert.Equal("Second", model.Layout!.Facts[0].Value);
    }
}

public sealed partial class InspectionViewModelTests
{
    private static FakeSession LayoutSession(bool supportsLayout = true, bool supportsOverlay = true)
    {
        var session = new FakeSession
        {
            Inspect = request => Task.FromResult(Element(request, "Hello") with { Layout = LayoutTestData.Snapshot(request.NodeId) })
        };
        session.Hello = session.Hello with { Capabilities = session.Hello.Capabilities
            .Concat(supportsLayout ? ["layout"] : Array.Empty<string>())
            .Concat(supportsOverlay ? ["layout-overlay"] : Array.Empty<string>()).ToArray() };
        return session;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LiveLayoutGatesFactsAndOverlayByAgentCapabilities(bool supportsLayout, bool supportsOverlay)
    {
        var session = LayoutSession(supportsLayout, supportsOverlay);
        var requests = new List<InspectionHighlightRequest>();
        session.Highlight = request => { requests.Add(request); return Task.FromResult(new InspectionHighlightResult(true)); };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0];
        Assert.Equal(supportsLayout, model.HasLayout);
        Assert.Equal(supportsLayout && supportsOverlay, model.CanShowLayoutOverlay);
        model.ShowLayoutOverlay = true;
        Assert.Equal(supportsLayout && supportsOverlay, requests.Last().ShowLayout);
        model.ShowLayoutOverlay = false;
        Assert.False(requests.Last().ShowLayout);
        if (!supportsLayout) Assert.Contains("does not provide", model.LayoutStatus);
        else Assert.Contains("Render size", model.Layout!.Facts.Select(fact => fact.Name));
    }

    [Fact]
    public async Task LiveLayoutOverlayWorksIndependentlyOfSelectionHighlightAndClearsDuringRefresh()
    {
        var session = LayoutSession();
        var requests = new List<InspectionHighlightRequest>();
        session.Highlight = request => { requests.Add(request); return Task.FromResult(new InspectionHighlightResult(true)); };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false, HighlightSelection = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0];
        model.ShowLayoutOverlay = true;
        Assert.Equal("first", requests.Last().NodeId);
        Assert.True(requests.Last().ShowLayout);

        var pending = new TaskCompletionSource<InspectionElement>();
        InspectionNodeRequest? observed = null;
        session.Inspect = request => { observed = request; return pending.Task; };
        var refresh = model.RefreshCommand.ExecuteAsync(null);
        Assert.Null(model.Layout);
        Assert.False(model.CanShowLayoutOverlay);
        Assert.Null(requests.Last().NodeId);
        pending.SetResult(Element(observed!, "Hello") with { Layout = LayoutTestData.Snapshot("Refreshed") });
        await refresh;
        Assert.Equal("Refreshed", model.Layout!.Facts[0].Value);
        Assert.True(requests.Last().ShowLayout);
        model.ShowLayoutOverlay = false;
        Assert.Null(requests.Last().NodeId);
    }

    [Fact]
    public async Task LiveLayoutRejectsLateSelectionAndPauseResumeReplies()
    {
        var first = new TaskCompletionSource<InspectionElement>();
        var session = LayoutSession();
        var highlights = new List<InspectionHighlightRequest>();
        session.Highlight = request => { highlights.Add(request); return Task.FromResult(new InspectionHighlightResult(true)); };
        session.Inspect = request => request.NodeId == "first" ? first.Task : Task.FromResult(Element(request, "Hello") with { Layout = LayoutTestData.Snapshot("Second") });
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0];
        model.SelectedNode = model.Tree[1];
        first.SetResult(Element(new(1, "first"), "Late") with { Layout = LayoutTestData.Snapshot("First") });
        await Task.Yield();
        Assert.Equal("Second", model.Layout!.Facts[0].Value);
        model.ShowLayoutOverlay = true;
        Assert.True(highlights.Last().ShowLayout);

        var delayed = new TaskCompletionSource<InspectionElement>();
        InspectionNodeRequest? observed = null;
        session.Inspect = request => { observed = request; return delayed.Task; };
        var refresh = model.RefreshCommand.ExecuteAsync(null);
        model.SetDebuggerState(true);
        var pausedRequests = highlights.Count;
        Assert.Null(model.Layout);
        Assert.False(model.CanShowLayoutOverlay);
        Assert.Contains("paused", model.LayoutStatus);
        model.ShowLayoutOverlay = false;
        model.ShowLayoutOverlay = true;
        Assert.Equal(pausedRequests, highlights.Count);
        model.SetDebuggerState(false);
        Assert.Equal(pausedRequests + 1, highlights.Count);
        Assert.Null(highlights.Last().NodeId);
        Assert.False(highlights.Last().ShowLayout);
        delayed.SetResult(Element(observed!, "Late") with { Layout = LayoutTestData.Snapshot("Before pause") });
        await refresh;
        Assert.Null(model.Layout);
        session.Inspect = request => Task.FromResult(Element(request, "Hello") with { Layout = LayoutTestData.Snapshot("After pause") });
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("After pause", model.Layout!.Facts[0].Value);
        await model.DisconnectCommand.ExecuteAsync(null);
        Assert.Null(model.Layout);
        Assert.Empty(model.LayoutDetails.Facts);
        Assert.False(model.CanShowLayoutOverlay);
    }

    [Fact]
    public async Task ReplacedSessionAndUnavailableElementNeverRetainLayout()
    {
        var pending = new TaskCompletionSource<InspectionElement>();
        var first = LayoutSession(); first.Inspect = _ => pending.Task;
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(first);
        model.SelectedNode = model.Tree[0];
        var second = LayoutSession();
        second.Inspect = request => Task.FromResult(Element(request, "Hello") with { Available = false, Status = "Element removed", Layout = LayoutTestData.Snapshot("Invalid") });
        await model.AttachAsync(second);
        model.SelectedNode = model.Tree[1];
        pending.SetResult(Element(new(1, "first"), "Late") with { Layout = LayoutTestData.Snapshot("Old session") });
        await Task.Yield();
        Assert.Null(model.Layout);
        Assert.Empty(model.LayoutDetails.VisibleOverlays);
        Assert.Contains("removed", model.LayoutStatus);
    }
}
