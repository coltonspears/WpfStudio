using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Runtime.Design;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

public sealed partial class DesignerTests
{
    [Fact]
    public async Task OlderRenderCannotReplaceNewDocument()
    {
        var client = new FakePreview();
        var first = new TaskCompletionSource<PreviewSnapshot>(); var second = new TaskCompletionSource<PreviewSnapshot>();
        client.Render = request => client.Requests.Count == 1 ? first.Task : second.Task;
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        var oldRender = model.OpenAsync(Document("old.xaml"));
        var newRender = model.OpenAsync(Document("new.xaml"));
        second.SetResult(Snapshot(client.Requests[1].Version, "New"));
        await newRender;
        first.SetResult(Snapshot(client.Requests[0].Version, "Old"));
        await oldRender;
        Assert.Equal("New", Assert.Single(model.Tree).Node.Name);
        Assert.Equal("new.xaml", model.DocumentName);
        Assert.True(model.IsCurrent);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task PausingAutoRefreshThenEditingCancelsBusyState()
    {
        var client = new FakePreview();
        var pending = new TaskCompletionSource<PreviewSnapshot>();
        client.Render = _ => pending.Task;
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        var document = Document("view.xaml");
        var render = model.OpenAsync(document);
        Assert.True(model.IsBusy);
        model.AutoRefresh = false;
        document.Content = "<Grid />";
        Assert.False(model.IsBusy);
        pending.SetResult(Snapshot(client.Requests[0].Version));
        await render;
        Assert.False(model.IsCurrent);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task SourceChangeInvalidatesPickingAndPendingInspection()
    {
        var client = new FakePreview();
        var pending = new TaskCompletionSource<PreviewInspection>();
        client.Inspect = _ => pending.Task;
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        await model.OpenAsync(document);
        model.SelectedNode = model.Tree[0];
        document.Content = "<Grid />";
        pending.SetResult(Inspection(client.Requests[0].Version));
        await Task.Yield();
        await model.PickAsync(5, 5);
        Assert.False(model.IsCurrent);
        Assert.Empty(model.Properties);
        Assert.Empty(model.Tree);
        Assert.Null(model.Image);
        Assert.Equal(0, client.Picks);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task PickSynchronizesTreeAndShowsPropertyAndBindingProvenance()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        await model.PickAsync(12, 24);
        Assert.True(model.SelectedNode!.IsSelected);
        Assert.Same(model.Tree[0], model.SelectedNode);
        Assert.Equal("Local", Assert.Single(model.Properties).ValueSource);
        Assert.Equal("Name", Assert.Single(model.Bindings).BindingPath);
        Assert.Equal("Active", model.Bindings[0].BindingStatus);
        Assert.NotNull(model.SelectionBounds);
    }

    [Fact]
    public async Task PathlessBindingRemainsVisibleInBindingsTab()
    {
        var client = new FakePreview
        {
            Inspect = request => Task.FromResult(new PreviewInspection(request.Version, Node(),
                [new("Text", "System.String", "Hello", "Local", true, false, false, true, BindingStatus: "Active")], []))
        };
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        model.SelectedNode = model.Tree[0];
        Assert.Null(Assert.Single(model.Bindings).BindingPath);
    }

    [Fact]
    public async Task PreviewEditsUseCurrentGenerationAndKeepSourceUnchanged()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        var document = Document("view.xaml"); var original = document.Content; var version = document.Version;
        await model.OpenAsync(document);
        await model.PickAsync(0, 0);
        model.SelectedProperty = model.Properties[0]; model.EditedValue = "Temporary";
        await model.ApplyPropertyCommand.ExecuteAsync(null);
        Assert.Equal("Temporary", Assert.Single(client.Edits).Value);
        Assert.Equal(client.Requests[0].Version, client.Edits[0].Version);
        Assert.False(client.Edits[0].Reset);
        await model.ResetPropertyCommand.ExecuteAsync(null);
        Assert.True(client.Edits[1].Reset);
        Assert.Equal(original, document.Content);
        Assert.Equal(version, document.Version);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PendingPreviewEditRefreshesValuesWithoutReplacingNewerPropertyOrInput(bool switchProperty, bool reset)
    {
        static PreviewProperty SizeProperty(string name, string value) => new(name, "System.Double", value,
            "Local", false, false, false, true, OwnerType: "System.Windows.FrameworkElement",
            OwnerAssembly: "PresentationFramework", EditableValue: value);
        var pending = new TaskCompletionSource<PreviewEditResult>();
        var client = new FakePreview
        {
            Inspect = request => Task.FromResult(new PreviewInspection(request.Version, Node(),
                [SizeProperty("Width", "100"), SizeProperty("Height", "30")], [])),
            Edit = _ => pending.Task
        };
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        model.SelectedNode = model.Tree[0];
        model.SelectedProperty = Assert.Single(model.Properties, property => property.Name == "Width");
        model.EditedValue = "120";

        var operation = reset ? model.ResetPropertyCommand.ExecuteAsync(null) : model.ApplyPropertyCommand.ExecuteAsync(null);
        var request = Assert.Single(client.Edits);
        Assert.Equal(reset, request.Reset);
        if (switchProperty)
            model.SelectedProperty = Assert.Single(model.Properties, property => property.Name == "Height");
        string newerInput = switchProperty ? "60" : "145";
        model.EditedValue = newerInput;

        pending.SetResult(new PreviewEditResult(true, Snapshot(request.Version), new PreviewInspection(request.Version,
            Node(), [SizeProperty("Width", "120"), SizeProperty("Height", "35")], [])));
        await operation;

        var selected = Assert.Single(model.Properties, property => property.Name == (switchProperty ? "Height" : "Width"));
        Assert.Same(selected, model.SelectedProperty);
        Assert.Equal(newerInput, model.EditedValue);
        Assert.Equal("120", Assert.Single(model.Properties, property => property.Name == "Width").Value);
        Assert.Equal("35", Assert.Single(model.Properties, property => property.Name == "Height").Value);
        Assert.True(model.IsCurrent);
    }

    [Fact]
    public async Task CloseDetachesSourceAndStopsOwnedSession()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        var document = Document("view.xaml");
        await model.OpenAsync(document);
        await model.CloseAsync();
        document.Content = "<Button />";
        Assert.Single(client.Requests);
        Assert.Equal(1, client.Stops);
        Assert.Null(model.SourcePath);
        Assert.False(model.IsCurrent);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task HostExitClearsDeadSelectionsAndCanRefresh()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher());
        await model.OpenAsync(Document("view.xaml"));
        await model.PickAsync(0, 0);
        client.Exit("Control failed");
        Assert.False(model.IsCurrent);
        Assert.Null(model.SelectedNode);
        Assert.Empty(model.Properties);
        Assert.Equal("Control failed", model.Status);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.IsCurrent);
    }

    [Fact]
    public async Task SourceSelectionAndNavigationRequireCurrentDocument()
    {
        var client = new FakePreview();
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        var document = Document("view.xaml");
        client.Render = request => Task.FromResult(Snapshot(request.Version, source: new(document.Path, 2, 5, 1, 3)));
        await model.OpenAsync(document);
        SourceLocation? destination = null; model.SourceRequested += value => destination = value;
        model.SelectSource(document.Path, 4);
        Assert.NotNull(model.SelectedNode);
        model.GoToSourceCommand.Execute(null);
        Assert.Equal(2, destination!.Start);
        destination = null;
        document.Content = "<Grid />";
        model.SelectSource(document.Path, 4); model.GoToSourceCommand.Execute(null);
        Assert.Null(destination);
    }

    private static DocumentState Document(string name) => new(Path.Combine(Path.GetTempPath(), name), "<TextBlock Text=\"Hello\" />");
    private static PreviewNode Node(string name = "Greeting", SourceLocation? source = null) => new("1", null, null, "System.Windows.Controls.TextBlock", name, new(0, 0, 100, 30), source, true);
    private static PreviewSnapshot Snapshot(long version, string name = "Greeting", SourceLocation? source = null) => new(version, true, [1], 100, 30, [Node(name, source)], []);
    private static PreviewInspection Inspection(long version) => new(version, Node(),
        [new("Text", "System.String", "Hello", "Local", true, false, false, true, "Name", "Active", "Demo.ViewModel")], []);
    private sealed class FakePreview : IPreviewClient
    {
        public List<PreviewRequest> Requests { get; } = [];
        public List<PreviewCaptureRequest> Captures { get; } = [];
        public Func<PreviewCaptureRequest, Task<PreviewSnapshot>> Capture { get; set; } = request => Task.FromResult(Snapshot(request.Version));
        public List<PreviewPropertyEdit> Edits { get; } = [];
        public List<PreviewLayoutValidationRequest> LayoutValidations { get; } = [];
        public Func<PreviewLayoutValidationRequest, Task<PreviewLayoutValidationResult>> ValidateLayout { get; set; } = request => Task.FromResult(new PreviewLayoutValidationResult(request, true));
        public List<AppearanceRequest> AppearanceRequests { get; } = [];
        public List<CancellationToken> AppearanceTokens { get; } = [];
        public List<BindingSourceRequest> BindingSourceRequests { get; } = [];
        public Func<BindingSourceRequest, Task<BindingSourceResponse>> BindingSource { get; set; } = request =>
            Task.FromResult(new BindingSourceResponse(request, false, Status: "Binding source is not configured in this test."));
        public Func<AppearanceRequest, Task<AppearanceResponse>> Appearance { get; set; } = request =>
            Task.FromResult(new AppearanceResponse(request, AppearanceSnapshot.Unavailable("Appearance is not configured in this test.")));
        public int Picks { get; private set; }
        public int Stops { get; private set; }
        public Func<PreviewSurfaceIdentity, IPreviewInteractionSession?> Interaction { get; set; } = _ => null;
        public IPreviewInteractionSession? CreateInteractionSession(PreviewSurfaceIdentity surface) => Interaction(surface);
        public Func<PreviewRequest, Task<PreviewSnapshot>> Render { get; set; } = request => Task.FromResult(Snapshot(request.Version));
        public Func<PreviewNodeRequest, Task<PreviewInspection>> Inspect { get; set; } = request => Task.FromResult(Inspection(request.Version));
        public Func<PreviewPickRequest, Task<PreviewInspection>> Pick { get; set; } = request => Task.FromResult(Inspection(request.Version));
        public Func<PreviewPropertyEdit, Task<PreviewPropertyValidation>> Validate { get; set; } = _ => Task.FromResult(new PreviewPropertyValidation(true));
        public Func<PreviewPropertyEdit, Task<PreviewEditResult>> Edit { get; set; } = request =>
            Task.FromResult(new PreviewEditResult(true, Snapshot(request.Version), Inspection(request.Version)));
        public event EventHandler<string>? Disconnected;
        public void Exit(string message) => Disconnected?.Invoke(this, message);
        public Task<PreviewSnapshot> RenderAsync(PreviewRequest request, CancellationToken cancellationToken) { Requests.Add(request); return Render(request); }
        public Task<PreviewSnapshot> CaptureAsync(PreviewCaptureRequest request, CancellationToken cancellationToken) { Captures.Add(request); return Capture(request); }
        public Task<PreviewInspection> InspectAsync(PreviewNodeRequest request, CancellationToken cancellationToken) => Inspect(request);
        public Task<PreviewInspection> PickAsync(PreviewPickRequest request, CancellationToken cancellationToken) { Picks++; return Pick(request); }
        public Task<PreviewEditResult> SetPropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken)
        { Edits.Add(request); return Edit(request); }
        public Task<PreviewPropertyValidation> ValidatePropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken) => Validate(request);
        public Task<PreviewLayoutValidationResult> ValidateLayoutEditAsync(PreviewLayoutValidationRequest request, CancellationToken cancellationToken)
        { LayoutValidations.Add(request); return ValidateLayout(request); }
        public Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request, CancellationToken cancellationToken)
        { AppearanceRequests.Add(request); AppearanceTokens.Add(cancellationToken); return Appearance(request); }
        public Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request, CancellationToken cancellationToken)
        { BindingSourceRequests.Add(request); return BindingSource(request); }
        public Task StopAsync(CancellationToken cancellationToken = default) { Stops++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
