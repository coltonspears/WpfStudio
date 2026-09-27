using System.Windows.Input;
using System.Windows.Threading;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class EditorCompletionTests
{
    [Fact]
    public Task StaleCompletionCannotOverwriteNewerExternalEdit() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Completion.cs"), "Con");
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = 3;
        var response = new TaskCompletionSource<(long, TextEdit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long version = state.Version;
        Task commit = editor.CommitCompletionAsync(_ => response.Task, "(");
        state.Content = "Other";
        response.SetResult((version, new TextEdit(0, 3, "Console.WriteLine")));
        await commit;
        Assert.DoesNotContain("Console", state.Content);
        Assert.Contains("Other", state.Content);
        Assert.Contains("(", state.Content);
        Assert.Contains("document changed", model.LanguageStatus);
    });

    [Fact]
    public Task QueuedInputUsesActualRoslynCompletionChange() => OnDispatcherAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-CompletionIntegration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string project = Path.Combine(directory, "Fixture.csproj");
            string file = Path.Combine(directory, "Fixture.cs");
            const string source = "class Fixture { void Run() { System.Console.Wri } }";
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(file, source);
            var build = new BuildService();
            Assert.Equal(0, (await build.RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
            await using var workspace = new WorkspaceClient();
            await workspace.LoadAsync(new LoadWorkspaceRequest(project));
            var state = new DocumentState(file, source);
            using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
            var editor = new EditorSurface { ViewModel = model };
            editor.CaretOffset = source.IndexOf("Wri", StringComparison.Ordinal) + 3;
            CompletionResult completions = await model.CompleteAsync(editor.CaretOffset);
            CompletionEntry chosen = Assert.Single(completions.Items, item => item.DisplayText == "WriteLine");
            Task commit = editor.CommitCompletionAsync(async token =>
            {
                TextEdit? change = await model.CompletionEditAsync(chosen, completions.Version, token);
                Assert.NotNull(change);
                return (completions.Version, change);
            }, "(");
            editor.TextArea.PerformTextInput("\"hello\");");
            await commit;
            Assert.Contains("System.Console.WriteLine(\"hello\");", state.Content);
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public Task PunctuationAndSubsequentTypingReplayAfterActualCompletionEdit() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Completion.cs"), "Con");
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = editor.Document.TextLength;
        var response = new TaskCompletionSource<(long, TextEdit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long version = state.Version;
        Task commit = editor.CommitCompletionAsync(_ => response.Task, "(");
        editor.TextArea.PerformTextInput("42");
        editor.TextArea.PerformTextInput(");");
        Assert.Equal("Con", editor.Text);
        response.SetResult((version, new TextEdit(0, 3, "Console.WriteLine")));
        await commit;
        Assert.Equal("Console.WriteLine(42);", editor.Text);
        Assert.Equal(editor.Text, state.Content);
    });

    [Fact]
    public Task EditingCommandsRetainOrderAmongQueuedCharacters() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Completion.cs"), "Con");
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = 3;
        var response = new TaskCompletionSource<(long, TextEdit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long version = state.Version;
        Task commit = editor.CommitCompletionAsync(_ => response.Task, "(");
        editor.TextArea.PerformTextInput("45");
        System.Windows.Documents.EditingCommands.Backspace.Execute(null, editor.TextArea);
        editor.TextArea.PerformTextInput("2)");
        Assert.Equal("Con", editor.Text);
        response.SetResult((version, new TextEdit(0, 3, "Console.WriteLine")));
        await commit;
        Assert.Equal("Console.WriteLine(42)", editor.Text);
    });

    [Fact]
    public Task ReparentingWhileCompletionWaitsPreservesInputInOriginalBuffer() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var first = new DocumentState(Path.Combine(Path.GetTempPath(), "First.cs"), "Con");
        var second = new DocumentState(Path.Combine(Path.GetTempPath(), "Second.cs"), "second");
        using var firstModel = new EditorViewModel(first, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        using var secondModel = new EditorViewModel(second, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = firstModel };
        editor.CaretOffset = 3;
        var response = new TaskCompletionSource<(long, TextEdit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long version = first.Version;
        Task commit = editor.CommitCompletionAsync(_ => response.Task, "(");
        editor.TextArea.PerformTextInput("typed");
        editor.ViewModel = secondModel;
        response.SetResult((version, new TextEdit(0, 3, "Console.WriteLine")));
        await commit;
        Assert.Equal("Con(typed", first.Content);
        Assert.Equal("second", second.Content);
        Assert.Equal("second", editor.Text);
    });

    [Fact]
    public Task FailedCompletionStillReplaysAllTypedText() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Completion.cs"), "Con");
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = 3;
        var response = new TaskCompletionSource<(long, TextEdit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task commit = editor.CommitCompletionAsync(_ => response.Task, "(");
        editor.TextArea.PerformTextInput("1)");
        response.SetException(new InvalidOperationException("Worker disconnected"));
        await commit;
        Assert.Equal("Con(1)", state.Content);
        Assert.Contains("Worker disconnected", model.LanguageStatus);
    });

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
