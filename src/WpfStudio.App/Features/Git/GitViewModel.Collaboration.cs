using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Features.Git;

/// <summary>One conflict in the resolver, with the side the user chose for it.</summary>
public sealed partial class ConflictItem(int number, ConflictBlock block, Action changed) : ObservableObject
{
    public int Number { get; } = number;
    public ConflictBlock Block { get; } = block;
    public string Heading => $"Conflict {Number}";
    public string CurrentLabel => "Current" + (Block.CurrentLabel.Length > 0 ? " · " + Block.CurrentLabel : "");
    public string IncomingLabel => "Incoming" + (Block.IncomingLabel.Length > 0 ? " · " + Block.IncomingLabel : "");
    public string Current => Block.Current.Length == 0 ? "(nothing)" : Block.Current.TrimEnd('\r', '\n');
    public string Incoming => Block.Incoming.Length == 0 ? "(nothing)" : Block.Incoming.TrimEnd('\r', '\n');
    public bool HasBase => Block.Base is not null;
    [ObservableProperty] public partial ConflictChoice Choice { get; set; }
    public string ChoiceText => Choice switch
    {
        ConflictChoice.Current => "Keeps current", ConflictChoice.Incoming => "Keeps incoming",
        ConflictChoice.CurrentThenIncoming => "Keeps both, current first", ConflictChoice.IncomingThenCurrent => "Keeps both, incoming first",
        ConflictChoice.Base => "Keeps the common ancestor", _ => "Not resolved"
    };
    public bool IsResolved => Choice != ConflictChoice.Unresolved;
    partial void OnChoiceChanged(ConflictChoice value) { OnPropertyChanged(nameof(ChoiceText)); OnPropertyChanged(nameof(IsResolved)); changed(); }
    [RelayCommand] private void Choose(ConflictChoice choice) => Choice = choice;
}

/// <summary>Remote hosting links, sign-in, merging and the conflict resolver.</summary>
public sealed partial class GitViewModel
{
    private Func<CancellationToken, Task<string>>? _retryWithSignIn;
    private string _retryLabel = "";

    /// <summary>Raised after any successful Git action, so editors can refresh their change margins and blame.</summary>
    public event Action? RepositoryChanged;
    public ObservableCollection<ConflictItem> Conflicts { get; } = [];
    [ObservableProperty] public partial GitRemote? Remote { get; set; }
    [ObservableProperty] public partial GitOperation Operation { get; set; }
    [ObservableProperty] public partial bool CanSignIn { get; set; }
    [ObservableProperty] public partial GitConflictFile? ConflictFile { get; set; }
    [ObservableProperty] public partial string ResolvedPreview { get; set; } = "";
    [ObservableProperty] public partial string? MergeSource { get; set; }

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
    public bool IsResolving => ConflictFile is not null;
    public string ResolverHeading => ConflictFile is null ? "" : $"{ConflictFile.RelativePath} · {Conflicts.Count(c => c.IsResolved)} of {Conflicts.Count} resolved";
    public bool AllConflictsChosen => Conflicts.Count > 0 && Conflicts.All(c => c.IsResolved);

    partial void OnRemoteChanged(GitRemote? value) { OnPropertyChanged(nameof(HasRemote)); OnPropertyChanged(nameof(RemoteText)); OnPropertyChanged(nameof(PullRequestText)); NotifyCollaborationCommands(); }
    partial void OnOperationChanged(GitOperation value) { OnPropertyChanged(nameof(HasOperation)); OnPropertyChanged(nameof(OperationText)); NotifyCollaborationCommands(); }
    partial void OnConflictFileChanged(GitConflictFile? value) { OnPropertyChanged(nameof(IsResolving)); OnPropertyChanged(nameof(ResolverHeading)); }
    partial void OnMergeSourceChanged(string? value) => MergeBranchCommand.NotifyCanExecuteChanged();

    private void ApplyCollaboration(GitSnapshot snapshot)
    {
        Remote = snapshot.Remote;
        Operation = snapshot.Operation;
        OnPropertyChanged(nameof(ConflictCount)); OnPropertyChanged(nameof(OperationText));
        if (ConflictFile is { } open && !Staged.Concat(Unstaged).Any(change => change.IsConflict && change.Path == open.RelativePath)) CloseResolver();
    }

