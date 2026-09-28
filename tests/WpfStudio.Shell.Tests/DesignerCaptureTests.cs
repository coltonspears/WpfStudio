using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed partial class DesignerTests
{
    private static PreviewProperty CaptureSizeProperty(string name, string value) => new(name, "System.Double", value,
        "Local", false, false, false, true, OwnerType: "System.Windows.FrameworkElement",
        OwnerAssembly: "PresentationFramework", EditableValue: value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotUpdatePreservesLatestPropertySelectionAndDraft(bool typeDuringInspection)
    {
        var client = new FakePreview();
        client.Inspect = request => Task.FromResult(new PreviewInspection(request.Version, Node(),
            [CaptureSizeProperty("Width", "100"), CaptureSizeProperty("Height", "30")], []));
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        var document = Document("view.xaml");
        await model.OpenAsync(document);
        model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties[0];
        model.EditedValue = "draft width";
        // Bound TreeView selection feedback occurs synchronously when reset.
        model.Tree.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) model.SelectedNode = null;
        };
        var pendingCapture = new TaskCompletionSource<PreviewSnapshot>();
        var pendingInspection = new TaskCompletionSource<PreviewInspection>();
        client.Capture = _ => pendingCapture.Task;
        if (typeDuringInspection) client.Inspect = _ => pendingInspection.Task;

        var update = model.UpdateSnapshotCommand.ExecuteAsync(null);
        Assert.False(model.UpdateSnapshotCommand.CanExecute(null));
        var version = Assert.Single(client.Captures).Version;
        if (typeDuringInspection) pendingCapture.SetResult(Snapshot(version, "Updated"));
        model.SelectedProperty = model.Properties[1];
        model.EditedValue = "draft height";
        if (!typeDuringInspection) pendingCapture.SetResult(Snapshot(version, "Updated"));
        else pendingInspection.SetResult(new(version, Node("Updated"), [CaptureSizeProperty("Width", "125"), CaptureSizeProperty("Height", "45")], []));
        await update;

        Assert.Single(client.Requests);
        Assert.Equal("Updated", model.SelectedNode!.Node.Name);
        Assert.Same(model.Tree[0], model.SelectedNode);
        Assert.Equal("Height", model.SelectedProperty!.Name);
        Assert.Equal("draft height", model.EditedValue);
        Assert.Equal(typeDuringInspection ? "45" : "30", model.SelectedProperty.Value);
        Assert.Equal("<TextBlock Text=\"Hello\" />", document.Content);
        Assert.True(model.IsCurrent);
        Assert.True(model.UpdateSnapshotCommand.CanExecute(null));
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSnapshotCannotReplaceChangedSourceOrNewRender(bool refresh)
    {
        var client = new FakePreview();
        var pending = new TaskCompletionSource<PreviewSnapshot>();
        client.Capture = _ => pending.Task;
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        await model.OpenAsync(document);
        var update = model.UpdateSnapshotCommand.ExecuteAsync(null);
        var oldRevision = client.Captures[0].Version;
        document.Content = "<Button />";
        if (refresh)
        {
            client.Render = request => Task.FromResult(Snapshot(request.Version, "New render"));
            await model.RefreshCommand.ExecuteAsync(null);
        }
        pending.SetResult(Snapshot(oldRevision, "Old capture"));
        await update;
        Assert.Equal(refresh, model.IsCurrent);
        if (refresh) Assert.Equal("New render", Assert.Single(model.Tree).Node.Name);
        else { Assert.Empty(model.Tree); Assert.Null(model.Image); }
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task SnapshotRemovingSelectedElementClearsInspectorAndDraft()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties[0];
        model.EditedValue = "Pending draft";
        client.Capture = request => Task.FromResult(Snapshot(request.Version) with { Nodes = [] });
        await model.UpdateSnapshotCommand.ExecuteAsync(null);
        Assert.Empty(model.Tree);
        Assert.Null(model.SelectedNode);
        Assert.Null(model.SelectedProperty);
        Assert.Empty(model.Properties);
        Assert.Empty(model.Bindings);
        Assert.Null(model.SelectionBounds);
        Assert.Equal("", model.EditedValue);
        Assert.True(model.IsCurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableOrMismatchedCaptureKeepsPreviousSnapshot(bool mismatched)
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        model.SelectedNode = model.Tree[0];
        model.SelectedProperty = model.Properties[0];
        model.EditedValue = "Draft";
        client.Capture = request => Task.FromResult(mismatched ? Snapshot(request.Version + 1, "Wrong")
            : new PreviewSnapshot(request.Version, false, null, 0, 0, [], [], "Unavailable"));
        await model.UpdateSnapshotCommand.ExecuteAsync(null);
        Assert.Equal("Greeting", Assert.Single(model.Tree).Node.Name);
        Assert.Equal("Draft", model.EditedValue);
        Assert.True(model.IsCurrent);
        Assert.False(model.IsBusy);
        Assert.Contains(mismatched ? "out of date" : "Unavailable", model.Status);
    }

    [Fact]
    public async Task SelectionChangeWhileCapturingCannotReplaceNewSelection()
    {
        var client = new FakePreview();
        var pending = new TaskCompletionSource<PreviewSnapshot>();
        client.Capture = _ => pending.Task;
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        model.SelectedNode = model.Tree[0];
        var update = model.UpdateSnapshotCommand.ExecuteAsync(null);
        model.SelectedNode = null;
        pending.SetResult(Snapshot(client.Captures[0].Version, "Outdated selection"));
        await update;
        Assert.Null(model.SelectedNode);
        Assert.Empty(model.Properties);
        Assert.Equal("Greeting", Assert.Single(model.Tree).Node.Name);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task UnavailableCaptureRestoresInspectionSupersededWhilePending()
    {
        var client = new FakePreview();
        var originalInspection = new TaskCompletionSource<PreviewInspection>();
        int inspections = 0;
        client.Inspect = request => ++inspections == 1 ? originalInspection.Task : Task.FromResult(Inspection(request.Version));
        client.Capture = request => Task.FromResult(new PreviewSnapshot(request.Version, false, null, 0, 0, [], [], "Unavailable"));
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        model.SelectedNode = model.Tree[0];
        Assert.Empty(model.Properties);
        await model.UpdateSnapshotCommand.ExecuteAsync(null);
        Assert.Equal(2, inspections);
        Assert.NotEmpty(model.Properties);
        originalInspection.SetResult(new(client.Requests[0].Version, Node(), [], [], "Old inspection"));
        await Task.Yield();
        Assert.NotEmpty(model.Properties);
        Assert.NotEqual("Old inspection", model.Status);
        Assert.True(model.IsCurrent);
    }

    [Fact]
    public async Task ElementRemovedBetweenCaptureAndInspectionKeepsUnavailableExplanation()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        model.SelectedNode = model.Tree[0];
        client.Inspect = request => Task.FromResult(new PreviewInspection(request.Version, null, [], [], "Element unloaded during observation"));
        await model.UpdateSnapshotCommand.ExecuteAsync(null);
        Assert.Equal("Element unloaded during observation", model.Status);
        Assert.Empty(model.Properties);
        Assert.Null(model.SelectedProperty);
    }
}
