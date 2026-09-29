using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WpfStudio.App.Features.Git;

public sealed partial class GitViewModel : ObservableObject, IDisposable
{
    public const int HistoryPageSize = 50;
    public const int ChangesTab = 0, HistoryTab = 1, OutputTab = 2;

    private readonly GitService _service;
    private readonly Func<CancellationToken, Task<bool>>? _saveBeforeOperation;
    private string? _workspacePath;
    private CancellationTokenSource? _operation;
    private Task _activeTask = Task.CompletedTask;
    private int _workspaceVersion;
    private bool _switchingWorkspace;
    private bool _disposed;
    // Diffs and commit details load beside Git actions (they only read), each cancelling its own previous request.
    private CancellationTokenSource? _diffLoad, _commitLoad, _commitFileLoad;
    private Task _diffTask = Task.CompletedTask, _commitTask = Task.CompletedTask, _commitFileTask = Task.CompletedTask;
    private bool _quietSelection;

    public GitViewModel(GitService service, Func<CancellationToken, Task<bool>>? saveBeforeOperation = null)
    {
        _service = service;
        _saveBeforeOperation = saveBeforeOperation;
    }

    /// <summary>Raised when a View diff command has loaded the diff it asked for.</summary>
    public event Action<GitDiff>? DiffRequested;
    public event Action<string>? OpenFileRequested;
    /// <summary>Raised only after a successful Git operation that can replace working files.</summary>
    public event Action? WorkspaceFilesChanged;
    public ObservableCollection<GitChange> Staged { get; } = [];
    public ObservableCollection<GitChange> Unstaged { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];
    /// <summary>Every commit loaded so far, newest first.</summary>
    public ObservableCollection<GitCommit> History { get; } = [];
    /// <summary>The loaded commits that match <see cref="HistoryFilter"/>.</summary>
    public ObservableCollection<GitCommit> VisibleHistory { get; } = [];
    public ObservableCollection<GitCommitFile> CommitFiles { get; } = [];
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
    [ObservableProperty] public partial int SelectedTab { get; set; }
    /// <summary>The selected working or staged file's diff.</summary>
    [ObservableProperty] public partial GitDiff? CurrentDiff { get; set; }
    [ObservableProperty] public partial bool IsDiffLoading { get; set; }
    [ObservableProperty] public partial string HistoryFilter { get; set; } = "";
    [ObservableProperty] public partial bool HasMoreHistory { get; set; }
    [ObservableProperty] public partial GitCommitDetails? CommitDetails { get; set; }
    [ObservableProperty] public partial bool IsCommitLoading { get; set; }
    [ObservableProperty] public partial GitCommitFile? SelectedCommitFile { get; set; }
    /// <summary>The selected commit file's diff against the commit's first parent.</summary>
    [ObservableProperty] public partial GitDiff? CommitDiff { get; set; }

