using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Features.Git;

public sealed partial class GitViewModel : ObservableObject, IDisposable, IGitOperationHost
{
    public const int ChangesTab = 0, HistoryTab = 1, OutputTab = 2;

    private readonly GitService _service;
    private readonly IDesktopService _desktop;
    private readonly Func<CancellationToken, Task<bool>>? _saveBeforeOperation;
    private string? _workspacePath;
    private readonly OperationGate _operations;
    private Task _activeTask = Task.CompletedTask;
    private int _workspaceVersion;
    private bool _switchingWorkspace;
    private bool _disposed;
    // Diffs load beside Git actions (they only read), each cancelling its own previous request.
    private CancellationTokenSource? _diffLoad;
    private Task _diffTask = Task.CompletedTask;
    private bool _quietSelection;

    public GitViewModel(GitService service, IDesktopService desktop, Func<CancellationToken, Task<bool>>? saveBeforeOperation = null)
    {
        _service = service;
        _desktop = desktop;
        _saveBeforeOperation = saveBeforeOperation;
        _operations = new(busy => IsBusy = busy);
        CommitHistory = new(service, this);
        Resolver = new(service, this);
    }

    public CommitHistoryViewModel CommitHistory { get; }
    public ConflictResolverViewModel Resolver { get; }

    /// <summary>Raised when a View diff command has loaded the diff it asked for.</summary>
    public event Action<GitDiff>? DiffRequested;
    public event Action<string>? OpenFileRequested;
    /// <summary>Raised only after a successful Git operation that can replace working files.</summary>
    public event Action? WorkspaceFilesChanged;
    public ObservableCollection<GitChange> Staged { get; } = [];
    public ObservableCollection<GitChange> Unstaged { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRepository))] public partial string? RepositoryRoot { get; set; }
    [ObservableProperty] public partial string Branch { get; set; } = "No repository";
    [ObservableProperty] public partial string Tracking { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "Open a solution or project to see its Git repository.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string Output { get; set; } = "Git actions run only when you request them. Credentials use your existing Git configuration.\n";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CommitCommand))] public partial string CommitMessage { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateBranchCommand))] public partial string NewBranchName { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SwitchBranchCommand))] public partial string? SelectedBranch { get; set; }
    [ObservableProperty] public partial GitChange? SelectedStaged { get; set; }
    [ObservableProperty] public partial GitChange? SelectedUnstaged { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial int SelectedTab { get; set; }
    /// <summary>The selected working or staged file's diff.</summary>
    [ObservableProperty] public partial GitDiff? CurrentDiff { get; set; }
    [ObservableProperty] public partial bool IsDiffLoading { get; set; }

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

    /// <summary>The branch popup; the actions in it close it as they start.</summary>
    [ObservableProperty] public partial bool IsBranchPickerOpen { get; set; }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnRepositoryRootChanged(string? value) => NotifyCommands();
    partial void OnSelectedTabChanged(int value) => EnsureSelection();

    partial void OnSelectedUnstagedChanged(GitChange? value)
    {
        if (_quietSelection) return;
        // One diff at a time: choosing a file in one list clears the other.
        if (value is not null) Quietly(() => SelectedStaged = null);
        LoadSelectedDiff().Forget();
    }

    partial void OnSelectedStagedChanged(GitChange? value)
    {
        if (_quietSelection) return;
        if (value is not null) Quietly(() => SelectedUnstaged = null);
        LoadSelectedDiff().Forget();
    }

    public async Task SetWorkspaceAsync(string? workspacePath, CancellationToken cancellationToken = default)
    {
        var version = ++_workspaceVersion;
        _switchingWorkspace = true;
        NotifyCommands();
        try
        {
            _operations.Cancel();
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
                Branches.Clear();
                SelectedBranch = null;
                SelectedStaged = null;
                SelectedUnstaged = null;
            });
            CommitHistory.Clear();
            CurrentDiff = null;
            NotifyLists();
        }
        // Only the newest switch may lift the guard; an older one returning early leaves it to that switch. A failure must not leave Git disabled.
        finally { if (version == _workspaceVersion) { _switchingWorkspace = false; NotifyCommands(); } }
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

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync(CancellationToken cancel) => RunAsync("Refresh", RefreshCoreAsync, cancel);

    /// <summary>The pane's one Cancel button: it cancels whichever operation is running, including the refresh a workspace switch starts, which no command started.</summary>
    [RelayCommand]
    private void Cancel() => _operations.Cancel();

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanUseChange))]
    private Task StageFileAsync(GitChange? change, CancellationToken cancel) => change is null ? Task.CompletedTask :
        MutateAsync("Stage file", token => _service.StageAsync(RepositoryRoot!, change, token), cancel, save: true);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStageAll))]
    private Task StageAllAsync(CancellationToken cancel) => MutateAsync("Stage all", token => _service.StageAsync(RepositoryRoot!, null, token), cancel, save: true);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanUseChange))]
    private Task UnstageFileAsync(GitChange? change, CancellationToken cancel) => change is null ? Task.CompletedTask :
        MutateAsync("Unstage file", token => _service.UnstageAsync(RepositoryRoot!, change, token), cancel);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanUnstageAll))]
    private Task UnstageAllAsync(CancellationToken cancel) => MutateAsync("Unstage all", token => _service.UnstageAsync(RepositoryRoot!, null, token), cancel);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanCommit))]
    private Task CommitAsync(CancellationToken cancel) => MutateAsync("Commit staged changes", async token =>
    {
        var result = await _service.CommitAsync(RepositoryRoot!, CommitMessage, token);
        CommitMessage = "";
        return result;
    }, cancel);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanOperate))]
    private Task FetchAsync(CancellationToken cancel) => NetworkAsync("Fetch", (signIn, token) => _service.FetchAsync(RepositoryRoot!, token, signIn), cancel);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanOperate))]
    private Task PullAsync(CancellationToken cancel) => NetworkAsync("Pull (fast-forward only)", (signIn, token) => _service.PullAsync(RepositoryRoot!, token, signIn), cancel, save: true, changesWorkspaceFiles: true);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanOperate))]
    private Task PushAsync(CancellationToken cancel) => NetworkAsync("Push", (signIn, token) => _service.PushAsync(RepositoryRoot!, token, signIn), cancel);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanSwitch))]
    private Task SwitchBranchAsync(CancellationToken cancel)
    {
        IsBranchPickerOpen = false;
        return MutateAsync("Switch branch", token => _service.SwitchBranchAsync(RepositoryRoot!, SelectedBranch!, cancellationToken: token), cancel, save: true, changesWorkspaceFiles: true);
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanCreateBranch))]
    private Task CreateBranchAsync(CancellationToken cancel)
    {
        IsBranchPickerOpen = false;
        return MutateAsync("Create branch", async token =>
        {
            var result = await _service.SwitchBranchAsync(RepositoryRoot!, NewBranchName.Trim(), create: true, cancellationToken: token);
            NewBranchName = "";
            return result;
        }, cancel, save: true, changesWorkspaceFiles: true);
    }

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
        await CommitHistory.WhenLoadedAsync();
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
            load.Dispose(); // This load owns its source; the field no longer offers it to anyone who would cancel it.
        }
    }

    private async Task CancelLoadsAsync()
    {
        _diffLoad?.Cancel();
        CommitHistory.CancelLoads();
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
        if (SelectedTab == HistoryTab && CommitHistory.SelectedCommit is null && CommitHistory.VisibleHistory.FirstOrDefault() is { } newest) CommitHistory.SelectedCommit = newest;
    }

    private Task MutateAsync(string label, Func<CancellationToken, Task<string>> action, CancellationToken cancel, bool save = false, bool changesWorkspaceFiles = false) => RunAsync(label, async token =>
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
            if (version == _workspaceVersion) RepositoryChanged?.Invoke();
        }
    }, cancel);

    private Task RunAsync(string label, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        if (_switchingWorkspace || _disposed || _operations.TryEnter(cancellationToken) is not { } lease) return Task.CompletedTask;
        _activeTask = RunCoreAsync(label, action, lease);
        return _activeTask;
    }

    private async Task RunCoreAsync(string label, Func<CancellationToken, Task> action, OperationGate.Lease lease)
    {
        var version = _workspaceVersion;
        Error = "";
        Status = label + "…";
        try
        {
            await action(lease.Token);
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
        finally { lease.Dispose(); }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var version = _workspaceVersion;
        // Keep as many commits as the reader has paged in; one extra tells whether older ones exist.
        var historyLimit = CommitHistory.RefreshLimit;
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
        Quietly(() =>
        {
            Replace(Staged, snapshot.Changes.Where(x => x.IsStaged));
            Replace(Unstaged, snapshot.Changes.Where(x => x.IsUnstaged));
            Replace(Branches, snapshot.Branches);
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
        });
        ApplyCollaboration(snapshot);
        NotifyLists();
        NotifyCommands();
        // Git may have changed the file (stage, commit, pull), so reload its diff even when the selection is unchanged.
        LoadSelectedDiff().Forget();
        CommitHistory.ApplyRefresh(snapshot.History, historyLimit, rootChanged);
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
        CommitHistory.NotifyHeading();
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(ChangesPlaceholder));
    }

    private void NotifyCommands()
    {
        foreach (var command in new IRelayCommand[] { RefreshCommand, StageFileCommand, StageAllCommand, UnstageFileCommand,
            UnstageAllCommand, CommitCommand, FetchCommand, PullCommand, PushCommand, SwitchBranchCommand, CreateBranchCommand,
            ViewWorkingDiffCommand, ViewStagedDiffCommand, OpenFileCommand })
            command.NotifyCanExecuteChanged();
        CommitHistory.NotifyCommands();
        NotifyCollaborationCommands();
    }

    internal static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    public void Dispose()
    {
        _disposed = true;
        _operations.Cancel();
        _diffLoad?.Cancel();
        CommitHistory.CancelLoads();
        NotifyCommands();
    }

    bool IGitOperationHost.CanOperate => CanOperate();
    int IGitOperationHost.WorkspaceVersion => _workspaceVersion;
    void IGitOperationHost.ReportStatus(string status) => Status = status;
    void IGitOperationHost.ReportError(string error) => Error = error;
    Task IGitOperationHost.RunAsync(string label, Func<CancellationToken, Task> action, CancellationToken cancel) => RunAsync(label, action, cancel);
    Task IGitOperationHost.MutateAsync(string label, Func<CancellationToken, Task<string>> action, CancellationToken cancel, bool changesWorkspaceFiles) =>
        MutateAsync(label, action, cancel, changesWorkspaceFiles: changesWorkspaceFiles);
    Task<bool> IGitOperationHost.SaveOpenDocumentsAsync(CancellationToken cancellationToken) =>
        _saveBeforeOperation is null ? Task.FromResult(true) : _saveBeforeOperation(cancellationToken);
    void IGitOperationHost.OpenFile(string path) => OpenFileRequested?.Invoke(path);
    void IGitOperationHost.OpenUrl(string url) => Open(url);
    void IGitOperationHost.ShowHistoryTab() => SelectedTab = HistoryTab;
}
