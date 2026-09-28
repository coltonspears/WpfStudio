using System.Collections.Concurrent;
using System.Text.Json;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Runtime.Design;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

public sealed class DesignerScenarioTests
{
    [Fact]
    public async Task CatalogNeverSelectsOrExecutesFactoryUntilExplicitSelectionAndRefresh()
    {
        await using var context = new ScenarioContext();
        await context.WriteScenariosAsync(Data("Loading"), Data("Empty"), Data("Populated"), Data("Error"));
        await context.OpenAsync();
        Assert.Equal(["Default", "Loading", "Empty", "Populated", "Error"], context.Model.Scenarios.Select(scenario => scenario.Name));
        Assert.Null(Assert.Single(context.Client.Requests).Scenario);
        context.Select("Populated");
        Assert.False(context.Model.IsCurrent);
        Assert.Single(context.Client.Requests);
        Assert.Contains("Selected scenario: Populated", context.Model.ScenarioDescription);
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(Data("Populated"), context.Client.Requests[1].Scenario);
        Assert.Contains("Active scenario: Populated", context.Model.ScenarioDescription);
        Assert.True(context.Model.IsCurrent);
        Assert.False(context.Document.IsDirty);
    }

    [Fact]
    public async Task SourceAutoRefreshCannotExecuteANewlySelectedFactoryBeforeExplicitRefresh()
    {
        await using var context = new ScenarioContext();
        await context.WriteScenariosAsync(Data("Populated"));
        await context.OpenAsync();
        context.Model.AutoRefresh = true;
        context.Select("Populated");
        context.Document.Content += "\n";
        Assert.Single(context.Client.Requests);
        Assert.False(context.Model.IsBusy);
        Assert.Contains("Refresh", context.Model.Status);
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Populated", context.Client.Requests.Last().Scenario!.Name);
    }

    [Fact]
    public async Task NewSelectionRejectsPreviousRenderAndClearsInspectionAndLayout()
    {
        await using var context = new ScenarioContext();
        await context.WriteScenariosAsync(Data("Loading"), Data("Populated"));
        await context.OpenAsync();
        context.Model.SelectedNode = context.Model.Tree[0];
        Assert.NotEmpty(context.Model.Properties);
        var pending = new TaskCompletionSource<PreviewSnapshot>();
        var started = new TaskCompletionSource<PreviewRequest>();
        context.Client.Render = request => { started.SetResult(request); return pending.Task; };
        context.Select("Loading");
        var render = context.Model.RefreshCommand.ExecuteAsync(null);
        var request = await started.Task;
        context.Select("Populated");
        Assert.Empty(context.Model.Tree);
        Assert.Empty(context.Model.Properties);
        Assert.Null(context.Model.Layout);
        pending.SetResult(Snapshot(request));
        await render;
        Assert.False(context.Model.IsCurrent);
        Assert.Null(context.Model.Image);
        Assert.Contains("Selected scenario: Populated", context.Model.ScenarioDescription);
    }

    [Fact]
    public async Task ReloadDoesNotRenderAndRemovedSelectionNeverRunsAnotherFactory()
    {
        await using var context = new ScenarioContext();
        await context.WriteScenariosAsync(Data("Loading"));
        await context.OpenAsync();
        context.Select("Loading");
        await context.Model.RefreshCommand.ExecuteAsync(null);
        await context.WriteScenariosAsync(Data("Different"));
        await context.Model.ReloadScenariosCommand.ExecuteAsync(null);
        Assert.Equal(2, context.Client.Requests.Count);
        Assert.Null(context.Model.SelectedScenario!.Configuration);
        Assert.Contains("no longer available", context.Model.Status);
        Assert.Contains(context.Model.ScenarioWarnings, warning => warning.Contains("Loading"));
        Assert.False(context.Model.IsCurrent);
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Null(context.Client.Requests.Last().Scenario);
    }

