using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Tasks;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;

namespace WpfStudio.App.Features.Tasks;

/// <summary>What the Tasks pane needs from the shell beyond the workbench contract.</summary>
public interface ITaskHost : IWorkbenchHost
{
    /// <summary>The profile folder that holds the personal tasks file.</summary>
    string DataDirectory { get; }
    /// <summary>Where a task without a working directory runs when no workspace is open.</summary>
    string FallbackDirectory { get; }
    /// <summary>What the task variables stand for right now.</summary>
    TaskVariableContext VariableContext();
    void ShowTerminal();
    /// <summary>Problems read from task output changed, so Problems needs refreshing.</summary>
    void TaskDiagnosticsChanged();
    /// <summary>Shows the change for review, then writes it; false when the user cancelled it.</summary>
    Task<bool> ReviewAndSaveAsync(string path, string before, string after, string title);
    /// <summary>Opens a tasks file at <paramref name="line"/>, starting a new one from <paramref name="template"/> when it does not exist.</summary>
    Task OpenTasksFileAsync(string path, string template, int line);
}

/// <summary>
/// Tasks in the style of Visual Studio Code: read from .vscode/tasks.json and .wpfstudio/tasks.json in the repository and
/// from tasks.json in the WpfStudio profile, merged by label. Each run gets its own terminal tab; dependencies run
/// together or in order first, and the MSBuild problem matcher turns compiler errors in the output into Problems.
/// </summary>
public sealed partial class TasksViewModel : ObservableObject, IDisposable
{
    private readonly ITaskHost _host;
    private readonly TerminalViewModel _terminal;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessenger _messenger;
    private readonly List<TaskItemViewModel> _all = [];
    private int _reloads;
    private bool _disposed;

    public TasksViewModel(ITaskHost host, TerminalViewModel terminal, IUiDispatcher dispatcher, IMessenger messenger)
    {
        _host = host; _terminal = terminal; _dispatcher = dispatcher; _messenger = messenger;
        messenger.Register<TasksViewModel, WorkspaceChangedMessage>(this, static (tasks, _) => tasks.ReloadInBackground());
        messenger.Register<TasksViewModel, DocumentSavedMessage>(this, static (tasks, message) => { if (tasks.IsTasksFile(message.Document.Path)) tasks.ReloadInBackground(); });
    }

    /// <summary>The tasks matching <see cref="Filter"/>, in file order. Hidden tasks only run as dependencies.</summary>
    public ObservableCollection<TaskItemViewModel> Items { get; } = [];
    public ObservableCollection<TaskFileProblem> FileProblems { get; } = [];
    public TaskEditorViewModel Editor { get; } = new();
    [ObservableProperty] public partial string Filter { get; set; } = "";
    partial void OnFilterChanged(string value) => RefreshVisible();
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial TaskItemViewModel? SelectedItem { get; set; }
    public bool HasProblems => FileProblems.Count > 0;
    /// <summary>Every task, hidden ones included, for the command palette and dependencies.</summary>
    public IReadOnlyList<TaskItemViewModel> All => _all;
    /// <summary>Problems the latest run of each task found in its output.</summary>
    public IEnumerable<WorkspaceDiagnostic> Diagnostics => (Editor.TestRun is { } test ? _all.Append(test) : _all).SelectMany(item => item.Diagnostics);
    public bool HasWorkspace => WorkspaceFolder is not null;