    /// <summary>
    /// When the pane is on screen the first change and the newest commit are selected automatically, so the
    /// diff area is never empty. Off for headless use, where every read would be wasted work.
    /// </summary>
    public bool AutoSelect { get; set; }
    public string StagedHeading => $"Staged changes ({Staged.Count})";
    public string UnstagedHeading => $"Working changes ({Unstaged.Count})";
    public int ChangeCount => Staged.Concat(Unstaged).Select(change => change.Path).Distinct(StringComparer.Ordinal).Count();
    public bool HasRepository => RepositoryRoot is not null;
    public bool HasError => Error.Length > 0;
    public GitChange? SelectedChange => SelectedUnstaged ?? SelectedStaged;
    public bool SelectedChangeIsStaged => SelectedUnstaged is null && SelectedStaged is not null;
    public string CommitButtonText => Staged.Count == 0 ? "Commit" : $"Commit {Staged.Count} staged file{(Staged.Count == 1 ? "" : "s")}";
    public string ChangesPlaceholder => !HasRepository ? Status : ChangeCount == 0 ? "Nothing to commit. The working tree is clean." : "Select a file to see its changes.";
    public bool IsHistoryFiltered => HistoryFilter.Trim().Length > 0;
    public bool HistoryFilterHasNoMatches => IsHistoryFiltered && VisibleHistory.Count == 0 && History.Count > 0;
    public string HistoryHeading => IsHistoryFiltered
        ? $"{VisibleHistory.Count} of {History.Count} loaded commits match"
        : $"{History.Count}{(HasMoreHistory ? "+" : "")} commit{(History.Count == 1 ? "" : "s")} on {Branch}";

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnRepositoryRootChanged(string? value) { OnPropertyChanged(nameof(HasRepository)); NotifyCommands(); }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnCommitMessageChanged(string value) => CommitCommand.NotifyCanExecuteChanged();
    partial void OnNewBranchNameChanged(string value) => CreateBranchCommand.NotifyCanExecuteChanged();
    partial void OnSelectedBranchChanged(string? value) => SwitchBranchCommand.NotifyCanExecuteChanged();
    partial void OnHasMoreHistoryChanged(bool value) { LoadMoreHistoryCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(HistoryHeading)); }
    partial void OnHistoryFilterChanged(string value) { ApplyHistoryFilter(); OnPropertyChanged(nameof(IsHistoryFiltered)); }
    partial void OnSelectedTabChanged(int value) => EnsureSelection();

    partial void OnSelectedUnstagedChanged(GitChange? value)
    {
        if (_quietSelection) return;
        // One diff at a time: choosing a file in one list clears the other.
        if (value is not null) Quietly(() => SelectedStaged = null);
        _ = LoadSelectedDiff();
    }

    partial void OnSelectedStagedChanged(GitChange? value)
    {
        if (_quietSelection) return;
        if (value is not null) Quietly(() => SelectedUnstaged = null);
        _ = LoadSelectedDiff();
    }

    partial void OnSelectedCommitChanged(GitCommit? value) { if (!_quietSelection) _ = LoadCommit(value); }
    partial void OnSelectedCommitFileChanged(GitCommitFile? value) { if (!_quietSelection) _ = LoadCommitFile(value); }

    public async Task SetWorkspaceAsync(string? workspacePath, CancellationToken cancellationToken = default)
    {
        var version = ++_workspaceVersion;
        _switchingWorkspace = true;
        NotifyCommands();
        _operation?.Cancel();
        await _activeTask;
        await CancelLoadsAsync();
        if (version != _workspaceVersion) return;
        _workspacePath = workspacePath;
        RepositoryRoot = null;
        Branch = "Loading repository…";
        Tracking = "";
        Quietly(() =>
        {
            Staged.Clear();
            Unstaged.Clear();
            History.Clear();
            VisibleHistory.Clear();
            Branches.Clear();
            SelectedBranch = null;
            SelectedStaged = null;
            SelectedUnstaged = null;
            SelectedCommit = null;
        });
        HasMoreHistory = false;
        ClearCommit();
        CurrentDiff = null;
        NotifyLists();
        _switchingWorkspace = false;
        await RunAsync("Refresh", token => RefreshCoreAsync(token), cancellationToken);
    }

    private bool CanRefresh() => !IsBusy && !_switchingWorkspace && !_disposed;
    private bool CanOperate() => CanRefresh() && HasRepository;
    private bool CanUseChange(GitChange? change) => CanOperate() && change is not null;
    private bool CanStageAll() => CanOperate() && Unstaged.Count > 0;
    private bool CanUnstageAll() => CanOperate() && Staged.Count > 0;
    private bool CanCommit() => CanUnstageAll() && !Staged.Any(x => x.IsConflict) && !Unstaged.Any(x => x.IsConflict) && !string.IsNullOrWhiteSpace(CommitMessage);
    private bool CanSwitch() => CanOperate() && !string.IsNullOrWhiteSpace(SelectedBranch) && SelectedBranch != Branch;
    private bool CanCreateBranch() => CanOperate() && !string.IsNullOrWhiteSpace(NewBranchName);
    private bool CanLoadMoreHistory() => CanOperate() && HasMoreHistory;
    private bool CanOpenCommitFile(GitCommitFile? file) => HasRepository && file is not null && file.Status != 'D';

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
    private Task ViewWorkingDiffAsync(GitChange? change) => ShowChangeAsync(change, staged: false);

    [RelayCommand(CanExecute = nameof(CanUseChange))]
    private Task ViewStagedDiffAsync(GitChange? change) => ShowChangeAsync(change, staged: true);

    [RelayCommand(CanExecute = nameof(CanUseChange))]
    private void OpenFile(GitChange? change)
    {
        if (change is null || RepositoryRoot is null) return;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(RepositoryRoot, change.Path));
        if (File.Exists(path)) OpenFileRequested?.Invoke(path);
        else Error = "This file has been deleted. Its last version is shown in the diff.";
    }

    [RelayCommand(CanExecute = nameof(CanOpenCommitFile))]
    private void OpenCommitFile(GitCommitFile? file)
    {
        if (file is null || RepositoryRoot is null) return;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(RepositoryRoot, file.Path));
        if (File.Exists(path)) OpenFileRequested?.Invoke(path);
        else Error = "This file no longer exists in the working tree.";
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreHistory))]
    private Task LoadMoreHistoryAsync() => RunAsync("Load more history", async token =>
    {
        var version = _workspaceVersion;
        var more = await _service.LoadHistoryAsync(RepositoryRoot!, History.Count, HistoryPageSize + 1, token);
        if (version != _workspaceVersion) return;
        foreach (var commit in more.Take(HistoryPageSize).Where(commit => !History.Any(existing => existing.Id == commit.Id))) History.Add(commit);
        HasMoreHistory = more.Count > HistoryPageSize;
        ApplyHistoryFilter();
        Status = $"{History.Count} commits loaded";
    });

    [RelayCommand]
    private void ClearHistoryFilter() => HistoryFilter = "";

    private async Task ShowChangeAsync(GitChange? change, bool staged)
    {
        if (change is null) return;
        SelectedTab = ChangesTab;
        Quietly(() =>
        {
            SelectedUnstaged = staged ? null : change;
            SelectedStaged = staged ? change : null;
        });
        await LoadSelectedDiff();
        if (CurrentDiff is { } diff && SelectedChange == change) DiffRequested?.Invoke(diff);
    }

    /// <summary>Waits for diff and commit reads started by selection changes.</summary>
    public async Task WhenLoadedAsync()
    {
        await _diffTask;
        await _commitTask;
        await _commitFileTask;
    }

    private Task LoadSelectedDiff()
    {
        OnPropertyChanged(nameof(SelectedChange));
        OnPropertyChanged(nameof(SelectedChangeIsStaged));
        _diffLoad?.Cancel();
        _diffLoad = null;
        var change = SelectedChange;
        if (change is null || RepositoryRoot is not { } root)
        {
            IsDiffLoading = false;
            CurrentDiff = null;
            return _diffTask = Task.CompletedTask;
        }
        var load = _diffLoad = new CancellationTokenSource();
        return _diffTask = LoadDiffCoreAsync(root, change, SelectedChangeIsStaged, load);
    }

    private async Task LoadDiffCoreAsync(string root, GitChange change, bool staged, CancellationTokenSource load)
    {
        IsDiffLoading = true;
        try
        {
            var diff = await _service.GetDiffAsync(root, change, staged, load.Token);
            if (!load.IsCancellationRequested) CurrentDiff = diff;
        }
        catch (Exception exception)
        {
            if (!load.IsCancellationRequested && exception is not OperationCanceledException)
                CurrentDiff = new(change.Path, System.IO.Path.Combine(root, change.Path), "", Message: GitDiagnosticSanitizer.Redact(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_diffLoad, load)) { _diffLoad = null; IsDiffLoading = false; }
        }
    }

    private Task LoadCommit(GitCommit? commit)
    {
        _commitLoad?.Cancel();
        _commitLoad = null;
        IsCommitLoading = false;
        if (commit is not null && CommitDetails?.Id == commit.Id) return _commitTask = Task.CompletedTask;
        if (commit is null || RepositoryRoot is not { } root)
        {
            ClearCommit();
            return _commitTask = Task.CompletedTask;
        }
        var load = _commitLoad = new CancellationTokenSource();
        return _commitTask = LoadCommitCoreAsync(root, commit, load);
    }

    private async Task LoadCommitCoreAsync(string root, GitCommit commit, CancellationTokenSource load)
    {
        IsCommitLoading = true;
        try
        {
            var details = await _service.GetCommitDetailsAsync(root, commit.Id, load.Token);
            if (load.IsCancellationRequested) return;
            CommitDetails = details;
            Quietly(() =>
            {
                Replace(CommitFiles, details.Files);
                SelectedCommitFile = details.Files.FirstOrDefault();
            });
            await LoadCommitFile(SelectedCommitFile);
        }
        catch (Exception exception)
        {
            if (!load.IsCancellationRequested && exception is not OperationCanceledException)
            {
                ClearCommit();
                Error = GitDiagnosticSanitizer.Redact(exception.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_commitLoad, load)) { _commitLoad = null; IsCommitLoading = false; }
        }
    }

    private Task LoadCommitFile(GitCommitFile? file)
    {
        _commitFileLoad?.Cancel();
        _commitFileLoad = null;
        OpenCommitFileCommand.NotifyCanExecuteChanged();
        if (file is null || CommitDetails is not { } details || RepositoryRoot is not { } root)
        {
            CommitDiff = null;
            return _commitFileTask = Task.CompletedTask;
        }
        var load = _commitFileLoad = new CancellationTokenSource();
        return _commitFileTask = LoadCommitFileCoreAsync(root, details, file, load);
    }

    private async Task LoadCommitFileCoreAsync(string root, GitCommitDetails details, GitCommitFile file, CancellationTokenSource load)
    {
        try
        {
            var diff = await _service.GetCommitFileDiffAsync(root, details, file, load.Token);
            if (!load.IsCancellationRequested) CommitDiff = diff;
        }
        catch (Exception exception)
        {
            if (!load.IsCancellationRequested && exception is not OperationCanceledException)
                CommitDiff = new(file.Path, System.IO.Path.Combine(root, file.Path), "", Message: GitDiagnosticSanitizer.Redact(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_commitFileLoad, load)) _commitFileLoad = null;
        }
    }

    private void ClearCommit()
    {
        _commitFileLoad?.Cancel();
        _commitFileLoad = null;
        CommitDetails = null;
        Quietly(() =>
        {
            CommitFiles.Clear();
            SelectedCommitFile = null;
        });
        CommitDiff = null;
    }

    private async Task CancelLoadsAsync()
    {
        foreach (var load in new[] { _diffLoad, _commitLoad, _commitFileLoad }) load?.Cancel();
        // Awaiting lets cancelled Git processes exit before the caller replaces or deletes the repository.
        await WhenLoadedAsync();
    }

    /// <summary>Selects the first change and, on the History tab, the newest commit when nothing is selected.</summary>
    private void EnsureSelection()
    {
        if (!AutoSelect || _disposed || !HasRepository) return;
        if (SelectedTab == ChangesTab && SelectedChange is null)
        {
            if (Unstaged.FirstOrDefault() is { } working) SelectedUnstaged = working;
            else if (Staged.FirstOrDefault() is { } staged) SelectedStaged = staged;
        }
        if (SelectedTab == HistoryTab && SelectedCommit is null && VisibleHistory.FirstOrDefault() is { } newest) SelectedCommit = newest;
    }

    /// <summary>Keeps the visible list in history order while changing it in place, so the selection survives filtering.</summary>
    private void ApplyHistoryFilter()
    {
        var filter = HistoryFilter.Trim();
        bool Matches(GitCommit commit) => filter.Length == 0
            || commit.Subject.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || commit.Author.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || commit.Refs.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || commit.Id.StartsWith(filter, StringComparison.OrdinalIgnoreCase);
        var visible = 0;
        foreach (var commit in History)
        {
            if (!Matches(commit)) continue;
            var existing = -1;
            for (var index = visible; index < VisibleHistory.Count; index++)
                if (VisibleHistory[index] == commit) { existing = index; break; }
            if (existing < 0) VisibleHistory.Insert(visible, commit);
            else
                for (var index = existing - 1; index >= visible; index--) VisibleHistory.RemoveAt(index);
            visible++;
        }
        while (VisibleHistory.Count > visible) VisibleHistory.RemoveAt(VisibleHistory.Count - 1);
        OnPropertyChanged(nameof(HistoryHeading));
        OnPropertyChanged(nameof(HistoryFilterHasNoMatches));
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
            if (version == _workspaceVersion && label is not ("Refresh" or "Load more history")) AppendOutput(label + " completed.");
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
        // Keep as many commits as the reader has paged in; one extra tells whether older ones exist.
        var historyLimit = Math.Max(HistoryPageSize, History.Count);
        var snapshot = await _service.LoadAsync(_workspacePath, cancellationToken, historyLimit + 1);
        if (version != _workspaceVersion) return;
        var rootChanged = !string.Equals(RepositoryRoot, snapshot.Root, StringComparison.OrdinalIgnoreCase);
        RepositoryRoot = snapshot.Root;
        Branch = snapshot.Branch;
        Tracking = snapshot.Tracking;
        Status = snapshot.Message;
        // Rebuilding the lists would clear their selection; keep the reader on the same file and commit instead.
        var selected = SelectedChange;
        var selectedWasStaged = SelectedChangeIsStaged;
        var selectedCommit = SelectedCommit;
        Quietly(() =>
        {
            Replace(Staged, snapshot.Changes.Where(x => x.IsStaged));
            Replace(Unstaged, snapshot.Changes.Where(x => x.IsUnstaged));
            Replace(Branches, snapshot.Branches);
            Replace(History, snapshot.History.Take(historyLimit));
            ApplyHistoryFilter();
            SelectedBranch = snapshot.Branches.Contains(Branch) ? Branch : null;
            var (preferred, other) = selectedWasStaged ? (Staged, Unstaged) : (Unstaged, Staged);
            var match = selected is null ? null : preferred.FirstOrDefault(x => x.Path == selected.Path);
            var matchIsStaged = selectedWasStaged;
            if (selected is not null && match is null)
            {
                match = other.FirstOrDefault(x => x.Path == selected.Path);
                matchIsStaged = !selectedWasStaged;
            }
            SelectedUnstaged = match is not null && !matchIsStaged ? match : null;
            SelectedStaged = match is not null && matchIsStaged ? match : null;
            SelectedCommit = selectedCommit is null ? null : VisibleHistory.FirstOrDefault(x => x.Id == selectedCommit.Id);
        });
        HasMoreHistory = snapshot.History.Count > historyLimit;
        NotifyLists();
        NotifyCommands();
        // Git may have changed the file (stage, commit, pull), so reload its diff even when the selection is unchanged.
        _ = LoadSelectedDiff();
        if (rootChanged || SelectedCommit is null) _ = LoadCommit(SelectedCommit);
        EnsureSelection();
    }

    private void Quietly(Action action)
    {
        var previous = _quietSelection;
        _quietSelection = true;
        try { action(); }
        finally { _quietSelection = previous; }
    }

    private void AppendOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var next = Output + $"[{DateTime.Now:HH:mm:ss}] {GitDiagnosticSanitizer.Redact(text.Trim())}\n";
        Output = next.Length > 100_000 ? next[^100_000..] : next;
    }

    private void NotifyLists()
    {
        OnPropertyChanged(nameof(StagedHeading));
        OnPropertyChanged(nameof(UnstagedHeading));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(HistoryHeading));
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(ChangesPlaceholder));
    }

    private void NotifyCommands()
    {
        foreach (var command in new IRelayCommand[] { RefreshCommand, StageFileCommand, StageAllCommand, UnstageFileCommand,
            UnstageAllCommand, CommitCommand, FetchCommand, PullCommand, PushCommand, SwitchBranchCommand, CreateBranchCommand,
            ViewWorkingDiffCommand, ViewStagedDiffCommand, OpenFileCommand, OpenCommitFileCommand, LoadMoreHistoryCommand })
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
        foreach (var load in new[] { _diffLoad, _commitLoad, _commitFileLoad }) load?.Cancel();
        NotifyCommands();
    }
}