    [Fact]
    public async Task RefreshReadsChangedFactoryForSameSelectedNameAndRejectsOldConfigurationReply()
    {
        await using var context = new ScenarioContext();
        await context.WriteScenariosAsync(Data("Populated"));
        await context.OpenAsync();
        context.Select("Populated");
        var pending = new TaskCompletionSource<PreviewSnapshot>();
        var started = new TaskCompletionSource<PreviewRequest>();
        context.Client.Render = request => { started.SetResult(request); return pending.Task; };
        var operation = context.Model.RefreshCommand.ExecuteAsync(null);
        var request = await started.Task;
        var timestamp = File.GetLastWriteTimeUtc(context.ConfigurationPath);
        long length = new FileInfo(context.ConfigurationPath).Length;
        var changed = Data("Populated") with { DataContextFactory = new("Demo.ChangedData", "Populated") };
        await context.WriteScenariosAsync(changed);
        Assert.Equal(length, new FileInfo(context.ConfigurationPath).Length);
        File.SetLastWriteTimeUtc(context.ConfigurationPath, timestamp);
        pending.SetResult(Snapshot(request));
        await operation;
        Assert.False(context.Model.IsCurrent);
        Assert.Null(context.Model.Image);
        context.Client.Render = request => Task.FromResult(Snapshot(request));
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(changed, context.Client.Requests.Last().Scenario);
        Assert.True(context.Model.IsCurrent);
    }

    [Fact]
    public async Task SameViewAndProjectKeepSelectionDifferentViewOrProjectResetIt()
    {
        await using var context = new ScenarioContext();
        await context.WriteScenariosAsync(Data("Populated"));
        await context.OpenAsync();
        context.Select("Populated");
        await context.OpenAsync();
        Assert.Equal("Populated", context.Client.Requests.Last().Scenario!.Name);
        await context.Model.OpenAsync(new(Path.Combine(context.Root, "Other.xaml"), "<Grid />"), projectDirectory: context.Root);
        Assert.Null(context.Client.Requests.Last().Scenario);
        await context.OpenAsync();
        context.Select("Populated");
        string otherProject = Path.Combine(context.Root, "OtherProject");
        Directory.CreateDirectory(otherProject);
        context.Model.ProjectDirectory = otherProject;
        Assert.False(context.Model.IsCurrent);
        Assert.Null(context.Model.SelectedScenario!.Configuration);
    }

    [Fact]
    public async Task ViewFactoryRequiresExplicitCompiledModeAndResourceInputsInvalidateSourceScenario()
    {
        await using var context = new ScenarioContext();
        var factory = new PreviewScenario("Injected", new("Demo.Views", "Create"));
        await context.WriteScenariosAsync(factory, Data("Populated"));
        await context.OpenAsync();
        context.Select("Injected");
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Single(context.Client.Requests);
        Assert.Contains("Compiled", context.Model.Status);
        context.Model.Mode = PreviewMode.Compiled;
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(factory, context.Client.Requests.Last().Scenario);
        Assert.Equal(PreviewMode.Compiled, context.Client.Requests.Last().Mode);
        context.Model.Mode = PreviewMode.Source;
        context.Select("Populated");
        Assert.True(context.Model.UsesApplicationResources);
        await context.Model.RefreshCommand.ExecuteAsync(null);
        context.Model.ApplicationResourcePath = "";
        Assert.False(context.Model.IsCurrent);
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Null(context.Client.Requests.Last().ApplicationResourcePath);
        context.Model.AssemblyPath = Path.Combine(context.Root, "new-build.dll");
        Assert.False(context.Model.IsCurrent);
    }

    [Fact]
    public async Task DesignTimeValuesInvalidateSourcePreviewAndAreCarriedWithoutChangingSource()
    {
        await using var context = new ScenarioContext();
        await context.OpenAsync();
        Assert.True(context.Client.Requests.Last().UseDesignTimeValues);
        var original = context.Document.Content;
        context.Model.UseDesignTimeValues = false;
        Assert.False(context.Model.IsCurrent);
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.False(context.Client.Requests.Last().UseDesignTimeValues);
        Assert.Equal(original, context.Document.Content);
        Assert.False(context.Document.IsDirty);
    }

    [Fact]
    public async Task WrongScenarioProvenanceCannotBecomeCurrentAndFailedScenarioRemainsSelected()
    {
        await using var context = new ScenarioContext();
        await context.WriteScenariosAsync(Data("Error"));
        await context.OpenAsync();
        context.Select("Error");
        context.Client.Render = request => Task.FromResult(Snapshot(request) with { Scenario = null });
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.False(context.Model.IsCurrent);
        Assert.Contains("confirm", context.Model.Status);
        context.Client.Render = request => Task.FromResult(new PreviewSnapshot(request.Version, false, null, 0, 0, [],
            [new("Factory failed")], "Factory failed"));
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.False(context.Model.IsCurrent);
        Assert.Equal("Error", context.Model.SelectedScenario!.Name);
        Assert.Contains(context.Model.Diagnostics, diagnostic => diagnostic.Message == "Factory failed");
    }

