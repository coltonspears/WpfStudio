using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.App.Features.Tasks;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Tasks;
using WpfStudio.Runtime.Terminal;

namespace WpfStudio.Shell.Tests;

/// <summary>The Tasks pane against a fake host and the real terminal, running small cmd.exe tasks.</summary>
public sealed class TasksViewModelTests
{
    private const string SharedTasks = """
        {
          // cmd.exe starts faster than PowerShell, which is the default shell.
          "options": { "shell": { "executable": "cmd.exe" } },
          "tasks": [
            { "label": "compile", "command": "echo C:\\src\\App\\Program.cs(3,5): error CS1002: ; expected [C:\\src\\App\\App.csproj]& exit /b 1", "problemMatcher": "$msCompile", "group": "build" },
            { "label": "ok", "command": "echo fine", "group": { "kind": "build", "isDefault": true } },
            { "label": "never", "command": "echo should not run" },
            { "label": "in-order", "dependsOn": ["ok", "compile", "never"], "dependsOrder": "sequence" },
            { "label": "together", "dependsOn": ["ok", "hidden"] },
            { "label": "hidden", "command": "exit /b 0", "hide": true },
            { "label": "loop-a", "dependsOn": "loop-b" },
            { "label": "loop-b", "dependsOn": "loop-a" },
            { "label": "orphan", "dependsOn": "nothing" },
            { "label": "server", "command": "ping -n 60 127.0.0.1", "isBackground": true },
            { "label": "after-server", "dependsOn": "server", "command": "echo started" },
            { "label": "env", "command": "if \"%TASK_MODE%\"==\"Release\" (exit /b 0) else (exit /b 9)", "options": { "env": { "TASK_MODE": "${configuration}" } } }
          ]
        }
        """;

    [Fact]
    public Task TheFilesMergeByLabelAndOpenBuffersWinOverTheDisk() => WithTasksAsync(async (tasks, host, _) =>
    {
        await File.WriteAllTextAsync(Path.Combine(host.Root, ".vscode", "tasks.json"), """{ "tasks": [ { "label": "ok", "command": "echo vscode" }, { "label": "vscode-only", "command": "echo v" }, { "label": "bad", "type": "npm" } ] }""");
        host.Buffers[host.PersonalFile] = """{ "tasks": [ { "label": "mine", "command": "echo mine" }, { "label": "never", "command": "echo personal" } ] }""";
        await tasks.ReloadAsync();

        Assert.Equal(["ok", "vscode-only", "compile", "never", "in-order", "together", "loop-a", "loop-b", "orphan", "server", "after-server", "env", "mine"], tasks.Items.Select(item => item.Label));
        Assert.Contains(tasks.All, item => item.Label == "hidden");
        Assert.Equal(("Shared", "fine"), (tasks.Find("ok")!.SourceText, tasks.Find("ok")!.Detail.Split(' ')[^1]));
        Assert.Equal(TaskSource.Personal, tasks.Find("never")!.Definition.Source);
        Assert.Contains("type \"npm\"", Assert.Single(tasks.FileProblems).Message);
        Assert.Equal("13 tasks", tasks.Status);
        tasks.Filter = "server";
        Assert.Equal(["server", "after-server"], tasks.Items.Select(item => item.Label));
    });

    [Fact]
    public Task InOrderDependenciesStopAtTheFirstFailureAndItsErrorsReachProblems() => WithTasksAsync(async (tasks, host, _) =>
    {
        await tasks.RunCommand.ExecuteAsync(tasks.Find("in-order"));

        Assert.Equal(TaskRunState.Succeeded, tasks.Find("ok")!.State);
        Assert.Equal((TaskRunState.Failed, 1), (tasks.Find("compile")!.State, tasks.Find("compile")!.ExitCode));
        Assert.Equal(TaskRunState.Idle, tasks.Find("never")!.State);
        Assert.Equal(TaskRunState.Failed, tasks.Find("in-order")!.State);
        Assert.Equal("Task 'in-order' failed", host.Status);
        await Until(() => tasks.Diagnostics.Any());
        var problem = Assert.Single(tasks.Diagnostics);
        Assert.Equal(("CS1002", "Error", @"C:\src\App\Program.cs", 3, 5, "; expected"), (problem.Id, problem.Severity, problem.Path, problem.Line, problem.Column, problem.Message));
        Assert.True(host.DiagnosticsChanges > 0);
        Assert.Contains("✗ compile (exit 1)", host.Terminal.Sessions.Select(session => session.Title));
        Assert.True(host.TerminalShown);
    });

