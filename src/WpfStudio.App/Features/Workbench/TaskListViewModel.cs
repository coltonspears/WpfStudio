using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core;
using WpfStudio.Core.Text;

namespace WpfStudio.App.Features.Workbench;

/// <summary>A hand-written Task List entry; checking it off saves the list.</summary>
public sealed partial class UserTaskViewModel(string text, bool isDone, string? path, int line) : ObservableObject
{
    public string Text { get; } = text;
    public string? Path { get; } = path;
    public int Line { get; } = line;
    public bool HasLocation => Path != null;
    public string Location => Path == null ? "" : $"{System.IO.Path.GetFileName(Path)}:{Line}";
    [ObservableProperty] public partial bool IsDone { get; set; } = isDone;
    public UserTaskItem ToItem() => new(Text, IsDone, Path, Line);
}

/// <summary>Task List: TODO-style comments found in the workspace, and the user's own to-do items for it.</summary>
public sealed partial class TaskListViewModel : ObservableObject, IDisposable
{
    private const string NoWorkspaceStatus = "Open a solution to list its TODO comments.";
    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;
    private readonly IWorkbenchHost _host;
    private readonly WorkspaceStateStore<UserTaskItem> _store;
    private CancellationTokenSource? _scan;
    private List<TaskComment> _comments = [];
    private bool _disposed;

    public TaskListViewModel(IWorkbenchHost host, WorkspaceStateStore<UserTaskItem>? store = null)
    {
        _host = host;
        _store = store ?? new("tasks");
    }

    /// <summary>The comments matching <see cref="Filter"/>, high priority first.</summary>
    public ObservableCollection<TaskComment> Comments { get; } = [];
    public ObservableCollection<UserTaskViewModel> UserTasks { get; } = [];
    [ObservableProperty] public partial TaskComment? SelectedComment { get; set; }
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial string NewTaskText { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = NoWorkspaceStatus;
    partial void OnFilterChanged(string value) => RefreshVisibleComments();

    /// <summary>Scans every source file in the workspace, reading open documents from their editor buffers.</summary>
    [RelayCommand] private Task RefreshAsync() => _host.GuardAsync(async () =>
    {
        // Only a newer scan or the end of the session cancels one, and neither is worth reporting.
        try { await ScanAsync(); } catch (OperationCanceledException) { }
    });
    private async Task ScanAsync()
    {
        _scan?.Cancel(); _scan?.Dispose();
        var scan = _scan = CancellationTokenSource.CreateLinkedTokenSource(_host.Lifetime);
        // The next scan disposes the source while this one may still be reading files, so the worker keeps the token.
        var token = scan.Token;
        if (_host.WorkspacePath is null) { _comments = []; RefreshVisibleComments(); Status = NoWorkspaceStatus; return; }
        Status = "Scanning for TODO comments…";
        var buffers = _host.OpenDocumentTexts();
        var files = _host.WorkspaceFiles.Where(TaskComments.Supports).ToArray();
        var found = await Task.Run(() =>
        {
            var results = new List<TaskComment>();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!buffers.TryGetValue(file, out var text))
                    {
                        if (new FileInfo(file).Length > TaskComments.MaximumFileLength) continue;
                        text = File.ReadAllText(file);
                    }
                    results.AddRange(TaskComments.Find(file, text));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Unreadable files are simply not listed. */ }
            }
            return results;
        }, token);
        if (token.IsCancellationRequested) return;
        _comments = found;
        RefreshVisibleComments();
        Status = found.Count == 0 ? $"No TODO comments in {files.Length} files" : $"{found.Count} comment{(found.Count == 1 ? "" : "s")} in {found.Select(c => c.Path).Distinct().Count()} files";
    }

    /// <summary>A saved document's comments replace the ones found in it before, without rescanning the workspace.</summary>
    public void UpdateComments(string path, string text)
    {
        if (_host.WorkspacePath is null || !TaskComments.Supports(path)) return;
        _comments = _comments.Where(c => !c.Path.Equals(path, IgnoreCase)).Concat(TaskComments.Find(path, text)).ToList();
        RefreshVisibleComments();
    }

    private void RefreshVisibleComments()
    {
        var terms = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Comments.Clear();
        foreach (var comment in _comments.OrderByDescending(c => c.IsHighPriority).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Line))
            if (terms.All(term => comment.Token.Contains(term, IgnoreCase) || comment.Text.Contains(term, IgnoreCase) || comment.FileName.Contains(term, IgnoreCase) || comment.Owner.Contains(term, IgnoreCase)))
                Comments.Add(comment);
    }

    [RelayCommand] private Task NavigateCommentAsync(TaskComment? comment) => comment == null ? Task.CompletedTask : _host.GuardAsync(() => _host.NavigateAsync(comment.Path, comment.Line, comment.Column));
    [RelayCommand] private Task NavigateUserTaskAsync(UserTaskViewModel? task) => task?.Path == null ? Task.CompletedTask : _host.GuardAsync(() => _host.NavigateAsync(task.Path, Math.Max(1, task.Line), 1));

    // ------------------------------------------------------------------ The user's own tasks

    /// <summary>Replaces the user's tasks with the ones saved for the current workspace.</summary>
    public async Task LoadUserTasksAsync()
    {
        foreach (var task in UserTasks) task.PropertyChanged -= UserTaskChanged;
        UserTasks.Clear();
        if (_host.WorkspacePath is not { } workspace) return;
        foreach (var item in await _store.LoadAsync(workspace, _host.Lifetime)) AddUserTask(new(item.Text, item.IsDone, item.Path, item.Line));
    }
    private void AddUserTask(UserTaskViewModel task) { task.PropertyChanged += UserTaskChanged; UserTasks.Add(task); }
    private void UserTaskChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(UserTaskViewModel.IsDone)) _ = _host.GuardAsync(SaveUserTasksAsync); }
    private Task SaveUserTasksAsync() => _host.WorkspacePath is { } workspace ? _store.SaveAsync(workspace, UserTasks.Select(t => t.ToItem()), _host.Lifetime) : Task.CompletedTask;

    /// <summary>Adds the typed task; starting it with @ links it to the caret line of the active document.</summary>
    [RelayCommand] private Task AddAsync() => _host.GuardAsync(async () =>
    {
        var text = NewTaskText.Trim();
        if (text.Length == 0) return;
        if (_host.WorkspacePath is null) { _host.ReportStatus("Open a solution to keep a task list for it"); return; }
        var location = text.StartsWith('@') ? _host.ActiveLocation : null;
        AddUserTask(new(location is null ? text : text[1..].Trim(), false, location?.Path, location?.Line ?? 0));
        NewTaskText = "";
        await SaveUserTasksAsync();
    });
    [RelayCommand] private Task RemoveAsync(UserTaskViewModel? task) => task == null ? Task.CompletedTask : _host.GuardAsync(async () =>
    {
        task.PropertyChanged -= UserTaskChanged; UserTasks.Remove(task);
        await SaveUserTasksAsync();
    });
    [RelayCommand] private Task ClearCompletedAsync() => _host.GuardAsync(async () =>
    {
        foreach (var task in UserTasks.Where(t => t.IsDone).ToArray()) { task.PropertyChanged -= UserTaskChanged; UserTasks.Remove(task); }
        await SaveUserTasksAsync();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scan?.Cancel(); _scan?.Dispose(); _scan = null;
    }
}