    [Fact]
    public async Task ConfigurationDiagnosticsAreVisibleAndOpenOnlyTargetsExistingFile()
    {
        await using var context = new ScenarioContext();
        await context.OpenAsync();
        Assert.False(context.Model.OpenScenarioConfigurationCommand.CanExecute(null));
        await File.WriteAllTextAsync(context.ConfigurationPath, "{ broken");
        await context.Model.ReloadScenariosCommand.ExecuteAsync(null);
        Assert.NotEmpty(context.Model.ScenarioWarnings);
        Assert.Single(context.Model.Scenarios);
        SourceLocation? requested = null;
        context.Model.SourceRequested += source => requested = source;
        Assert.True(context.Model.OpenScenarioConfigurationCommand.CanExecute(null));
        context.Model.OpenScenarioConfigurationCommand.Execute(null);
        Assert.Equal(context.ConfigurationPath, requested!.Path);
        await context.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Contains(context.Model.Diagnostics, diagnostic => diagnostic.Severity == "Warning");
    }

    [Fact]
    public async Task ManifestWatcherInvalidatesCurrentSnapshotAndCloseDetachesIt()
    {
        await using var context = new ScenarioContext();
        await context.WriteScenariosAsync(Data("Loading"));
        await context.OpenAsync();
        await context.WriteScenariosAsync(Data("Populated"));
        await context.Dispatcher.WaitForPostAsync();
        context.Dispatcher.Drain();
        for (int attempt = 0; context.Model.IsCurrent && attempt < 100; attempt++) await Task.Delay(10);
        Assert.False(context.Model.IsCurrent);
        Assert.Null(context.Model.Image);
        await context.Model.CloseAsync();
        Assert.Null(context.Model.ScenarioConfigurationPath);
        Assert.Single(context.Model.Scenarios);
        Assert.Null(context.Model.SelectedScenario!.Configuration);
        context.Dispatcher.Drain();
        Assert.Contains("stopped", context.Model.Status);
    }

