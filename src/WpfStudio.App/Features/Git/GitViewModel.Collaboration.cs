using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Features.Git;

/// <summary>Remote hosting links, sign-in and merging.</summary>
public sealed partial class GitViewModel
{
    private sealed record SignInRetry(string Label, Func<CancellationToken, Task<string>> Action, bool Save, bool ChangesWorkspaceFiles);
    private SignInRetry? _signInRetry;

    /// <summary>Raised after any successful Git action, so editors can refresh their change margins and blame.</summary>
    public event Action? RepositoryChanged;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRemote), nameof(RemoteText), nameof(PullRequestText))] public partial GitRemote? Remote { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOperation), nameof(OperationText))] public partial GitOperation Operation { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignInAndRetryCommand))] public partial bool CanSignIn { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(MergeBranchCommand))] public partial string? MergeSource { get; set; }

    public bool HasRemote => Remote is not null;
    public string RemoteText => Remote is null ? "No web remote" : $"Open on {Remote.HostName}";
    public string PullRequestText => Remote?.Host == GitHost.GitLab ? "Create merge request" : "Create pull request";
    public bool HasOperation => Operation != GitOperation.None;
    public string OperationText => Operation switch
    {
        GitOperation.Merge => "Merge in progress", GitOperation.Rebase => "Rebase in progress",
        GitOperation.CherryPick => "Cherry-pick in progress", GitOperation.Revert => "Revert in progress", _ => ""
    } + (ConflictCount > 0 ? $" · {ConflictCount} conflicted file{(ConflictCount == 1 ? "" : "s")}" : " · all conflicts resolved");
    public int ConflictCount => Staged.Concat(Unstaged).Where(change => change.IsConflict).Select(change => change.Path).Distinct().Count();

    partial void OnRemoteChanged(GitRemote? value) => NotifyCollaborationCommands();
    partial void OnOperationChanged(GitOperation value) => NotifyCollaborationCommands();

    private void ApplyCollaboration(GitSnapshot snapshot)
    {
        Remote = snapshot.Remote;
        Operation = snapshot.Operation;
        OnPropertyChanged(nameof(ConflictCount)); OnPropertyChanged(nameof(OperationText));
        Resolver.CloseIfResolved(Staged.Concat(Unstaged));
    }

    private void NotifyCollaborationCommands()
    {
        foreach (var command in new IRelayCommand[] { MergeBranchCommand, AbortOperationCommand, ContinueOperationCommand, OpenRemoteCommand,
            CreatePullRequestCommand, CommitHistory.OpenCommitOnRemoteCommand, SignInAndRetryCommand })
            command.NotifyCanExecuteChanged();
        Resolver.NotifyCommands();
    }

    // ------------------------------------------------------------------ Remote links
    [RelayCommand(CanExecute = nameof(HasRemote))]
    private void OpenRemote() => Open(Remote!.BranchUrl(Branch));
    private bool CanCreatePullRequest() => HasRemote && HasRepository && Branch is not ("Detached HEAD" or "No repository" or "Loading repository…");
    [RelayCommand(CanExecute = nameof(CanCreatePullRequest))]
    private void CreatePullRequest()
    {
        // The DevOps workbench creates it in WpfStudio when it serves this remote; the web page is the fallback.
        if (CreatePullRequestInWorkbench?.Invoke() == true) return;
        Open(Remote!.NewPullRequestUrl(Branch));
    }

    /// <summary>Set by the DevOps workbench: starts creating a pull request in WpfStudio, and returns false when it cannot for this remote.</summary>
    public Func<bool>? CreatePullRequestInWorkbench { get; set; }

    // ------------------------------------------------------------------ The current branch's pull request
    /// <summary>The active pull request from the current branch as the DevOps workbench last found it, such as <c>!42 · 1 approval</c>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBranchPullRequest))] public partial string? BranchPullRequestText { get; set; }
    public bool HasBranchPullRequest => BranchPullRequestText is not null;
    /// <summary>Raised to show the current branch's pull request in the DevOps workbench.</summary>
    public event Action? BranchPullRequestRequested;
    [RelayCommand]
    private void OpenBranchPullRequest() => BranchPullRequestRequested?.Invoke();

    /// <summary>
    /// Runs a Git action another tool asked for (the DevOps workbench checks out a pull request or publishes a branch) as this pane's
    /// one Git action: nothing happens while another runs, output and failures go to this pane, open documents are saved first when
    /// files can change, and the repository is refreshed afterwards. A network action can offer Sign in and retry here. True when the
    /// action completed.
    /// </summary>
    /// <param name="action">Receives the repository root and whether Git Credential Manager may show its sign-in window.</param>
    public async Task<bool> RunToolActionAsync(string label, Func<string, bool, CancellationToken, Task<string>> action, bool changesWorkspaceFiles, bool network,
        CancellationToken cancellationToken = default)
    {
        if (RepositoryRoot is not { } root || !CanOperate()) return false;
        var completed = false;
        async Task<string> Run(bool signIn, CancellationToken token)
        {
            var output = await action(root, signIn, token);
            completed = true;
            return output;
        }
        if (network) await NetworkAsync(label, Run, cancellationToken, save: changesWorkspaceFiles, changesWorkspaceFiles);
        else await MutateAsync(label, token => Run(false, token), cancellationToken, save: changesWorkspaceFiles, changesWorkspaceFiles);
        return completed;
    }
    private void Open(string url)
    {
        try { _desktop.OpenUrl(url); AppendOutput("Opened " + url); }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>
    /// Shows a commit from editor blame in History, loading its details even when it is not in the loaded page.
    /// With <paramref name="relativePath"/>, that file's diff is selected instead of the commit's first file.
    /// </summary>
    public void ShowCommit(BlameLine line, string? relativePath = null) => CommitHistory.ShowCommit(line, relativePath);

    // ------------------------------------------------------------------ Sign-in
    /// <summary>
    /// Runs a network action with stored credentials. When they are missing or rejected, offers Sign in and retry,
    /// which lets Git Credential Manager show its window (Microsoft Entra for Azure DevOps, a browser for GitHub).
    /// </summary>
    private Task NetworkAsync(string label, Func<bool, CancellationToken, Task<string>> action, CancellationToken cancel, bool save = false, bool changesWorkspaceFiles = false)
    {
        CanSignIn = false;
        return MutateAsync(label, async token =>
        {
            try { return await action(false, token); }
            catch (InvalidOperationException ex) when (GitOutput.IsAuthenticationFailure(ex.Message))
            {
                _signInRetry = new(label, retryToken => action(true, retryToken), save, changesWorkspaceFiles);
                CanSignIn = true;
                throw new InvalidOperationException($"{(Remote?.HostName is { } host && Remote is not null ? host : "The remote")} needs you to sign in. Choose Sign in and retry. " + ex.Message);
            }
        }, cancel, save, changesWorkspaceFiles);
    }
    private bool CanRetryWithSignIn() => CanOperate() && CanSignIn && _signInRetry is not null;
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRetryWithSignIn))]
    private Task SignInAndRetryAsync(CancellationToken cancel)
    {
        var retry = _signInRetry!;
        _signInRetry = null; CanSignIn = false;
        return MutateAsync(retry.Label + " (signed in)", retry.Action, cancel, retry.Save, retry.ChangesWorkspaceFiles);
    }

    // ------------------------------------------------------------------ Merge
    private bool CanMerge() => CanOperate() && !HasOperation && !string.IsNullOrWhiteSpace(MergeSource) && MergeSource != Branch;
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanMerge))]
    private Task MergeBranchAsync(CancellationToken cancel)
    {
        IsBranchPickerOpen = false;
        return MutateAsync($"Merge {MergeSource} into {Branch}", token => _service.MergeAsync(RepositoryRoot!, MergeSource!, token), cancel, save: true, changesWorkspaceFiles: true);
    }
    private bool CanEndOperation() => CanOperate() && HasOperation;
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanEndOperation))]
    private Task AbortOperationAsync(CancellationToken cancel) => MutateAsync("Abort " + Operation.ToString().ToLowerInvariant(), token => _service.AbortOperationAsync(RepositoryRoot!, token), cancel, save: true, changesWorkspaceFiles: true);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanEndOperation))]
    private Task ContinueOperationAsync(CancellationToken cancel) => MutateAsync("Continue " + Operation.ToString().ToLowerInvariant(), token => _service.ContinueOperationAsync(RepositoryRoot!, token), cancel, save: true, changesWorkspaceFiles: true);
}