    private string? WorkspaceFolder => _host.WorkspacePath is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
    public string PersonalFile => TaskCatalog.PersonalFile(_host.DataDirectory);
    public string? SharedFile => WorkspaceFolder is { } folder ? Path.Combine(folder, TaskCatalog.SharedFile) : null;
    public string? VisualStudioCodeFile => WorkspaceFolder is { } folder ? Path.Combine(folder, TaskCatalog.VisualStudioCodeFile) : null;
    private bool IsTasksFile(string path) => new[] { PersonalFile, SharedFile, VisualStudioCodeFile }.Any(file => file is not null && file.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ Loading

    private void ReloadInBackground() => _host.GuardAsync(ReloadAsync).Forget();
    [RelayCommand] private Task RefreshAsync() => _host.GuardAsync(ReloadAsync);

    /// <summary>Reads the three files, preferring an open editor's text to the disk, and merges them. Rows of running tasks are kept.</summary>
    public async Task ReloadAsync()
    {
        if (_disposed) return;
        var reload = ++_reloads;
        var buffers = _host.OpenDocumentTexts();
        async Task<TaskFile> ReadAsync(string? path, TaskSource source)
        {
            if (path is null) return TaskFile.Empty("", source);
            return buffers.TryGetValue(path, out var text) ? TaskFileReader.Read(path, text, source) : await TaskFileReader.ReadFileAsync(path, source, _host.Lifetime);
        }
        var files = await Task.WhenAll(ReadAsync(VisualStudioCodeFile, TaskSource.VisualStudioCode), ReadAsync(SharedFile, TaskSource.Shared), ReadAsync(PersonalFile, TaskSource.Personal));
        // A newer reload started while this one read the files.
        if (reload != _reloads || _disposed) return;
        var merged = TaskCatalog.Merge(files);
        var kept = new List<TaskItemViewModel>();
        foreach (var task in merged)
        {
            var item = _all.FirstOrDefault(existing => existing.Label == task.Label);
            if (item is null) item = new TaskItemViewModel(task);
            else item.Definition = task;
            kept.Add(item);
        }
        // A task removed from its file while running stays listed until it ends.
        kept.AddRange(_all.Where(item => item.IsRunning && !kept.Contains(item)));
        _all.Clear(); _all.AddRange(kept);
        FileProblems.Clear();
        foreach (var problem in files.SelectMany(file => file.Problems)) FileProblems.Add(problem);
        OnPropertyChanged(nameof(HasProblems)); OnPropertyChanged(nameof(HasWorkspace));
        RefreshVisible();
        var count = _all.Count(item => !item.Definition.IsHidden);
        Status = count == 0
            ? HasWorkspace ? "No tasks yet. Choose New task, or add .wpfstudio/tasks.json or .vscode/tasks.json to the repository." : "No tasks yet. Choose New task to add a personal one."
            : $"{count} task{(count == 1 ? "" : "s")}";
    }

    private void RefreshVisible()
    {
        var terms = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = SelectedItem;
        Items.Clear();
        foreach (var item in _all.Where(item => !item.Definition.IsHidden))
            if (terms.All(term => item.Label.Contains(term, StringComparison.OrdinalIgnoreCase) || item.Detail.Contains(term, StringComparison.OrdinalIgnoreCase) || item.GroupText.Contains(term, StringComparison.OrdinalIgnoreCase)))
                Items.Add(item);
        SelectedItem = selected is not null && Items.Contains(selected) ? selected : null;
    }

    public TaskItemViewModel? Find(string label) => _all.FirstOrDefault(item => item.Label == label);

    // ------------------------------------------------------------------ Running

    // Runs can last minutes and overlap, so none of these commands waits for another to finish.
    [RelayCommand(AllowConcurrentExecutions = true)] private Task RunAsync(TaskItemViewModel? item) => item is null ? Task.CompletedTask : _host.GuardAsync(async () =>
    {
        if (item.IsRunning)
        {
            if (item.Terminal is { } tab) { _host.ShowTerminal(); _terminal.SelectedSession = tab; }
            _host.ReportStatus($"Task '{item.Label}' is already running. Restart it to run it again.");
            return;
        }
        await RunReportedAsync(item);
    });

    /// <summary>Runs the task with this label, as the command palette does.</summary>
    public Task RunByLabelAsync(string label) => Find(label) is { } item ? RunCommand.ExecuteAsync(item) : _host.GuardAsync(() => throw new TaskConfigurationException($"There is no task labelled '{label}'."));

    [RelayCommand(AllowConcurrentExecutions = true)] private Task RunBuildTaskAsync() => RunGroupAsync(TaskGroup.Build);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task RunTestTaskAsync() => RunGroupAsync(TaskGroup.Test);
    /// <summary>Runs the group's default task. Without a clear default, the Tasks pane is shown to pick one.</summary>
    private Task RunGroupAsync(TaskGroup group) => _host.GuardAsync(async () =>
    {
        if (_all.Count == 0) await ReloadAsync();
        var candidates = _all.Where(item => !item.Definition.IsHidden).Select(item => item.Definition);
        if (TaskCatalog.DefaultFor(candidates, group) is { } task) { await RunCommand.ExecuteAsync(Find(task.Label)); return; }
        var name = group == TaskGroup.Build ? "build" : "test";
        _host.ReportStatus(_all.Any(item => item.Definition.Group == group)
            ? $"Several {name} tasks and none is the default. Mark one with \"isDefault\": true, or pick one from Tasks."
            : $"No {name} task yet. Give a task \"group\": \"{name}\" in tasks.json.");
        _messenger.Send(new ShowToolMessage(Workbenches.Tasks));
    });

    private async Task RunReportedAsync(TaskItemViewModel item)
    {
        _host.ReportStatus($"Running task '{item.Label}'…");
        var succeeded = await RunTaskAsync(item, []);
        _host.ReportStatus(item.State switch
        {
            TaskRunState.Running => $"Task '{item.Label}' is running in the background",
            TaskRunState.Stopped => $"Task '{item.Label}' was stopped",
            _ when succeeded => $"Task '{item.Label}' succeeded",
            _ => $"Task '{item.Label}' failed" + (item.ExitCode is { } code ? $" with exit code {code}" : "")
        });
    }

    /// <summary>
    /// Runs the task once its dependencies have succeeded. A task already running is not started twice: its run is shared,
    /// and a background task counts as done once started. True when the task, and everything it depends on, succeeded.
    /// </summary>
    private Task<bool> RunTaskAsync(TaskItemViewModel item, IReadOnlyList<string> chain)
    {
        if (chain.Contains(item.Label)) throw new TaskConfigurationException("Tasks depend on each other in a loop: " + string.Join(" → ", chain.Append(item.Label)) + ".");
        if (item.CurrentRun is { IsCompleted: false } running) return running;
        if (item.IsRunning) return Task.FromResult(true);
        return item.CurrentRun = RunCoreAsync(item, [.. chain, item.Label]);
    }

    private async Task<bool> RunCoreAsync(TaskItemViewModel item, IReadOnlyList<string> chain)
    {
        try { return await RunStepsAsync(item, chain); }
        catch
        {
            // A run that could not start, such as one in a dependency loop, leaves no program behind and is not running any more.
            if (item.State == TaskRunState.Running && item.Terminal?.Session.Exited is not { IsCompleted: false }) item.State = TaskRunState.Failed;
            throw;
        }
    }

    private async Task<bool> RunStepsAsync(TaskItemViewModel item, IReadOnlyList<string> chain)
    {
        var task = item.Definition;
        item.StopRequested = false; item.ExitCode = null;
        var dependencies = task.DependsOn.Select(label => Find(label) ?? throw new TaskConfigurationException($"Task '{task.Label}' depends on '{label}', which no tasks file defines.")).ToArray();
        if (dependencies.Length > 0)
        {
            item.State = TaskRunState.Running;
            bool ready;
            if (task.DependsInSequence)
            {
                ready = true;
                foreach (var dependency in dependencies)
                    if (!await RunTaskAsync(dependency, chain) || item.StopRequested) { ready = false; break; }
            }
            else ready = (await Task.WhenAll(dependencies.Select(dependency => RunTaskAsync(dependency, chain)))).All(result => result);
            if (!ready || item.StopRequested) { item.State = item.StopRequested ? TaskRunState.Stopped : TaskRunState.Failed; return false; }
        }
        if (!task.HasCommand) { item.State = TaskRunState.Succeeded; return true; }

        item.State = TaskRunState.Running;
        var launch = TaskCommandLine.Create(task, _host.VariableContext(), TaskCommandLine.DefaultShell(TaskCommandLine.FindOnPath), TaskCommandLine.FindOnPath, _host.FallbackDirectory);
        item.ClearDiagnostics();
        _host.TaskDiagnosticsChanged();
        Action<byte[]>? observe = null;
        if (task.ProblemMatcher == TaskProblemMatcher.MSBuild)
        {
            // Called on the terminal's reader thread; the rows it finds are added on the UI thread.
            var directory = launch.WorkingDirectory;
            var lines = new TaskOutputLines(line =>
            {
                if (BuildService.ParseDiagnostic(line, directory) is { } diagnostic)
                    _dispatcher.Post(() => { if (!_disposed && item.AddDiagnostic(diagnostic)) _host.TaskDiagnosticsChanged(); });
            });
            observe = bytes => lines.Write(bytes);
        }
        _host.ShowTerminal();
        var previous = item.Terminal is { } old && old.Session.Exited.IsCompleted ? old : null;
        var tab = await _terminal.StartTaskAsync(new(task.Label, "Task: " + task.Label, launch.Executable, launch.Arguments, launch.WorkingDirectory, launch.Environment, observe, previous));
        if (tab is null) { item.State = TaskRunState.Stopped; return false; }
        item.Terminal = tab;
        if (task.IsBackground) { WatchAsync(item, tab).Forget(); return true; }
        return Finish(item, tab, await tab.Session.Exited);
    }

    private async Task WatchAsync(TaskItemViewModel item, TerminalSessionViewModel tab)
    {
        var code = await tab.Session.Exited;
        if (ReferenceEquals(item.Terminal, tab)) Finish(item, tab, code);
    }

    private bool Finish(TaskItemViewModel item, TerminalSessionViewModel tab, int code)
    {
        // Closing the tab ends the program too, which is a stop rather than a failure.
        var stopped = item.StopRequested || !_terminal.Sessions.Contains(tab);
        item.ExitCode = stopped ? null : code;
        item.State = stopped ? TaskRunState.Stopped : code == 0 ? TaskRunState.Succeeded : TaskRunState.Failed;
        tab.Title = item.State switch
        {
            TaskRunState.Succeeded => "✓ " + item.Label,
            TaskRunState.Stopped => "■ " + item.Label,
            _ => $"✗ {item.Label} (exit {code})"
        };
        return item.State == TaskRunState.Succeeded;
    }

    [RelayCommand(AllowConcurrentExecutions = true)] private Task StopAsync(TaskItemViewModel? item) => item is null ? Task.CompletedTask : _host.GuardAsync(() => { Stop(item, []); return Task.CompletedTask; });
    /// <summary>Ends the task's program, or the dependencies a task without one is waiting for.</summary>
    private void Stop(TaskItemViewModel item, HashSet<string> seen)
    {
        if (!seen.Add(item.Label) || !item.IsRunning) return;
        item.StopRequested = true;
        if (item.Definition.HasCommand && item.Terminal is { } tab && !tab.Session.Exited.IsCompleted) tab.Session.Terminate();
        foreach (var dependency in item.Definition.DependsOn.Select(Find).OfType<TaskItemViewModel>()) Stop(dependency, seen);
        // A task still waiting for its dependencies has no program; it ends when they do.
    }
    [RelayCommand] private Task StopAllAsync() => _host.GuardAsync(() => { foreach (var item in _all.Where(item => item.IsRunning).ToArray()) Stop(item, []); return Task.CompletedTask; });
    public bool AnyRunning => _all.Any(item => item.IsRunning);

    [RelayCommand(AllowConcurrentExecutions = true)] private Task RestartAsync(TaskItemViewModel? item) => item is null ? Task.CompletedTask : _host.GuardAsync(async () =>
    {
        if (item.IsRunning)
        {
            Stop(item, []);
            if (item.Terminal is { } tab) await tab.Session.Exited.WaitAsync(TimeSpan.FromSeconds(10));
            if (item.CurrentRun is { } run) await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await RunReportedAsync(item);
    });

    // ------------------------------------------------------------------ Editing

    [RelayCommand] private Task EditSharedAsync() => _host.GuardAsync(async () =>
    {
        if (SharedFile is not { } path) { _host.ReportStatus("Open a solution to keep tasks in its repository"); return; }
        await _host.OpenTasksFileAsync(path, TaskFileWriter.Template(TaskSource.Shared), 1);
    });
    [RelayCommand] private Task EditPersonalAsync() => _host.GuardAsync(() => _host.OpenTasksFileAsync(PersonalFile, TaskFileWriter.Template(TaskSource.Personal), 1));
    [RelayCommand] private Task EditTaskAsync(TaskItemViewModel? item) => item is not { CanEdit: true } ? Task.CompletedTask
        : _host.GuardAsync(() => _host.OpenTasksFileAsync(item.Definition.SourcePath, "", Math.Max(1, item.Definition.Line)));
    [RelayCommand] private Task NavigateProblemAsync(TaskFileProblem? problem) => problem is null || problem.Path.Length == 0 ? Task.CompletedTask
        : _host.GuardAsync(() => _host.OpenTasksFileAsync(problem.Path, "", Math.Max(1, problem.Line)));

    [RelayCommand] private void NewTask()
    {
        Editor.Reset(personal: !HasWorkspace);
        Editor.IsOpen = true;
        _messenger.Send(new ShowToolMessage(Workbenches.Tasks));
    }
    [RelayCommand] private void CancelNewTask() => Editor.IsOpen = false;

    /// <summary>Runs the form's task as it stands, before saving it.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)] private Task TestNewTaskAsync() => _host.GuardAsync(async () =>
    {
        Editor.Error = "";
        TaskDraft draft;
        try { draft = Editor.ToDraft(); }
        catch (TaskConfigurationException ex) { Editor.Error = ex.Message; return; }
        if (Editor.TestRun is { IsRunning: true }) { Editor.Error = "The test run is still going. Stop it in the terminal first."; return; }
        var previous = Editor.TestRun;
        var item = Editor.TestRun = new TaskItemViewModel(TaskEditorViewModel.ToTask(draft, draft.Label + " (test run)")) { Terminal = previous?.Terminal };
        Editor.TestStatus = "Running…";
        try
        {
            var succeeded = await RunTaskAsync(item, []);
            Editor.TestStatus = succeeded ? "The test run succeeded." : item.State == TaskRunState.Stopped ? "The test run was stopped." : "The test run failed" + (item.ExitCode is { } code ? $" with exit code {code}." : ".");
        }
        catch (TaskConfigurationException ex) { Editor.TestStatus = ""; Editor.Error = ex.Message; }
    });

    [RelayCommand] private Task SaveNewTaskAsync() => _host.GuardAsync(async () =>
    {
        Editor.Error = "";
        TaskDraft draft;
        try { draft = Editor.ToDraft(); }
        catch (TaskConfigurationException ex) { Editor.Error = ex.Message; return; }
        var personal = Editor.SaveToPersonal || SharedFile is null;
        var path = personal ? PersonalFile : SharedFile!;
        var source = personal ? TaskSource.Personal : TaskSource.Shared;
        var before = _host.OpenDocumentTexts().TryGetValue(path, out var open) ? open : File.Exists(path) ? await File.ReadAllTextAsync(path, _host.Lifetime) : "";
        if (TaskFileReader.Read(path, before, source).Tasks.Any(task => task.Label == draft.Label)) { Editor.Error = $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)} already has a task labelled '{draft.Label}'."; return; }
        string after;
        try { after = TaskFileWriter.AddTask(before, draft, source); }
        catch (InvalidOperationException ex) { Editor.Error = ex.Message + " Fix the file, or add the task to it by hand."; return; }
        if (!await _host.ReviewAndSaveAsync(path, before, after, $"Add task '{draft.Label}'")) return;
        Editor.IsOpen = false;
        await ReloadAsync();
        SelectedItem = Find(draft.Label) is { } added && Items.Contains(added) ? added : SelectedItem;
        _host.ReportStatus($"Added task '{draft.Label}' to {path}");
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messenger.UnregisterAll(this);
    }
}
