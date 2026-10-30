using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core;
using WpfStudio.Core.Text;

namespace WpfStudio.App.ViewModels;

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
public sealed partial class ShellViewModel
{
    private readonly WorkspaceStateStore<UserTaskItem> _userTasks = new("tasks");
    private CancellationTokenSource? _taskScan;
    private List<TaskComment> _taskComments = [];
    public ObservableCollection<TaskComment> TaskCommentItems { get; } = [];
    public ObservableCollection<UserTaskViewModel> UserTasks { get; } = [];
    [ObservableProperty] public partial TaskComment? SelectedTaskComment { get; set; }
    [ObservableProperty] public partial string TaskFilter { get; set; } = "";
    [ObservableProperty] public partial string NewTaskText { get; set; } = "";
    [ObservableProperty] public partial string TaskListStatus { get; set; } = "Open a solution to list its TODO comments.";
    partial void OnTaskFilterChanged(string value) => RefreshVisibleTaskComments();

    /// <summary>Scans every source file in the workspace, reading open documents from their editor buffers.</summary>
    [RelayCommand] private Task RefreshTaskListAsync() => GuardAsync(ScanTaskCommentsAsync);
    private async Task ScanTaskCommentsAsync()
    {
        _taskScan?.Cancel(); _taskScan?.Dispose();
        var scan = _taskScan = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (Workspace == null) { _taskComments = []; RefreshVisibleTaskComments(); TaskListStatus = "Open a solution to list its TODO comments."; return; }
        TaskListStatus = "Scanning for TODO comments…";
        var buffers = Documents.ToDictionary(d => d.State.Path, d => d.State.Content, StringComparer.OrdinalIgnoreCase);
        var files = _allFiles.Where(TaskComments.Supports).ToArray();
        var found = await Task.Run(() =>
        {
            var results = new List<TaskComment>();
            foreach (var file in files)
            {
                scan.Token.ThrowIfCancellationRequested();
                try
                {
                    if (!buffers.TryGetValue(file, out var text))
                    {
                        if (new FileInfo(file).Length > TaskComments.MaximumFileLength) continue;
                        text = File.ReadAllText(file);
                    }
                    results.AddRange(TaskComments.Find(file, text));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return results;
        }, scan.Token);
        if (scan.IsCancellationRequested) return;
        _taskComments = found;
        RefreshVisibleTaskComments();
        TaskListStatus = found.Count == 0 ? $"No TODO comments in {files.Length} files" : $"{found.Count} comment{(found.Count == 1 ? "" : "s")} in {found.Select(c => c.Path).Distinct().Count()} files";
    }
    /// <summary>A saved document's comments replace the ones found in it before, without rescanning the workspace.</summary>
    private void UpdateTaskComments(string path, string text)
    {
        if (Workspace == null || !TaskComments.Supports(path)) return;
        _taskComments = _taskComments.Where(c => !c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Concat(TaskComments.Find(path, text)).ToList();
        RefreshVisibleTaskComments();
    }
    private void RefreshVisibleTaskComments()
    {
        var terms = TaskFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        TaskCommentItems.Clear();
        foreach (var comment in _taskComments.OrderByDescending(c => c.IsHighPriority).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Line))
            if (terms.All(term => comment.Token.Contains(term, IgnoreCase) || comment.Text.Contains(term, IgnoreCase) || comment.FileName.Contains(term, IgnoreCase) || comment.Owner.Contains(term, IgnoreCase)))
                TaskCommentItems.Add(comment);
    }
    [RelayCommand] private Task NavigateTaskCommentAsync(TaskComment? comment) => comment == null ? Task.CompletedTask : GuardAsync(() => NavigateAsync(comment.Path, comment.Line, comment.Column));
    [RelayCommand] private Task NavigateUserTaskAsync(UserTaskViewModel? task) => task?.Path == null ? Task.CompletedTask : GuardAsync(() => NavigateAsync(task.Path, Math.Max(1, task.Line), 1));

    // ------------------------------------------------------------------ The user's own tasks
    private async Task LoadUserTasksAsync()
    {
        foreach (var task in UserTasks) task.PropertyChanged -= UserTaskChanged;
        UserTasks.Clear();
        if (Workspace == null) return;
        foreach (var item in await _userTasks.LoadAsync(Workspace.Path, _lifetime.Token)) AddUserTask(new(item.Text, item.IsDone, item.Path, item.Line));
    }
    private void AddUserTask(UserTaskViewModel task) { task.PropertyChanged += UserTaskChanged; UserTasks.Add(task); }
    private void UserTaskChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(UserTaskViewModel.IsDone)) _ = GuardAsync(SaveUserTasksAsync); }
    private Task SaveUserTasksAsync() => Workspace == null ? Task.CompletedTask : _userTasks.SaveAsync(Workspace.Path, UserTasks.Select(t => t.ToItem()), _lifetime.Token);

    /// <summary>Adds the typed task; starting it with @ links it to the caret line of the active document.</summary>
    [RelayCommand] private Task AddUserTaskAsync() => GuardAsync(async () =>
    {
        var text = NewTaskText.Trim();
        if (text.Length == 0) return;
        if (Workspace == null) { Status = "Open a solution to keep a task list for it"; return; }
        var linked = text.StartsWith('@') && ActiveDocument != null;
        AddUserTask(new(linked ? text[1..].Trim() : text, false, linked ? ActiveDocument!.State.Path : null, linked ? ActiveDocument!.State.CaretLine : 0));
        NewTaskText = "";
        await SaveUserTasksAsync();
    });
    [RelayCommand] private Task RemoveUserTaskAsync(UserTaskViewModel? task) => task == null ? Task.CompletedTask : GuardAsync(async () =>
    {
        task.PropertyChanged -= UserTaskChanged; UserTasks.Remove(task);
        await SaveUserTasksAsync();
    });
    [RelayCommand] private Task ClearCompletedUserTasksAsync() => GuardAsync(async () =>
    {
        foreach (var task in UserTasks.Where(t => t.IsDone).ToArray()) { task.PropertyChanged -= UserTaskChanged; UserTasks.Remove(task); }
        await SaveUserTasksAsync();
    });
}
