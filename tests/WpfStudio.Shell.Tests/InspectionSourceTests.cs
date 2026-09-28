using WpfStudio.App.Features.Inspection;
using WpfStudio.App.ViewModels;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

public sealed class InspectionSourceVerificationTests
{
    private const string Source = "<Button />";

    [Fact]
    public async Task VerifiedLeaseNavigatesTheSameUnchangedBufferAfterActivation()
    {
        await using var context = new ShellTestContext();
        var path = await context.CreateFileAsync("View.xaml", Source);
        var document = await context.Store.OpenAsync(path);
        using var source = Lease(context.Store, document);
        Assert.True(source.IsCurrent());
        var status = context.Shell.NavigateVerifiedInspectionSource(source);
        Assert.StartsWith("Opened verified source location:", status);
        Assert.Same(document, context.Shell.ActiveDocument!.State);
        Assert.Equal(source.Location.Location!.Start, document.CaretOffset);
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("edit-round-trip")]
    [InlineData("context")]
    [InlineData("closed")]
    [InlineData("active-editor")]
    public async Task EditorActivationCannotNavigateAChangedVerificationLease(string change)
    {
        await using var context = new ShellTestContext();
        var path = await context.CreateFileAsync("View.xaml", Source);
        var document = await context.Store.OpenAsync(path);
        bool current = true;
        using var source = Lease(context.Store, document, () => current);
        int navigations = 0;
        bool activated = false;
        context.Shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ShellViewModel.ActiveDocument) || activated ||
                context.Shell.ActiveDocument?.State != document) return;
            activated = true;
            context.Shell.ActiveDocument.NavigationRequested += () => navigations++;
            switch (change)
            {
                case "edited": document.Content = "<Grid />"; break;
                case "edit-round-trip": document.Content = "<Grid />"; document.Content = Source; break;
                case "context": current = false; break;
                case "closed": context.Store.Close(document); break;
                case "active-editor": context.Shell.ActiveDocument = null; break;
            }
        };
        var status = context.Shell.NavigateVerifiedInspectionSource(source);
        Assert.True(activated);
        Assert.Contains("navigation cancelled", status);
        Assert.Equal(change == "active-editor", source.IsCurrent());
        Assert.Equal(0, navigations);
        Assert.Equal(0, document.CaretOffset);
        Assert.Equal(change == "edited" ? "<Grid />" : Source, document.Content);
        Assert.Equal(Source, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReopeningIdenticalTextAndVersionCannotReviveThePreviousLease()
    {
        await using var context = new ShellTestContext();
        var path = await context.CreateFileAsync("View.xaml", Source);
        var original = await context.Store.OpenAsync(path);
        using var source = Lease(context.Store, original);
        context.Store.Close(original);
        var replacement = await context.Store.OpenAsync(path);
        Assert.NotSame(original, replacement);
        Assert.Equal(original.Version, replacement.Version);
        Assert.Equal(original.Content, replacement.Content);
        Assert.False(source.IsCurrent());
        var status = context.Shell.NavigateVerifiedInspectionSource(source);
        Assert.Contains("navigation cancelled", status);
        Assert.Empty(context.Shell.Documents);
    }

    private static ShellViewModel.VerifiedInspectionSource Lease(DocumentStore store, DocumentState document, Func<bool>? current = null) =>
        new(store, document, Source, XamlRuntimeSourceLocator.Locate(document.Path, Source, 1, 1, "System.Windows.Controls.Button", null),
            new("Demo", "Demo", Path.Combine(Path.GetDirectoryName(document.Path)!, "Demo.dll"), Guid.NewGuid()),
            current ?? (() => true), new FileStream(document.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
}

public sealed partial class InspectionViewModelTests
{
    private static InspectionNode SourceNode(string id) => Node(id) with
    {
        Source = new($"pack://application:,,,/Demo;component/{id}.xaml", 2, 5)
    };
    private static FakeSession SourceSession() => new() { Nodes = [SourceNode("first"), SourceNode("second")] };

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task SourceNavigationRequiresHintCapabilityAndHostCallback(bool source, bool capability, bool callback)
    {
        var session = SourceSession();
        if (!source) session.Nodes = [Node("first")];
        if (!capability) session.Hello = session.Hello with { Capabilities = ["tree"] };
        var modules = new InspectionModuleCatalog([]);
        session.Modules = _ => Task.FromResult(modules);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int navigations = 0;
        if (callback) model.SourceRequested += (request, _) =>
        {
            navigations++;
            Assert.Equal(session.Nodes[0], request.Node);
            Assert.Same(modules, request.Modules);
            Assert.True(request.IsCurrent());
            return Task.FromResult("Opened verified XAML");
        };
        await model.AttachAsync(session);
        model.SelectedNode = model.Tree[0];
        bool enabled = source && capability && callback;
        Assert.Equal(enabled, model.ShowSourceCommand.CanExecute(null));
        await model.ShowSourceCommand.ExecuteAsync(null);
        Assert.Equal(enabled ? 1 : 0, session.ModuleReads);
        Assert.Equal(enabled ? 1 : 0, navigations);
        if (enabled) Assert.Equal("Opened verified XAML", model.SourceDescription);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("selection-round-trip")]
    [InlineData("source-changed")]
    [InlineData("disconnect")]
    [InlineData("pause")]
    [InlineData("pause-resume")]
    [InlineData("session")]
    public async Task DelayedModuleCatalogCannotNavigateAnObsoleteSelectionOrSession(string change)
    {
        var ready = new TaskCompletionSource<InspectionModuleCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = SourceSession();
        session.Modules = _ => ready.Task; // Deliberately ignores cancellation, like a late transport response.
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int navigations = 0;
        model.SourceRequested += (_, _) => { navigations++; return Task.FromResult("Stale navigation"); };
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0];
        var pending = model.ShowSourceCommand.ExecuteAsync(null);
        Assert.Equal(1, session.ModuleReads);
        Assert.False(model.ShowSourceCommand.CanExecute(null));
        await ChangeSourceContextAsync(model, session, change);
        Assert.DoesNotContain("Verifying compiled XAML source", model.SourceDescription);
        ready.SetResult(new([]));
        await pending;
        Assert.Equal(0, navigations);
        Assert.DoesNotContain("Stale navigation", model.SourceDescription);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("selection-round-trip")]
    [InlineData("source-changed")]
    [InlineData("disconnect")]
    [InlineData("pause")]
    [InlineData("pause-resume")]
    [InlineData("session")]
    public async Task SourceCallbackReceivesLiveStalenessGuardAndCannotReplaceNewStatus(string change)
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = SourceSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        InspectionSourceRequest? captured = null;
        model.SourceRequested += (request, _) => { captured = request; return ready.Task; };
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0];
        var pending = model.ShowSourceCommand.ExecuteAsync(null);
        Assert.NotNull(captured);
        Assert.True(captured.IsCurrent());
        await ChangeSourceContextAsync(model, session, change);
        Assert.False(captured.IsCurrent());
        Assert.DoesNotContain("Verifying compiled XAML source", model.SourceDescription);
        string currentStatus = model.SourceDescription;
        ready.SetResult("Opened stale XAML");
        await pending;
        Assert.Equal(currentStatus, model.SourceDescription);
        Assert.DoesNotContain("Opened stale XAML", model.SourceDescription);
    }

    [Fact]
    public async Task DelayedModuleCatalogSurvivesRefreshWhenTheSelectedSourceIsUnchanged()
    {
        var ready = new TaskCompletionSource<InspectionModuleCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = SourceSession();
        session.Modules = _ => ready.Task;
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        int navigations = 0;
        model.SourceRequested += (request, _) =>
        {
            navigations++;
            Assert.Equal("first", request.Node.Id);
            Assert.True(request.IsCurrent());
            return Task.FromResult("Opened source after refresh");
        };
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0];
        var pending = model.ShowSourceCommand.ExecuteAsync(null);
        Assert.Equal(1, session.ModuleReads);
        int previousReads = session.Reads;
        session.Nodes = session.Nodes.Select(node => node with { Bounds = new(10, 20, 30, 40) }).ToArray();
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(previousReads + 1, session.Reads);
        Assert.Equal("first", model.SelectedNode?.Node.Id);
        Assert.Contains("Verifying compiled XAML source", model.SourceDescription);
        ready.SetResult(new([]));
        await pending;
        Assert.Equal(1, navigations);
        Assert.Equal("Opened source after refresh", model.SourceDescription);
    }

    [Fact]
    public async Task DelayedSourceCallbackGuardSurvivesRefreshWhenTheSelectedSourceIsUnchanged()
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = SourceSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        InspectionSourceRequest? captured = null;
        model.SourceRequested += (request, _) => { captured = request; return ready.Task; };
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0];
        var pending = model.ShowSourceCommand.ExecuteAsync(null);
        Assert.NotNull(captured);
        Assert.True(captured.IsCurrent());
        session.Nodes = session.Nodes.Select(node => node with { Bounds = new(10, 20, 30, 40) }).ToArray();
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(captured.IsCurrent());
        Assert.Contains("Verifying compiled XAML source", model.SourceDescription);
        ready.SetResult("Opened source after refresh");
        await pending;
        Assert.Equal("Opened source after refresh", model.SourceDescription);
    }

    [Fact]
    public async Task FailedOldSourceCallbackCannotReplaceTheNewSelectionsStatus()
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.SourceRequested += (_, _) => ready.Task;
        await model.AttachAsync(SourceSession());
        model.SelectedNode = model.Tree[0];
        var pending = model.ShowSourceCommand.ExecuteAsync(null);
        model.SelectedNode = model.Tree[1];
        string status = model.SourceDescription;
        ready.SetException(new InvalidOperationException("old module read failed"));
        await pending;
        Assert.Equal(status, model.SourceDescription);
        Assert.True(model.ShowSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task PausedOrDisconnectedInspectorDoesNotReadModulesForSourceNavigation()
    {
        var session = SourceSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.SourceRequested += (_, _) => Task.FromResult("Unexpected navigation");
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0];
        model.SetDebuggerState(true);
        Assert.False(model.ShowSourceCommand.CanExecute(null));
        await model.ShowSourceCommand.ExecuteAsync(null);
        model.SetDebuggerState(false);
        await model.DisconnectCommand.ExecuteAsync(null);
        Assert.False(model.ShowSourceCommand.CanExecute(null));
        await model.ShowSourceCommand.ExecuteAsync(null);
        Assert.Equal(0, session.ModuleReads);
    }

    private static async Task ChangeSourceContextAsync(InspectionViewModel model, FakeSession session, string change)
    {
        switch (change)
        {
            case "selection": model.SelectedNode = model.Tree[1]; break;
            case "selection-round-trip":
                var first = model.Tree[0];
                model.SelectedNode = model.Tree[1];
                model.SelectedNode = first;
                break;
            case "source-changed":
                session.Nodes = [SourceNode("first") with { Source = new("/Demo;component/changed.xaml", 3, 7) }, SourceNode("second")];
                await model.RefreshCommand.ExecuteAsync(null);
                break;
            case "disconnect": await model.DisconnectCommand.ExecuteAsync(null); break;
            case "pause": model.SetDebuggerState(true); break;
            case "pause-resume":
                model.SetDebuggerState(true);
                model.SetDebuggerState(false);
                break;
            case "session":
                await model.AttachAsync(SourceSession());
                model.SelectedNode = model.Tree[0]; // The same ID in a new process is still a different source request.
                break;
            default: throw new InvalidOperationException();
        }
    }
}
