using WpfStudio.App.Features.Inspection;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Shell.Tests;

public sealed partial class InspectionViewModelTests
{
    private static InspectionNode Node(string id) => new(id, null, null, 1, "System.Windows.Controls.TextBlock", id, null, true);
    private static InspectionElement Element(InspectionNodeRequest request, string value) => new(request.Revision, request.NodeId,
        [new("Text", "System.Windows.Controls.TextBlock", "PresentationFramework", "System.String", value, "Local", true, false, false,
            new("Name", "Active", "Resolved", "The binding is active.", "Demo.RuntimeViewModel"))], "Demo.RuntimeViewModel", true);

    [Fact]
    public async Task CurrentRuntimeValuesAndTypesRefreshWhileSelectionIsPreserved()
    {
        var session = new FakeSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0];
        Assert.Equal("Hello", Assert.Single(model.Bindings).Value);
        Assert.Equal("Demo.RuntimeViewModel", model.DataContextDescription);
        var selected = model.SelectedNode;
        session.Inspect = r => Task.FromResult(Element(r, "Changed"));
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Same(selected, model.SelectedNode);
        Assert.Equal("Changed", Assert.Single(model.Properties).Value);
    }

    [Fact]
    public async Task SlowInspectionCannotReplaceNewNodeOrDisconnectedSession()
    {
        var first = new TaskCompletionSource<InspectionElement>();
        var session = new FakeSession { Inspect = _ => first.Task };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0];
        session.Inspect = r => Task.FromResult(Element(r, "Second"));
        model.SelectedNode = model.Tree[1];
        first.SetResult(Element(new(1, "first"), "Stale"));
        await Task.Yield();
        Assert.Equal("Second", Assert.Single(model.Properties).Value);
        await model.DisconnectCommand.ExecuteAsync(null);
        Assert.False(model.IsConnected);
        Assert.True(session.Disposed);
        Assert.Contains("last snapshot", model.Freshness);
    }

    [Fact]
    public async Task DebuggerPauseKeepsLastSnapshotAndSendsNoInspectorRequests()
    {
        var session = new FakeSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0];
        var reads = session.Reads;
        model.SetDebuggerState(true);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(reads, session.Reads);
        Assert.True(model.IsPaused);
        Assert.Contains("Debugger paused", model.Freshness);
        Assert.NotEmpty(model.Tree);
        model.SetDebuggerState(false);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(session.Reads > reads);
    }

    [Fact]
    public async Task EndedRuntimeNodeClearsItsInspectionOnNextSnapshot()
    {
        var session = new FakeSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0];
        session.Nodes = [Node("second")];
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Null(model.SelectedNode);
        Assert.Empty(model.Properties);
        Assert.Empty(model.Bindings);
    }

    [Fact]
    public async Task ReplacingApplicationCannotRestoreOldTreeWhileWaitingForConnection()
    {
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(new FakeSession());
        var ready = new TaskCompletionSource<InspectionHello>();
        var replacement = new FakeSession { Wait = token => ready.Task.WaitAsync(token), Nodes = [Node("replacement")] };
        var connecting = model.AttachAsync(replacement);
        model.ShowLogicalTree = true;
        Assert.Empty(model.Tree);
        ready.SetResult(replacement.Hello);
        await connecting;
        Assert.Equal("replacement", Assert.Single(model.Tree).Node.Id);
    }

    [Fact]
    public async Task CancellingConnectionDisposesSessionInsteadOfAcceptingUnpolledLateHello()
    {
        var ready = new TaskCompletionSource<InspectionHello>();
        var session = new FakeSession { Wait = token => ready.Task.WaitAsync(token) };
        await using var model = new InspectionViewModel(new InlineDispatcher());
        using var cancellation = new CancellationTokenSource();
        var connecting = model.AttachAsync(session, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await connecting;
        ready.SetResult(session.Hello);
        Assert.True(session.Disposed);
        Assert.False(model.IsConnected);
        Assert.Empty(model.Tree);
    }

    [Fact]
    public async Task IssueGroupsPreserveInstancesAndNavigateToTheirActualElement()
    {
        var nodes = new[] { Node("first"), Node("second") };
        var observations = nodes.Select(node => new InspectionBindingObservation("binding-" + node.Id, node.Id, "Text",
            "System.Windows.Controls.TextBlock", "PresentationFramework",
            new("Nmae", "PathError", "MissingProperty", "No readable property Nmae.", "Demo.RuntimeViewModel"))).ToArray();
        long revision = 0;
        var session = new FakeSession { Snapshot = () => new(++revision, nodes, [], BindingObservations: observations, ScannedBindingNodes: ["first", "second"]) };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        var group = Assert.Single(model.IssueGroups);
        Assert.Equal(2, group.Instances.Count);
        var second = group.Instances.Single(issue => issue.NodeId == "second");
        await model.SelectBindingIssueCommand.ExecuteAsync(second);
        Assert.Equal("second", model.SelectedNode?.Node.Id);
        Assert.Equal(1, model.SelectedTabIndex);
        Assert.Equal("Hello", Assert.Single(model.Bindings).Value);

        observations[1] = observations[1] with { Binding = new("Nmae", "Active", "Active", "Active", "Demo.RuntimeViewModel") };
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Contains(model.IssueGroups.SelectMany(g => g.Instances), issue => issue.NodeId == "second" && issue.State == "Resolved");
        model.ShowIssueHistory = false;
        Assert.Equal("first", Assert.Single(Assert.Single(model.IssueGroups).Instances).NodeId);
        await model.DisconnectCommand.ExecuteAsync(null);
        Assert.Empty(model.IssueGroups);
        model.ShowIssueHistory = true;
        Assert.Contains(model.IssueGroups.SelectMany(g => g.Instances), issue => issue.NodeId == "first" && issue.State == "SessionEnded");
        Assert.Contains(model.IssueGroups.SelectMany(g => g.Instances), issue => issue.NodeId == "second" && issue.State == "Resolved");
    }

    [Fact]
    public async Task WaitingForRealConnectionDoesNotEndTheNewIssueTracker()
    {
        var ready = new TaskCompletionSource<InspectionHello>();
        var failure = new InspectionBindingObservation("failure", "first", "Text", "TextBlock", "PresentationFramework",
            new("Typo", "PathError", "MissingProperty", "Typo does not exist."));
        var session = new FakeSession
        {
            Connected = false,
            Wait = token => ready.Task.WaitAsync(token),
            Snapshot = () => new(1, [Node("first")], [], BindingObservations: [failure], ScannedBindingNodes: ["first"])
        };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        var connecting = model.AttachAsync(session);
        Assert.False(model.IsConnected);
        session.Connected = true;
        ready.SetResult(session.Hello);
        await connecting;
        Assert.Equal("Active", Assert.Single(Assert.Single(model.IssueGroups).Instances).State);
    }

    [Fact]
    public async Task PickingSelectsReportedElementAndIgnoresAnOlderSnapshot()
    {
        var pick = new InspectionPickState(false, 0);
        var session = new FakeSession
        {
            Snapshot = () => new(1, [Node("first"), Node("second")], [], Pick: pick),
            Pick = request => Task.FromResult(new InspectionPickState(request.Enabled, 5))
        };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        await model.TogglePickingCommand.ExecuteAsync(null);
        Assert.True(model.IsPicking);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.IsPicking); // Snapshot sequence 0 cannot undo the newer arm response.
        pick = new(false, 6, "second", "Picked second");
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.False(model.IsPicking);
        Assert.Equal("second", model.SelectedNode?.Node.Id);
        model.SelectedNode = model.Tree[0];
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("first", model.SelectedNode?.Node.Id); // Replaying the same pick cannot override user selection.
    }

    [Fact]
    public async Task SlowPickResponseCannotArmAReplacementSession()
    {
        var result = new TaskCompletionSource<InspectionPickState>();
        var first = new FakeSession { Pick = _ => result.Task };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(first);
        var pending = model.TogglePickingCommand.ExecuteAsync(null);
        await model.AttachAsync(new FakeSession());
        result.SetResult(new(true, 100));
        await pending;
        Assert.False(model.IsPicking);
        Assert.Empty(model.PickStatus);
    }

    [Fact]
    public async Task ClickCompletingBeforeArmResponseStillSelectsItsElementWithRefreshDisabled()
    {
        var pick = new InspectionPickState(false, 0);
        var session = new FakeSession
        {
            Snapshot = () => new(1, [Node("first"), Node("second")], [], Pick: pick),
            Pick = _ => Task.FromResult(pick = new(false, 2, "second", "Picked during arm"))
        };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        await model.TogglePickingCommand.ExecuteAsync(null);
        Assert.False(model.IsPicking);
        Assert.Equal("second", model.SelectedNode?.Node.Id);
        Assert.Equal(1, model.SelectedTabIndex);
    }

    [Fact]
    public async Task StaleHighlightFailureCannotReplaceNewSelectionStatus()
    {
        var result = new TaskCompletionSource<InspectionHighlightResult>();
        var requests = new List<InspectionHighlightRequest>();
        var session = new FakeSession
        {
            Highlight = request =>
            {
                requests.Add(request);
                return request.NodeId == "first" ? result.Task : Task.FromResult(new InspectionHighlightResult(true));
            }
        };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0];
        model.SelectedNode = model.Tree[1];
        result.SetResult(new(false, "Stale highlight failure"));
        await Task.Yield();
        Assert.Equal("second", requests.Last().NodeId);
        Assert.DoesNotContain("Stale", model.PickStatus);
        model.HighlightSelection = false;
        Assert.Null(requests.Last().NodeId);
    }

    [Fact]
    public async Task PausedDebuggerDisablesPickingAndHighlighting()
    {
        int picks = 0, highlights = 0;
        var session = new FakeSession
        {
            Pick = request => { picks++; return Task.FromResult(new InspectionPickState(true, 1)); },
            Highlight = request => { highlights++; return Task.FromResult(new InspectionHighlightResult(true)); }
        };
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        await model.AttachAsync(session, debugging: true);
        model.SetDebuggerState(true);
        int before = highlights;
        Assert.False(model.TogglePickingCommand.CanExecute(null));
        Assert.False(model.CanHighlight);
        model.SelectedNode = model.Tree[0];
        await model.TogglePickingCommand.ExecuteAsync(null);
        Assert.Equal(0, picks);
        Assert.Equal(before, highlights);
    }

    private sealed class FakeSession : IInspectionSession
    {
        public InspectionHello Hello { get; set; } = new(1, "test", 123, "10.0", "1.0", ["tree", "bindings", "pick", "highlight", "property-edit", "modules", "source-property-validation"]);
        public bool Connected { get; set; } = true;
        public bool IsConnected => Connected && !Disposed;
        public bool IsDebuggerPaused { get; private set; }
        public string Status => IsConnected ? "Connected" : "Disconnected";
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public int ModuleReads { get; private set; }
        public List<AppearanceRequest> AppearanceRequests { get; } = [];
        public List<BindingSourceRequest> BindingSourceRequests { get; } = [];
        public Func<BindingSourceRequest, Task<BindingSourceResponse>> BindingSource { get; set; } = request =>
            Task.FromResult(new BindingSourceResponse(request, false, Status: "Binding source is not configured in this test."));
        public Func<AppearanceRequest, Task<AppearanceResponse>> Appearance { get; set; } = request =>
            Task.FromResult(new AppearanceResponse(request, AppearanceSnapshot.Unavailable("Appearance is not configured in this test.")));
        public IReadOnlyList<InspectionNode> Nodes { get; set; } = [Node("first"), Node("second")];
        public Func<InspectionNodeRequest, Task<InspectionElement>> Inspect { get; set; } = r => Task.FromResult(Element(r, "Hello"));
        public Func<CancellationToken, Task<InspectionHello>>? Wait { get; set; }
        public Func<InspectionTree>? Snapshot { get; set; }
        public Func<CancellationToken, Task<InspectionModuleCatalog>> Modules { get; set; } = _ => Task.FromResult(new InspectionModuleCatalog([]));
        public Func<InspectionPickRequest, Task<InspectionPickState>> Pick { get; set; } = request => Task.FromResult(new InspectionPickState(request.Enabled, 1));
        public Func<InspectionHighlightRequest, Task<InspectionHighlightResult>> Highlight { get; set; } = request => Task.FromResult(new InspectionHighlightResult(true));
        public Func<InspectionPropertyEdit, Task<InspectionPropertyValidation>> Validate { get; set; } = request => Task.FromResult(new InspectionPropertyValidation(true));
        public Func<InspectionSourcePropertyRequest, CancellationToken, Task<InspectionSourcePropertyResult>> ValidateSource { get; set; } =
            (request, _) => Task.FromResult(new InspectionSourcePropertyResult(false, request.Revision, request.NodeId, request.PropertyId, request.SourceEditToken,
                Error: "Source validation is not configured in this test."));
        public Func<InspectionPropertyEdit, Task<InspectionPropertyEditResult>> Edit { get; set; } = request => Task.FromResult(new InspectionPropertyEditResult(request.OperationId, "Rejected"));
        public Func<InspectionEditStatusRequest, Task<InspectionPropertyEditResult>> EditStatus { get; set; } = request => Task.FromResult(new InspectionPropertyEditResult(request.OperationId, "Unknown"));
        public event Action? StateChanged;
        public Task<InspectionHello> WaitForConnectionAsync(CancellationToken cancellationToken = default) => Wait?.Invoke(cancellationToken) ?? Task.FromResult(Hello);
        public Task<InspectionTree> SnapshotAsync(InspectionTreeRequest? request = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot?.Invoke() ?? new InspectionTree(++Reads, Nodes, []));
        public Task<InspectionElement> InspectAsync(InspectionNodeRequest request, CancellationToken cancellationToken = default) => Inspect(request);
        public Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request, CancellationToken cancellationToken = default)
        { AppearanceRequests.Add(request); return Appearance(request); }
        public Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request, CancellationToken cancellationToken = default)
        { BindingSourceRequests.Add(request); return BindingSource(request); }
        public Task<InspectionModuleCatalog> GetModulesAsync(CancellationToken cancellationToken = default) { ModuleReads++; return Modules(cancellationToken); }
        public Task<InspectionPickState> SetPickingAsync(InspectionPickRequest request, CancellationToken cancellationToken = default) => Pick(request);
        public Task<InspectionHighlightResult> HighlightAsync(InspectionHighlightRequest request, CancellationToken cancellationToken = default) => Highlight(request);
        public Task<InspectionPropertyValidation> ValidatePropertyAsync(InspectionPropertyEdit request, CancellationToken cancellationToken = default) => Validate(request);
        public Task<InspectionSourcePropertyResult> ValidateSourcePropertyAsync(InspectionSourcePropertyRequest request, CancellationToken cancellationToken = default) => ValidateSource(request, cancellationToken);
        public Task<InspectionPropertyEditResult> SetPropertyAsync(InspectionPropertyEdit request, CancellationToken cancellationToken = default) => Edit(request);
        public Task<InspectionPropertyEditResult> GetEditStatusAsync(InspectionEditStatusRequest request, CancellationToken cancellationToken = default) => EditStatus(request);
        public void SetDebuggerPaused(bool paused) { IsDebuggerPaused = paused; StateChanged?.Invoke(); }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { Disposed = true; StateChanged?.Invoke(); return Task.CompletedTask; }
        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
}
