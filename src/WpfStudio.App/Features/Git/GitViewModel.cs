using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WpfStudio.App.Features.Git;

public sealed partial class GitViewModel : ObservableObject, IDisposable
{
    private readonly GitService _service;
    private readonly Func<CancellationToken, Task<bool>>? _saveBeforeOperation;
    private string? _workspacePath;
    private CancellationTokenSource? _operation;
    private Task _activeTask = Task.CompletedTask;
    private int _workspaceVersion;
    private bool _switchingWorkspace;
    private bool _disposed;

    public GitViewModel(GitService service, Func<CancellationToken, Task<bool>>? saveBeforeOperation = null)
    {
        _service = service;
        _saveBeforeOperation = saveBeforeOperation;
    }

    public event Action<GitDiff>? DiffRequested;
    public event Action<string>? OpenFileRequested;
    /// <summary>Raised only after a successful Git operation that can replace working files.</summary>
    public event Action? WorkspaceFilesChanged;
    public ObservableCollection<GitChange> Staged { get; } = [];
    public ObservableCollection<GitChange> Unstaged { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];
    public ObservableCollection<GitCommit> History { get; } = [];
    [ObservableProperty] public partial string? RepositoryRoot { get; set; }
    [ObservableProperty] public partial string Branch { get; set; } = "No repository";
    [ObservableProperty] public partial string Tracking { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "Open a solution or project to see its Git repository.";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string Output { get; set; } = "Git actions run only when you request them. Credentials use your existing Git configuration.\n";
    [ObservableProperty] public partial string CommitMessage { get; set; } = "";
    [ObservableProperty] public partial string NewBranchName { get; set; } = "";
    [ObservableProperty] public partial string? SelectedBranch { get; set; }
    [ObservableProperty] public partial GitChange? SelectedStaged { get; set; }
    [ObservableProperty] public partial GitChange? SelectedUnstaged { get; set; }
    [ObservableProperty] public partial GitCommit? SelectedCommit { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string DiffText { get; set; } = "Select a changed file or commit, then choose View diff.";
    [ObservableProperty] public partial string DiffTitle { get; set; } = "Diff preview";
    [ObservableProperty] public partial int SelectedTab { get; set; }

    public string StagedHeading => $"Staged changes ({Staged.Count})";
    public string UnstagedHeading => $"Working changes ({Unstaged.Count})";
    public bool HasRepository => RepositoryRoot is not null;
    public bool HasError => Error.Length > 0;

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnRepositoryRootChanged(string? value) { OnPropertyChanged(nameof(HasRepository)); NotifyCommands(); }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnCommitMessageChanged(string value) => CommitCommand.NotifyCanExecuteChanged();
    partial void OnNewBranchNameChanged(string value) => CreateBranchCommand.NotifyCanExecuteChanged();
    partial void OnSelectedBranchChanged(string? value) => SwitchBranchCommand.NotifyCanExecuteChanged();

    public async Task SetWorkspaceAsync(string? workspacePath, CancellationToken cancellationToken = default)
    {
        var version = ++_workspaceVersion;
        _switchingWorkspace = true;
        NotifyCommands();
        _operation?.Cancel();
        await _activeTask;
        if (version != _workspaceVersion) return;
        _workspacePath = workspacePath;
        RepositoryRoot = null;
        Branch = "Loading repository…";
        Tracking = "";
        Staged.Clear();
        Unstaged.Clear();
        History.Clear();
        Branches.Clear();
        SelectedBranch = null;
        SelectedStaged = null;
        SelectedUnstaged = null;
        SelectedCommit = null;
        OnPropertyChanged(nameof(StagedHeading));
        OnPropertyChanged(nameof(UnstagedHeading));
        _switchingWorkspace = false;
        await RunAsync("Refresh", token => RefreshCoreAsync(token), cancellationToken);
    }

    private bool CanRefresh() => !IsBusy && !_switchingWorkspace && !_disposed;
    private bool CanOperate() => CanRefresh() && HasRepository;
    private bool CanUseChange(GitChange? change) => CanOperate() && change is not null;
    private bool CanUseCommit(GitCommit? commit) => CanOperate() && commit is not null;
    private bool CanStageAll() => CanOperate() && Unstaged.Count > 0;
    private bool CanUnstageAll() => CanOperate() && Staged.Count > 0;
    private bool CanCommit() => CanUnstageAll() && !Staged.Any(x => x.IsConflict) && !Unstaged.Any(x => x.IsConflict) && !string.IsNullOrWhiteSpace(CommitMessage);
    private bool CanSwitch() => CanOperate() && !string.IsNullOrWhiteSpace(SelectedBranch) && SelectedBranch != Branch;
    private bool CanCreateBranch() => CanOperate() && !string.IsNullOrWhiteSpace(NewBranchName);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunAsync("Refresh", RefreshCoreAsync);

    [RelayCommand]
    private void Cancel() => _operation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanUseChange))]
    private Task StageFileAsync(GitChange? change) => change is null ? Task.CompletedTask :
        MutateAsync("Stage file", token => _service.StageAsync(RepositoryRoot!, change, token), save: true);

    [RelayCommand(CanExecute = nameof(CanStageAll))]
    private Task StageAllAsync() => MutateAsync("Stage all", token => _service.StageAsync(RepositoryRoot!, null, token), save: true);

    [RelayCommand(CanExecute = nameof(CanUseChange))]
    private Task UnstageFileAsync(GitChange? change) => change is null ? Task.CompletedTask :
        MutateAsync("Unstage file", token => _service.UnstageAsync(RepositoryRoot!, change, token));

    [RelayCommand(CanExecute = nameof(CanUnstageAll))]
    private Task UnstageAllAsync() => MutateAsync("Unstage all", token => _service.UnstageAsync(RepositoryRoot!, null, token));

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task CommitAsync() => MutateAsync("Commit staged changes", async token =>
    {
        var result = await _service.CommitAsync(RepositoryRoot!, CommitMessage, token);
        CommitMessage = "";
        return result;
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task FetchAsync() => MutateAsync("Fetch", token => _service.FetchAsync(RepositoryRoot!, token));

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task PullAsync() => MutateAsync("Pull (fast-forward only)", token => _service.PullAsync(RepositoryRoot!, token), save: true, changesWorkspaceFiles: true);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task PushAsync() => MutateAsync("Push", token => _service.PushAsync(RepositoryRoot!, token));

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task SwitchBranchAsync() => MutateAsync("Switch branch", token => _service.SwitchBranchAsync(RepositoryRoot!, SelectedBranch!, cancellationToken: token), save: true, changesWorkspaceFiles: true);

    [RelayCommand(CanExecute = nameof(CanCreateBranch))]
    private Task CreateBranchAsync() => MutateAsync("Create branch", async token =>
    {
        var result = await _service.SwitchBranchAsync(RepositoryRoot!, NewBranchName.Trim(), create: true, cancellationToken: token);
        NewBranchName = "";
        return result;
    }, save: true, changesWorkspaceFiles: true);

    [RelayCommand(CanExecute = nameof(CanUseChange))]
    private Task ViewWorkingDiffAsync(GitChange? change) => change is null ? Task.CompletedTask :
        RunAsync("Read diff", async token => ShowDiff(await _service.GetDiffAsync(RepositoryRoot!, change, false, token)));

    [RelayCommand(CanExecute = nameof(CanUseChange))]
    private Task ViewStagedDiffAsync(GitChange? change) => change is null ? Task.CompletedTask :
        RunAsync("Read staged diff", async token => ShowDiff(await _service.GetDiffAsync(RepositoryRoot!, change, true, token)));

    [RelayCommand(CanExecute = nameof(CanUseCommit))]
    private Task ViewCommitAsync(GitCommit? commit) => commit is null ? Task.CompletedTask :
        RunAsync("Read commit", async token => ShowDiff(await _service.GetCommitDiffAsync(RepositoryRoot!, commit.Id, token)));

    [RelayCommand(CanExecute = nameof(CanUseChange))]
    private void OpenFile(GitChange? change)
    {
        if (change is null || RepositoryRoot is null) return;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(RepositoryRoot, change.Path));
        if (File.Exists(path)) OpenFileRequested?.Invoke(path);
        else Error = "This file has been deleted. Use View diff to inspect its changes.";
    }

    private void ShowDiff(GitDiff diff)
    {
        DiffTitle = diff.Title;
        DiffText = diff.Text;
        Status = "Diff loaded";
        SelectedTab = 2;
        DiffRequested?.Invoke(diff);
    }

    private Task MutateAsync(string label, Func<CancellationToken, Task<string>> action, bool save = false, bool changesWorkspaceFiles = false) => RunAsync(label, async token =>
    {
        var version = _workspaceVersion;
        if (save && _saveBeforeOperation is not null && !await _saveBeforeOperation(token))
            throw new InvalidOperationException("The Git action was cancelled because open documents could not be saved.");
        AppendOutput(await action(token));
        try { await RefreshCoreAsync(token); }
        finally
        {
            // Notify even if the status refresh failed after Git changed files,
            // but never refresh a newly opened workspace for an old operation.
            if (changesWorkspaceFiles && version == _workspaceVersion) WorkspaceFilesChanged?.Invoke();
        }
    });

    private Task RunAsync(string label, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (IsBusy || _switchingWorkspace || _disposed) return Task.CompletedTask;
        _activeTask = RunCoreAsync(label, action, cancellationToken);
        return _activeTask;
    }

    private async Task RunCoreAsync(string label, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        var version = _workspaceVersion;
        IsBusy = true;
        Error = "";
        Status = label + "…";
        try
        {
            await action(operation.Token);
            if (version == _workspaceVersion && label != "Refresh") AppendOutput(label + " completed.");
        }
        catch (OperationCanceledException) { if (version == _workspaceVersion) Status = "Git action cancelled. Refresh to see the current repository state."; }
        catch (Exception exception)
        {
            if (version == _workspaceVersion)
            {
                Error = GitDiagnosticSanitizer.Redact(exception.Message);
                Status = label + " failed";
                AppendOutput(Error);
            }
        }
        finally { _operation = null; IsBusy = false; }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var version = _workspaceVersion;
        var snapshot = await _service.LoadAsync(_workspacePath, cancellationToken);
        if (version != _workspaceVersion) return;
        RepositoryRoot = snapshot.Root;
        Branch = snapshot.Branch;
        Tracking = snapshot.Tracking;
        Status = snapshot.Message;
        Replace(Staged, snapshot.Changes.Where(x => x.IsStaged));
        Replace(Unstaged, snapshot.Changes.Where(x => x.IsUnstaged));
        Replace(Branches, snapshot.Branches);
        Replace(History, snapshot.History);
        SelectedBranch = snapshot.Branches.Contains(Branch) ? Branch : null;
        OnPropertyChanged(nameof(StagedHeading));
        OnPropertyChanged(nameof(UnstagedHeading));
        NotifyCommands();
    }

    private void AppendOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var next = Output + $"[{DateTime.Now:HH:mm:ss}] {GitDiagnosticSanitizer.Redact(text.Trim())}\n";
        Output = next.Length > 100_000 ? next[^100_000..] : next;
    }

    private void NotifyCommands()
    {
        foreach (var command in new IRelayCommand[] { RefreshCommand, StageFileCommand, StageAllCommand, UnstageFileCommand,
            UnstageAllCommand, CommitCommand, FetchCommand, PullCommand, PushCommand, SwitchBranchCommand, CreateBranchCommand,
            ViewWorkingDiffCommand, ViewStagedDiffCommand, ViewCommitCommand, OpenFileCommand })
            command.NotifyCanExecuteChanged();
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    public void Dispose()
    {
        _disposed = true;
        _operation?.Cancel();
        NotifyCommands();
    }
}