    [Fact]
    public async Task ShellUsesExplicitLinkedProjectForScenarioCatalogAndAssembly()
    {
        var client = new ScenarioPreview();
        await using var context = new ShellTestContext(client);
        var path = Path.GetFullPath(await context.CreateFileAsync("Shared/View.xaml", "<Grid />"));
        string first = Path.Combine(context.Root, "First"), second = Path.Combine(context.Root, "Second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        await WriteCatalogAsync(second, "../Shared/View.xaml", Data("Second project"));
        var owners = new[] { Project(first, path), Project(second, path) };
        foreach (var owner in owners) context.Shell.Projects.Add(owner);
        await context.Shell.OpenDocumentAsync(path);
        var editor = context.Shell.ActiveDocument!;
        editor.SetXamlProjects(owners);
        await context.Shell.OpenDesignerCommand.ExecuteAsync(null);
        Assert.Empty(client.Requests);
        editor.XamlProject = owners[1];
        await context.Shell.OpenDesignerCommand.ExecuteAsync(null);
        Assert.Equal(second, Assert.Single(client.Requests).ProjectDirectory);
        Assert.Contains(context.Shell.Designer.Scenarios, scenario => scenario.Name == "Second project");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingLinkedProjectOrFrameworkClosesPendingPreviewBeforeExplicitReopen(bool sameProject)
    {
        var client = new ScenarioPreview();
        await using var context = new ShellTestContext(client);
        var path = Path.GetFullPath(await context.CreateFileAsync("Shared/View.xaml", "<Grid />"));
        string first = Path.Combine(context.Root, "First"), second = Path.Combine(context.Root, "Second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var owners = new[] { Project(first, path), sameProject ? Project(first, path) with { Id = "other-framework", TargetFramework = "net9.0-windows" } : Project(second, path) };
        foreach (var owner in owners) context.Shell.Projects.Add(owner);
        await context.Shell.OpenDocumentAsync(path);
        var editor = context.Shell.ActiveDocument!;
        editor.SetXamlProjects(owners); editor.XamlProject = owners[0];
        await context.Shell.OpenDesignerCommand.ExecuteAsync(null);
        var model = context.Shell.Designer;
        Assert.True(model.IsCurrent);

        var pendingRender = new TaskCompletionSource<PreviewSnapshot>();
        var rendering = new TaskCompletionSource<PreviewRequest>();
        client.Render = request => { rendering.SetResult(request); return pendingRender.Task; };
        var refresh = model.RefreshCommand.ExecuteAsync(null);
        var oldRequest = await rendering.Task;
        var stopped = new TaskCompletionSource();
        client.Stop = () => stopped.Task;
        try
        {
            editor.XamlProject = owners[1];
            Assert.False(model.IsCurrent);
            Assert.Null(model.SourcePath);
            Assert.Empty(model.Tree);
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, client.Requests.Count);

            var reopen = context.Shell.OpenDesignerCommand.ExecuteAsync(null);
            Assert.False(reopen.IsCompleted);
            pendingRender.SetResult(Snapshot(oldRequest));
            await refresh;
            Assert.False(model.IsCurrent);
            client.Render = request => Task.FromResult(Snapshot(request));
            stopped.SetResult();
            await reopen;
            Assert.True(model.IsCurrent);
            Assert.Equal(sameProject ? first : second, client.Requests.Last().ProjectDirectory);
            Assert.Equal(3, client.Requests.Count);
        }
        finally { pendingRender.TrySetResult(Snapshot(oldRequest)); stopped.TrySetResult(); }
    }

    private static WorkspaceProject Project(string directory, string source) => new(directory, Path.GetFileName(directory),
        Path.Combine(directory, "Demo.csproj"), "net10.0-windows", Path.Combine(directory, "Demo.dll"), false,
        [new(source, "View.xaml", "Page")], "Demo");
    private static PreviewScenario Data(string name) => new(name, DataContextFactory: new("Demo.PreviewData", name.Replace(" ", "")));
    private static async Task WriteCatalogAsync(string root, string path, params PreviewScenario[] scenarios)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        await File.WriteAllTextAsync(Path.Combine(root, PreviewScenarioCatalog.FileName),
            JsonSerializer.Serialize(new { version = 1, views = new[] { new { path, scenarios } } }, options));
    }
    private static PreviewNode Node => new("1", null, null, "System.Windows.Controls.TextBlock", "Preview", new(0, 0, 100, 30), null, true);
    private static PreviewSnapshot Snapshot(PreviewRequest request) => new(request.Version, true, [1], 100, 30, [Node], [],
        Scenario: request.Scenario is { } scenario ? new(scenario, "Demo.dll", "0123456789abcdef", "module", "Demo.View") : null);
    private sealed class ScenarioPreview : IPreviewClient
    {
        public List<PreviewRequest> Requests { get; } = [];
        public Func<PreviewRequest, Task<PreviewSnapshot>> Render { get; set; } = request => Task.FromResult(Snapshot(request));
        public Func<Task> Stop { get; set; } = () => Task.CompletedTask;
        public event EventHandler<string>? Disconnected { add { } remove { } }
        public Task<PreviewSnapshot> RenderAsync(PreviewRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return Render(request); }
        public Task<PreviewInspection> InspectAsync(PreviewNodeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PreviewInspection(request.Version, Node, [new("Text", "System.String", "Hello", "Local", false, false, false, true)], []));
        public Task<PreviewInspection> PickAsync(PreviewPickRequest request, CancellationToken cancellationToken) =>
            InspectAsync(new(request.Version, "1"), cancellationToken);
        public Task<PreviewEditResult> SetPropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PreviewPropertyValidation> ValidatePropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken) => Task.FromResult(new PreviewPropertyValidation(true));
        public Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AppearanceResponse(request, AppearanceSnapshot.Unavailable("Appearance is not configured in this test.")));
        public Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new BindingSourceResponse(request, false, Status: "Binding source is not configured in this test."));
        public Task StopAsync(CancellationToken cancellationToken = default) => Stop();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ScenarioContext : IAsyncDisposable
    {
        private readonly ShellTestContext _files = new();
        public string Root => _files.Root;
        public string ConfigurationPath => Path.Combine(Root, PreviewScenarioCatalog.FileName);
        public ScenarioPreview Client { get; } = new();
        public PostedDispatcher Dispatcher { get; } = new();
        public DesignerViewModel Model { get; }
        public DocumentState Document { get; }
        public ScenarioContext()
        {
            Model = new(Client, Dispatcher) { AutoRefresh = false };
            Document = new(Path.Combine(Root, "View.xaml"), "<Grid />");
            Document.MarkSaved(DocumentStore.Hash(System.Text.Encoding.UTF8.GetBytes(Document.Content)));
        }
        public Task WriteScenariosAsync(params PreviewScenario[] scenarios) => WriteCatalogAsync(Root, "View.xaml", scenarios);
        public Task OpenAsync() => Model.OpenAsync(Document, projectDirectory: Root);
        public void Select(string name) => Model.SelectedScenario = Assert.Single(Model.Scenarios, scenario => scenario.Name == name);
        public async ValueTask DisposeAsync() { await Model.DisposeAsync(); await _files.DisposeAsync(); }
    }
    private sealed class PostedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Post(Action action) { _queue.Enqueue(action); _posted.TrySetResult(); }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { action(); return Task.CompletedTask; }
        public Task WaitForPostAsync() => _posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Drain() { while (_queue.TryDequeue(out var action)) action(); }
    }
}