    [Fact]
    public Task TogetherDependenciesRunHiddenTasksAndARerunReusesTheFinishedTab() => WithTasksAsync(async (tasks, host, _) =>
    {
        await tasks.RunCommand.ExecuteAsync(tasks.Find("together"));
        Assert.Equal([TaskRunState.Succeeded, TaskRunState.Succeeded, TaskRunState.Succeeded], new[] { "ok", "hidden", "together" }.Select(label => tasks.Find(label)!.State));
        Assert.Equal("Task 'together' succeeded", host.Status);
        var tabs = host.Terminal.Sessions.Count;

        await tasks.RunCommand.ExecuteAsync(tasks.Find("ok"));
        Assert.Equal(tabs, host.Terminal.Sessions.Count);
        Assert.Contains("✓ ok", host.Terminal.Sessions.Select(session => session.Title));
    });

    [Fact]
    public Task LoopsAndMissingDependenciesAreReportedWithoutRunningAnything() => WithTasksAsync(async (tasks, host, _) =>
    {
        await tasks.RunCommand.ExecuteAsync(tasks.Find("loop-a"));
        Assert.Equal("Tasks depend on each other in a loop: loop-a → loop-b → loop-a.", host.Status);
        Assert.Equal([TaskRunState.Failed, TaskRunState.Failed], new[] { "loop-a", "loop-b" }.Select(label => tasks.Find(label)!.State));
        Assert.False(tasks.AnyRunning);
        await tasks.RunCommand.ExecuteAsync(tasks.Find("orphan"));
        Assert.Equal("Task 'orphan' depends on 'nothing', which no tasks file defines.", host.Status);
        Assert.Empty(host.Terminal.Sessions);
    });

    [Fact]
    public Task ABackgroundDependencyCountsOnceStartedAndStopsWhenAsked() => WithTasksAsync(async (tasks, host, _) =>
    {
        await tasks.RunCommand.ExecuteAsync(tasks.Find("after-server"));
        var server = tasks.Find("server")!;
        Assert.Equal(TaskRunState.Succeeded, tasks.Find("after-server")!.State);
        Assert.Equal(TaskRunState.Running, server.State);
        Assert.True(tasks.AnyRunning);

        await tasks.RunCommand.ExecuteAsync(server);
        Assert.Contains("already running", host.Status);

        await tasks.StopCommand.ExecuteAsync(server);
        await Until(() => server.State == TaskRunState.Stopped);
        Assert.Null(server.ExitCode);
        Assert.False(tasks.AnyRunning);
    });

    [Fact]
    public Task VariablesComeFromTheHostAndReachTheEnvironment() => WithTasksAsync(async (tasks, host, _) =>
    {
        await tasks.RunCommand.ExecuteAsync(tasks.Find("env"));
        Assert.Equal(TaskRunState.Succeeded, tasks.Find("env")!.State);
    });

    [Fact]
    public Task RunBuildTaskStartsTheGroupDefaultOrSaysWhyNot() => WithTasksAsync(async (tasks, host, messenger) =>
    {
        var shown = new List<string>();
        messenger.Register<List<string>, ShowToolMessage>(shown, static (list, message) => list.Add(message.ToolId));
        await tasks.RunBuildTaskCommand.ExecuteAsync(null);
        Assert.Equal(TaskRunState.Succeeded, tasks.Find("ok")!.State);

        await tasks.RunTestTaskCommand.ExecuteAsync(null);
        Assert.StartsWith("No test task yet", host.Status);
        Assert.Contains(App.Features.Workbench.Workbenches.Tasks, shown);
    });

