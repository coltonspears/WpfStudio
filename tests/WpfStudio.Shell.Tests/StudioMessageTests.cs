using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

/// <summary>Each message reaches its real receiver and stops when that receiver is disposed.</summary>
public sealed class StudioMessageTests
{
    private static WorkspaceProject Project(ShellTestContext test) =>
        new("id", "App", Path.Combine(test.Root, "App.csproj"), "net10.0", null, true, []);

    [Fact]
    public async Task EachContainerHasItsOwnMessenger()
    {
        await using var first = new ShellTestContext();
        await using var second = new ShellTestContext();
        Assert.NotSame(first.Shell.Messenger, second.Shell.Messenger);
        Assert.NotSame(WeakReferenceMessenger.Default, first.Shell.Messenger);
    }

    [Fact]
    public async Task WorkspaceChangesReachThePackagesTool()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        var project = Project(test);

        shell.Workspace = new(project.ProjectPath, "Test", [project], []);
        Assert.Equal(project.ProjectPath, Assert.Single(shell.Features.Packages.Projects).ProjectPath);
        shell.Workspace = null;
        Assert.Empty(shell.Features.Packages.Projects);

        shell.Features.Dispose();
        shell.Messenger.Send(new WorkspaceChangedMessage(new(project.ProjectPath, "Test", [project], [])));
        Assert.Empty(shell.Features.Packages.Projects);
    }

    [Fact]
    public async Task AFeatureCanAskForAToolUntilTheShellIsDisposed()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        var shown = new List<string>();
        shell.ToolRequested += shown.Add;

        shell.Breakpoints.ShowCommand.Execute(null);
        Assert.Equal([Workbenches.Debugger], shown);

        await shell.DisposeAsync();
        shown.Clear();
        shell.Messenger.Send(new ShowToolMessage(Workbenches.Output));
        Assert.Empty(shown);
    }

    [Fact]
    public async Task AnExplicitSaveUpdatesTheTaskListUntilItIsDisposed()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        shell.Workspace = new(Path.Combine(test.Root, "App.csproj"), "Test", [], []);
        var path = await test.CreateFileAsync("Saved.cs", "class Saved { }");
        await shell.OpenDocumentAsync(path);
        var document = shell.ActiveDocument!;
        Assert.Empty(shell.TaskList.Comments);

        document.State.Content = "// TODO: from the explicit save\nclass Saved { }";
        await shell.SaveCommand.ExecuteAsync(null);
        Assert.Contains("from the explicit save", Assert.Single(shell.TaskList.Comments).Text);

        shell.TaskList.Dispose();
        document.State.Content = "// TODO: after disposal\nclass Saved { }";
        await shell.SaveCommand.ExecuteAsync(null);
        Assert.Contains("from the explicit save", Assert.Single(shell.TaskList.Comments).Text);
    }

    [Fact]
    public async Task ATimedSaveTellsTheSameReceivers()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        shell.Workspace = new(Path.Combine(test.Root, "App.csproj"), "Test", [], []);
        var xaml = await test.CreateFileAsync("View.xaml", "<Grid />");
        var code = await test.CreateFileAsync("Code.cs", "class Code { }");
        await shell.OpenDocumentAsync(xaml);
        await shell.OpenDocumentAsync(code);
        var saved = new List<string>();
        shell.Messenger.Register<DocumentSavedMessage>(this, (_, message) => saved.Add(message.Document.Path));

        shell.Documents.First(d => d.State.Path == code).State.Content = "// TODO: from the timer\nclass Code { }";
        shell.Documents.First(d => d.State.Path == xaml).State.Content = "<Grid Margin=\"1\" />";
        await shell.AutoSave.RunNowAsync();

        Assert.Equal(new[] { code, xaml }.Order(), saved.Order());
        Assert.Contains("from the timer", Assert.Single(shell.TaskList.Comments).Text);
    }

    [Fact]
    public async Task ASavedXamlDocumentInvalidatesResourcesUntilTheShellIsDisposed()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        var resources = (XamlResourceContext)typeof(ShellViewModel).GetField("_xamlResources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
        var invalidated = 0;
        resources.Changed += (_, _) => invalidated++;
        var xaml = test.Store.Create(Path.Combine(test.Root, "View.xaml"));
        var code = test.Store.Create(Path.Combine(test.Root, "Code.cs"));

        shell.Messenger.Send(new DocumentSavedMessage(code));
        Assert.Equal(0, invalidated);
        shell.Messenger.Send(new DocumentSavedMessage(xaml));
        Assert.Equal(1, invalidated);

        await shell.DisposeAsync();
        shell.Messenger.Send(new DocumentSavedMessage(xaml));
        Assert.Equal(1, invalidated);
    }

    [Fact]
    public async Task ResumingClearsTheExecutionLineUntilTheShellIsDisposed()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        var path = await test.CreateFileAsync("Paused.cs", "class Paused { }\n\n\n");
        await shell.OpenDocumentAsync(path);
        var document = shell.ActiveDocument!;

        shell.Debugger.IsStopped = true;
        document.ExecutionLine = 2;
        shell.Debugger.IsStopped = false;
        Assert.Equal(-1, document.ExecutionLine);

        await shell.DisposeAsync();
        shell.Debugger.IsStopped = true;
        document.ExecutionLine = 2;
        shell.Debugger.IsStopped = false;
        Assert.Equal(2, document.ExecutionLine);
    }

    [Fact]
    public async Task APausedDebuggerKeepsF5AvailableWhileAnOperationRuns()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        var path = await test.CreateFileAsync("Edited.cs", "original");
        await shell.OpenDocumentAsync(path);
        shell.ActiveDocument!.State.Content = "edited";
        await File.WriteAllTextAsync(path, "changed on disk");
        shell.Workspace = new(Path.Combine(test.Root, "App.csproj"), "Test", [], []);
        TaskCompletionSource release = new(), reached = new();
        test.Dialogs.WhileConfirming = _ => { reached.TrySetResult(); return release.Task; };

        var build = shell.BuildCommand.ExecuteAsync(null);
        await reached.Task.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.False(shell.DebugCommand.CanExecute(null));
        var notified = 0;
        shell.DebugCommand.CanExecuteChanged += (_, _) => notified++;

        shell.Debugger.IsStopped = true;
        Assert.Equal(1, notified);
        Assert.True(shell.DebugCommand.CanExecute(null));

        shell.Debugger.IsStopped = false;
        release.SetResult();
        await build.WaitAsync(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ADebuggerSourceLineOpensAndMarksTheDocumentUntilTheShellIsDisposed()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        var path = await test.CreateFileAsync("Frame.cs", "class Frame { }\n\n\n");
        await test.CreateFileAsync("Other.cs", "class Other { }");
        shell.Debugger.Breakpoints.Add(new() { Path = path, Line = 2 });
        shell.Debugger.SelectedBreakpoint = shell.Debugger.Breakpoints[0];

        shell.Debugger.NavigateBreakpointCommand.Execute(null);
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        while (shell.ActiveDocument?.ExecutionLine != 2) await Task.Delay(10, patience.Token);
        Assert.Equal(path, shell.ActiveDocument.State.Path);

        await shell.CloseDocumentAsync(shell.ActiveDocument, discardChanges: true);
        await shell.DisposeAsync();
        shell.Debugger.NavigateBreakpointCommand.Execute(null);
        Assert.Empty(shell.Documents);
    }

    [Fact]
    public async Task TheDesignerSelectionReachesTheEditorThroughItsHost()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        var path = await test.CreateFileAsync("View.xaml", "<Grid><TextBlock Text=\"a>b\" />\n</Grid>");
        await shell.OpenDocumentAsync(path);

        ((WpfStudio.App.Features.Designer.IDesignerSourceHost)shell).RevealSelection(new(path, 6, 30, 1, 7));

        var editor = shell.ActiveDocument!;
        Assert.Equal(6, editor.SelectionStart);
        Assert.Equal("<TextBlock Text=\"a>b\" />\n</Grid>".IndexOf(" />", StringComparison.Ordinal) + 3, editor.SelectionLength);
    }

    [Fact]
    public async Task OpeningAndClosingAReviewOrThePaletteCoversTheDesignerThroughTheirHosts()
    {
        await using var test = new ShellTestContext();
        var shell = test.Shell;
        var path = await test.CreateFileAsync("Doc.cs", "class Doc { }");
        var notified = new List<string?>();
        shell.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        shell.Palette.OpenCommands();
        Assert.True(shell.Designer.IsOverlayOpen);
        shell.Palette.CloseCommand.Execute(null);
        Assert.False(shell.Designer.IsOverlayOpen);

        var review = shell.ChangeReview.ShowAsync("Review", [new(path, "a", "b", "one change")], "");
        Assert.True(shell.Designer.IsOverlayOpen);
        Assert.Contains(nameof(ShellViewModel.IsPreviewOpen), notified);
        notified.Clear();
        shell.ChangeReview.CancelCommand.Execute(null);
        await review;
        Assert.False(shell.Designer.IsOverlayOpen);
        Assert.Contains(nameof(ShellViewModel.IsPreviewOpen), notified);
    }

    [Fact]
    public async Task ALanguageWorkerExitReachesTheStatusBarThroughTheDispatcherUntilTheShellIsDisposed()
    {
        var dispatcher = new HoldingDispatcher();
        await using var test = new ShellTestContext(configure: services => services.AddSingleton<IUiDispatcher>(dispatcher));
        var shell = test.Shell;
        var exited = typeof(WorkspaceClient).GetField(nameof(WorkspaceClient.WorkerExited), BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Raise() => ((EventHandler<string>?)exited.GetValue(test.Workspace))?.Invoke(test.Workspace, "worker stderr");

        // WorkspaceClient raises this on the worker's exit thread, so nothing may touch the shell until the dispatcher runs it.
        dispatcher.Hold = true;
        await Task.Run(Raise);
        Assert.DoesNotContain("Language worker stopped", shell.Status);
        dispatcher.Release();
        Assert.Contains("Language worker stopped", shell.Status);
        Assert.Contains("worker stderr", shell.Output);

        await shell.DisposeAsync();
        var output = shell.Output;
        Raise();
        Assert.Equal(output, shell.Output);
    }

    private sealed class HoldingDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _held = new();
        public bool Hold { get; set; }
        public void Post(Action action) { if (Hold) lock (_held) _held.Enqueue(action); else action(); }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
        public void Release()
        {
            Hold = false;
            lock (_held) while (_held.TryDequeue(out var action)) action();
        }
    }
}