public sealed partial class DesignerTests
{
    [Fact]
    public async Task LinkedProjectChangeImmediatelyInvalidatesPendingDesignerSourceReview()
    {
        await using var context = new ShellTestContext(WritablePreview());
        var path = Path.GetFullPath(await context.CreateFileAsync("Shared/View.xaml", BoundSource));
        var owners = new[] { "First", "Second" }.Select(name => new WorkspaceProject(name, name,
            Path.Combine(context.Root, name, "Demo.csproj"), "net10.0-windows", null, false,
            [new(path, "View.xaml", "Page")])).ToArray();
        foreach (var owner in owners) context.Shell.Projects.Add(owner);
        await context.Shell.OpenDocumentAsync(path);
        var editor = context.Shell.ActiveDocument!;
        editor.SetXamlProjects(owners); editor.XamlProject = owners[0];
        await context.Shell.OpenDesignerCommand.ExecuteAsync(null);
        var model = context.Shell.Designer;
        SelectWritableProperty(model);
        model.EditedValue = "Updated";
        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.True(context.Shell.IsPreviewOpen, model.Status);
        editor.XamlProject = owners[1];
        Assert.False(model.IsCurrent);
        Assert.Null(model.SourcePath);
        context.Shell.AcceptPreviewCommand.Execute(null);
        await pending;
        Assert.Equal(BoundSource, editor.State.Content);
        Assert.Contains("project context changed", model.Status);
    }

    [Fact]
    public async Task ScenarioChangeDuringSourceDiffRejectsThePendingWorkspaceEdit()
    {
        await using var context = new ShellTestContext(WritablePreview());
        var path = await context.CreateFileAsync("View.xaml", BoundSource);
        await context.CreateFileAsync(PreviewScenarioCatalog.FileName, """
            {"version":1,"views":[{"path":"View.xaml","scenarios":[{"name":"Populated","dataContextFactory":{"typeName":"Demo.PreviewData","methodName":"Create"}}]}]}
            """);
        await context.Shell.OpenDocumentAsync(path);
        var model = context.Shell.Designer;
        model.AutoRefresh = false;
        await model.OpenAsync(context.Shell.ActiveDocument!.State, projectDirectory: context.Root);
        SelectWritableProperty(model);
        model.EditedValue = "Updated";
        var pending = model.WritePropertyToSourceCommand.ExecuteAsync(null);
        Assert.True(context.Shell.IsPreviewOpen, model.Status);
        model.SelectedScenario = Assert.Single(model.Scenarios, scenario => scenario.Name == "Populated");
        context.Shell.AcceptPreviewCommand.Execute(null);
        await pending;
        Assert.Equal(BoundSource, context.Shell.ActiveDocument!.State.Content);
        Assert.Contains("preview changed", model.Status);
        Assert.False(model.IsCurrent);
    }
}