    [Fact]
    public Task ANewTaskIsTestedThenAddedToTheChosenFile() => WithTasksAsync(async (tasks, host, _) =>
    {
        tasks.NewTaskCommand.Execute(null);
        Assert.True(tasks.Editor.IsOpen);
        Assert.False(tasks.Editor.SaveToPersonal);
        await tasks.SaveNewTaskCommand.ExecuteAsync(null);
        Assert.Equal("Give the task a label.", tasks.Editor.Error);

        tasks.Editor.Label = "ok";
        tasks.Editor.Command = "cmd.exe";
        tasks.Editor.IsProcess = true;
        tasks.Editor.Arguments = "/d /c \"exit /b 0\"";
        await tasks.SaveNewTaskCommand.ExecuteAsync(null);
        Assert.Contains("already has a task labelled 'ok'", tasks.Editor.Error);

        tasks.Editor.Label = "quick";
        await tasks.TestNewTaskCommand.ExecuteAsync(null);
        Assert.Equal("The test run succeeded.", tasks.Editor.TestStatus);
        Assert.Null(tasks.Find("quick"));

        tasks.Editor.SaveToPersonal = true;
        await tasks.SaveNewTaskCommand.ExecuteAsync(null);
        Assert.False(tasks.Editor.IsOpen);
        Assert.Equal(host.PersonalFile, Assert.Single(host.Reviewed));
        var added = tasks.Find("quick")!;
        Assert.Equal((TaskSource.Personal, TaskKind.Process), (added.Definition.Source, added.Definition.Kind));
        Assert.Equal(["/d", "/c", "exit /b 0"], added.Definition.Args);
        Assert.Contains("// Your own tasks", await File.ReadAllTextAsync(host.PersonalFile));
    });

    // ------------------------------------------------------------------ Harness

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The tasks did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    private static Task WithTasksAsync(Func<TasksViewModel, FakeTaskHost, IMessenger, Task> test)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "WpfStudio-Tasks-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, ".wpfstudio")); Directory.CreateDirectory(Path.Combine(root, ".vscode")); Directory.CreateDirectory(Path.Combine(root, "data"));
                var ui = new DispatcherPost(dispatcher);
                var terminal = new TerminalViewModel(ui) { WorkingDirectory = root };
                var host = new FakeTaskHost(root, terminal);
                var messenger = new WeakReferenceMessenger();
                var tasks = new TasksViewModel(host, terminal, ui, messenger);
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(root, ".wpfstudio", "tasks.json"), SharedTasks);
                    await tasks.ReloadAsync();
                    await test(tasks, host, messenger);
                    result.TrySetResult();
                }
                catch (Exception exception) { result.TrySetException(exception); }
                finally
                {
                    tasks.Dispose();
                    await terminal.DisposeAsync();
                    try { Directory.Delete(root, true); } catch (IOException) { }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Tasks test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private sealed class DispatcherPost(Dispatcher dispatcher) : IUiDispatcher
    {
        public void Post(Action action) => dispatcher.BeginInvoke(action);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }

    private sealed class FakeTaskHost(string root, TerminalViewModel terminal) : ITaskHost
    {
        public string Root { get; } = root;
        public TerminalViewModel Terminal { get; } = terminal;
        public Dictionary<string, string> Buffers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string PersonalFile => TaskCatalog.PersonalFile(DataDirectory);
        public string Status { get; private set; } = "";
        public int DiagnosticsChanges { get; private set; }
        public bool TerminalShown { get; private set; }
        public List<string> Reviewed { get; } = [];
        public FakeDialogs Dialogs { get; } = new();

        public string DataDirectory => Path.Combine(Root, "data");
        public string FallbackDirectory => Root;
        public TaskVariableContext VariableContext() => new() { WorkspaceFolder = Root, Configuration = "Release" };
        public void ShowTerminal() => TerminalShown = true;
        public void TaskDiagnosticsChanged() => DiagnosticsChanges++;
        public async Task<bool> ReviewAndSaveAsync(string path, string before, string after, string title)
        {
            Reviewed.Add(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, after);
            return true;
        }
        public Task OpenTasksFileAsync(string path, string template, int line) => Task.CompletedTask;

        public string? WorkspacePath => Path.Combine(Root, "App.sln");
        public IReadOnlyList<string> WorkspaceFiles => [];
        public IReadOnlyList<string> OpenDocumentPaths => [.. Buffers.Keys];
        public event Action? DocumentsChanged { add { } remove { } }
        public IReadOnlyDictionary<string, string> OpenDocumentTexts() => Buffers;
        public (string Path, int Line)? ActiveLocation => null;
        public string? SelectedExplorerFile => null;
        IUserDialogService App.Features.Workbench.IWorkbenchHost.Dialogs => Dialogs;
        public CancellationToken Lifetime => CancellationToken.None;
        public async Task GuardAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { Status = ex.Message; } }
        public void ReportStatus(string status) => Status = status;
        public Task NavigateAsync(string path, int line, int column) => Task.CompletedTask;
        public Task OpenDocumentAsync(string path) => Task.CompletedTask;
        public Task<bool> CloseDocumentAsync(string path) => Task.FromResult(true);
    }
}