    private void NotifyCollaborationCommands()
    {
        foreach (var command in new IRelayCommand[] { MergeBranchCommand, AbortOperationCommand, ContinueOperationCommand, OpenRemoteCommand,
            CreatePullRequestCommand, OpenCommitOnRemoteCommand, SignInAndRetryCommand, ResolveConflictsCommand, ApplyResolutionCommand })
            command.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ Remote links
    [RelayCommand(CanExecute = nameof(HasRemote))]
    private void OpenRemote() => Open(Remote!.BranchUrl(Branch));
    private bool CanCreatePullRequest() => HasRemote && HasRepository && Branch is not ("Detached HEAD" or "No repository" or "Loading repository…");
    [RelayCommand(CanExecute = nameof(CanCreatePullRequest))]
    private void CreatePullRequest() => Open(Remote!.NewPullRequestUrl(Branch));
    private bool CanOpenCommitOnRemote() => HasRemote && CommitDetails is not null;
    [RelayCommand(CanExecute = nameof(CanOpenCommitOnRemote))]
    private void OpenCommitOnRemote() => Open(Remote!.CommitUrl(CommitDetails!.Id));
    partial void OnCommitDetailsChanged(GitCommitDetails? value) => OpenCommitOnRemoteCommand.NotifyCanExecuteChanged();
    private void Open(string url)
    {
        try { DesktopNavigation.OpenUrl(url); AppendOutput("Opened " + url); }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>
    /// Shows a commit from editor blame in History, loading its details even when it is not in the loaded page.
    /// With <paramref name="relativePath"/>, that file's diff is selected instead of the commit's first file.
    /// </summary>
    public void ShowCommit(BlameLine line, string? relativePath = null)
    {
        if (!HasRepository || line.IsUncommitted) return;
        _preferredCommitFile = relativePath?.Replace('\\', '/');
        SelectedTab = HistoryTab;
        SelectedCommit = VisibleHistory.FirstOrDefault(commit => commit.Id == line.Commit)
            ?? new GitCommit(line.Commit, line.ShortCommit, line.Author, line.Time.ToString("O"), line.Summary);
        // Already showing this commit: its details do not load again, so switch to the requested file here.
        if (CommitDetails?.Id == line.Commit && TakePreferredFile(CommitDetails.Files) is { } file) SelectedCommitFile = file;
    }

    // ------------------------------------------------------------------ Sign-in
    /// <summary>
    /// Runs a network action with stored credentials. When they are missing or rejected, offers Sign in and retry,
    /// which lets Git Credential Manager show its window (Microsoft Entra for Azure DevOps, a browser for GitHub).
    /// </summary>
    private Task NetworkAsync(string label, Func<bool, CancellationToken, Task<string>> action, bool save = false, bool changesWorkspaceFiles = false)
    {
        CanSignIn = false;
        return MutateAsync(label, async token =>
        {
            try { return await action(false, token); }
            catch (InvalidOperationException ex) when (GitService.IsAuthenticationFailure(ex.Message))
            {
                _retryLabel = label;
                _retryWithSignIn = retryToken => action(true, retryToken);
                CanSignIn = true;
                throw new InvalidOperationException($"{(Remote?.HostName is { } host && Remote is not null ? host : "The remote")} needs you to sign in. Choose Sign in and retry. " + ex.Message);
            }
        }, save, changesWorkspaceFiles);
    }
    private bool CanRetryWithSignIn() => CanOperate() && CanSignIn && _retryWithSignIn is not null;
    [RelayCommand(CanExecute = nameof(CanRetryWithSignIn))]
    private Task SignInAndRetryAsync()
    {
        var retry = _retryWithSignIn!;
        _retryWithSignIn = null; CanSignIn = false;
        return MutateAsync(_retryLabel + " (signed in)", retry, save: _retryLabel.StartsWith("Pull", StringComparison.Ordinal), changesWorkspaceFiles: _retryLabel.StartsWith("Pull", StringComparison.Ordinal));
    }
    partial void OnCanSignInChanged(bool value) => SignInAndRetryCommand.NotifyCanExecuteChanged();

    // ------------------------------------------------------------------ Merge and conflicts
    private bool CanMerge() => CanOperate() && !HasOperation && !string.IsNullOrWhiteSpace(MergeSource) && MergeSource != Branch;
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private Task MergeBranchAsync() => MutateAsync($"Merge {MergeSource} into {Branch}", token => _service.MergeAsync(RepositoryRoot!, MergeSource!, token), save: true, changesWorkspaceFiles: true);
    private bool CanEndOperation() => CanOperate() && HasOperation;
    [RelayCommand(CanExecute = nameof(CanEndOperation))]
    private Task AbortOperationAsync() => MutateAsync("Abort " + Operation.ToString().ToLowerInvariant(), token => _service.AbortOperationAsync(RepositoryRoot!, token), save: true, changesWorkspaceFiles: true);
    [RelayCommand(CanExecute = nameof(CanEndOperation))]
    private Task ContinueOperationAsync() => MutateAsync("Continue " + Operation.ToString().ToLowerInvariant(), token => _service.ContinueOperationAsync(RepositoryRoot!, token), save: true, changesWorkspaceFiles: true);

    private bool CanResolve(GitChange? change) => CanOperate() && change is { IsConflict: true };
    [RelayCommand(CanExecute = nameof(CanResolve))]
    private Task ResolveConflictsAsync(GitChange? change) => RunAsync("Open conflicts", async token =>
    {
        if (_saveBeforeOperation is not null && !await _saveBeforeOperation(token))
            throw new InvalidOperationException("Save open documents before resolving conflicts.");
        var file = await _service.ReadConflictAsync(RepositoryRoot!, change!, token);
        if (file.Blocks.Count == 0)
            throw new InvalidOperationException("No conflict markers remain in this file. If it is correct, stage it to mark it resolved.");
        Conflicts.Clear();
        for (var index = 0; index < file.Blocks.Count; index++) Conflicts.Add(new(index + 1, file.Blocks[index], UpdateResolution));
        ConflictFile = file;
        UpdateResolution();
        Status = $"{file.Blocks.Count} conflict{(file.Blocks.Count == 1 ? "" : "s")} in {file.RelativePath}";
    });
    [RelayCommand] private void ChooseAll(ConflictChoice choice) { foreach (var conflict in Conflicts) conflict.Choice = choice; }
    private bool CanApplyResolution() => CanOperate() && ConflictFile is not null && AllConflictsChosen;
    [RelayCommand(CanExecute = nameof(CanApplyResolution))]
    private Task ApplyResolutionAsync()
    {
        var file = ConflictFile!;
        var resolved = MergeConflicts.Apply(file.Text, [.. Conflicts.Select(c => c.Block)], [.. Conflicts.Select(c => c.Choice)]);
        return MutateAsync($"Resolve {file.RelativePath}", async token =>
        {
            var result = await _service.ResolveConflictAsync(file, resolved, token);
            CloseResolver();
            return $"Resolved and staged {file.RelativePath}. " + result;
        }, changesWorkspaceFiles: true);
    }
    [RelayCommand] private void CloseResolver() { ConflictFile = null; Conflicts.Clear(); ResolvedPreview = ""; }

    private void UpdateResolution()
    {
        OnPropertyChanged(nameof(ResolverHeading)); OnPropertyChanged(nameof(AllConflictsChosen));
        ApplyResolutionCommand.NotifyCanExecuteChanged();
        if (ConflictFile is not { } file) { ResolvedPreview = ""; return; }
        // Unresolved blocks keep their markers in the preview so it shows what is left.
        var output = new System.Text.StringBuilder();
        var cursor = 0;
        foreach (var conflict in Conflicts)
        {
            output.Append(file.Text, cursor, conflict.Block.Start - cursor);
            output.Append(conflict.IsResolved ? conflict.Block.Resolve(conflict.Choice) : file.Text[conflict.Block.Start..conflict.Block.End]);
            cursor = conflict.Block.End;
        }
        ResolvedPreview = output.Append(file.Text, cursor, file.Text.Length - cursor).ToString();
    }
}
