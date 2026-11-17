using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Git;
using WpfStudio.Core.DevOps;

namespace WpfStudio.App.Features.DevOps;

public sealed record PullRequestListChoice(PullRequestList List, string Label);
public sealed record MergeStrategyChoice(MergeStrategy Strategy, string Label, string Description);

/// <summary>The Pull requests tab: the lists, the selected request's discussion, checks, files and commits, and what can be done to it.</summary>
public sealed partial class PullRequestsViewModel(IDevOpsHost host) : ObservableObject, IDisposable
{
    public const int OverviewTab = 0, FilesTab = 1, CommitsTab = 2;
    private const int ListSize = 100;
    private readonly LatestLoad _list = new(), _details = new(), _file = new();
    private int _loadedVersion = -1;
    private bool _quiet;
    private int? _pendingNumber;
    /// <summary>The request last opened from elsewhere (the Git pane, a link), kept in view while a list loads around it.</summary>
    private int? _shownNumber;

    public ObservableCollection<PullRequestListChoice> Lists { get; } = [];
    public ObservableCollection<DevOpsPullRequest> Items { get; } = [];
    public ObservableCollection<PullRequestThreadViewModel> Threads { get; } = [];
    public ObservableCollection<DevOpsPullRequestFile> Files { get; } = [];
    public ObservableCollection<MergeStrategyChoice> Strategies { get; } = [];

    [ObservableProperty] public partial PullRequestListChoice? SelectedList { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] public partial DevOpsPullRequest? Selected { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Current), nameof(ChecksSummary), nameof(HasChecks), nameof(HasWorkItems), nameof(MyVote), nameof(MyVoteText), nameof(IsAuthor), nameof(Reviewers))]
    public partial DevOpsPullRequestDetails? Details { get; private set; }
    [ObservableProperty] public partial DevOpsPullRequestFile? SelectedFile { get; set; }
    [ObservableProperty] public partial GitDiff? FileDiff { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool IsDetailLoading { get; private set; }
    [ObservableProperty] public partial bool IsFileLoading { get; private set; }
    [ObservableProperty] public partial int DetailTab { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddCommentCommand))] public partial string NewComment { get; set; } = "";
    [ObservableProperty] public partial MergeStrategyChoice? Strategy { get; set; }
    [ObservableProperty] public partial bool DeleteSourceBranch { get; set; } = true;
    [ObservableProperty] public partial bool CompleteWorkItems { get; set; } = true;
    [ObservableProperty] public partial bool IsCompleteOpen { get; set; }

    public bool HasSelection => Selected is not null;
    /// <summary>The selected request as most recently read: with its reviewers' votes once its details have loaded.</summary>
    public DevOpsPullRequest? Current => Details?.PullRequest ?? Selected;
    public IReadOnlyList<DevOpsReviewer> Reviewers => Current?.Reviewers ?? [];
    public DevOpsCapabilities? Capabilities => host.Provider?.Capabilities;
    public string CompleteVerb => Capabilities?.CompleteVerb ?? "Complete";
    public string AbandonVerb => Capabilities?.AbandonVerb ?? "Abandon";
    public string ReactivateVerb => Capabilities?.Votes == false ? "Reopen" : "Reactivate";
    public bool CanVoteOnService => Capabilities?.Votes == true;
    public bool CanResolveThreads => Capabilities?.ResolveThreads == true;
    internal bool CanReplyToThreads => host.CanAct && Current is not null;
    public bool HasAutoComplete => Capabilities?.AutoComplete == true;
    public bool HasCompleteWorkItems => Capabilities?.CompleteWorkItems == true;
    public bool HasChecks => Details?.Checks.Count > 0;
    public bool HasWorkItems => Details?.WorkItemIds.Count > 0;
    public bool IsAuthor => Current?.Author.IsSame(host.User) == true;
    public ReviewVote MyVote => Current?.Reviewers.FirstOrDefault(reviewer => reviewer.Identity.IsSame(host.User))?.Vote ?? ReviewVote.None;
    public string MyVoteText => MyVote == ReviewVote.None ? "" : "Your vote: " + DevOpsText.VoteLabel(MyVote);
    public string ChecksSummary => Details is not { Checks.Count: > 0 } details ? "No checks"
        : $"{details.Checks.Count(check => check.State == CheckState.Succeeded)} of {details.Checks.Count} checks passed"
          + (details.Checks.Any(check => check.State == CheckState.Failed) ? $" · {details.Checks.Count(check => check.State == CheckState.Failed)} failed" : "");
    public string EmptyText => IsLoading ? "Loading pull requests…" : SelectedList is null ? "" : $"No pull requests in “{SelectedList.Label}”.";
    public string AutoCompleteText => Current?.AutoComplete == true ? "Cancel auto-complete" : "Set auto-complete";

    partial void OnSelectedListChanged(PullRequestListChoice? value) { if (!_quiet) LoadList(); }
    partial void OnSelectedChanged(DevOpsPullRequest? value) { if (!_quiet) LoadDetails(); }
    partial void OnSelectedFileChanged(DevOpsPullRequestFile? value) { if (!_quiet) LoadFile(); }
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(EmptyText));
    partial void OnDetailsChanged(DevOpsPullRequestDetails? value)
    {
        OnPropertyChanged(nameof(AutoCompleteText));
        NotifyCommands();
    }

    internal void EnsureLoaded()
    {
        if (_loadedVersion == host.Version) return;
        _loadedVersion = host.Version;
        Quietly(() =>
        {
            GitViewModel.Replace(Lists, host.Provider?.Capabilities.Votes == false
                ? [new(PullRequestList.Active, "Open"), new(PullRequestList.Mine, "Created by me"), new(PullRequestList.ReviewRequested, "Review requested"),
                   new(PullRequestList.Completed, "Merged"), new(PullRequestList.Abandoned, "Closed")]
                : [new(PullRequestList.Active, "Active"), new(PullRequestList.Mine, "Created by me"), new(PullRequestList.ReviewRequested, "Assigned to me"),
                   new(PullRequestList.Completed, "Completed"), new(PullRequestList.Abandoned, "Abandoned")]);
            GitViewModel.Replace(Strategies, host.Provider?.Capabilities.Votes == false
                ? [new(MergeStrategy.Squash, "Squash and merge", "One commit with every change"), new(MergeStrategy.NoFastForward, "Create a merge commit", "Keeps every commit and adds a merge"),
                   new(MergeStrategy.Rebase, "Rebase and merge", "Replays each commit onto the target")]
                : [new(MergeStrategy.Squash, "Squash commit", "One commit with every change"), new(MergeStrategy.NoFastForward, "Merge (no fast-forward)", "Keeps every commit and adds a merge"),
                   new(MergeStrategy.Rebase, "Rebase and fast-forward", "Replays each commit onto the target"), new(MergeStrategy.RebaseMerge, "Semi-linear merge", "Rebases, then adds a merge commit")]);
            SelectedList = Lists.FirstOrDefault();
            Strategy = Strategies.FirstOrDefault(choice => choice.Strategy == host.Settings.MergeStrategy) ?? Strategies.FirstOrDefault();
            DeleteSourceBranch = host.Settings.DeleteSourceBranch;
            CompleteWorkItems = host.Settings.CompleteWorkItems;
        });
        foreach (var name in new[] { nameof(CompleteVerb), nameof(AbandonVerb), nameof(ReactivateVerb), nameof(CanVoteOnService), nameof(CanResolveThreads), nameof(HasAutoComplete), nameof(HasCompleteWorkItems), nameof(Capabilities) })
            OnPropertyChanged(name);
        LoadList();
    }

    internal void Reload()
    {
        if (_loadedVersion != host.Version) EnsureLoaded();
        else LoadList();
    }

    internal void Reset()
    {
        _list.Cancel();
        _details.Cancel();
        _file.Cancel();
        _loadedVersion = -1;
        _pendingNumber = null;
        _shownNumber = null;
        Quietly(() =>
        {
            Items.Clear();
            Selected = null;
            Lists.Clear();
            SelectedList = null;
        });
        ClearDetails();
    }

    /// <summary>Selects a pull request by number, reading it when no loaded list holds it.</summary>
    internal void Show(int number)
    {
        if (_loadedVersion != host.Version) EnsureLoaded();
        if (Items.FirstOrDefault(item => item.Number == number) is { } listed) { Selected = listed; return; }
        if (host.Provider is not { } provider) return;
        _pendingNumber = number;
        _shownNumber = number;
        _details.Start(async token =>
        {
            var pullRequest = await provider.GetPullRequestAsync(number, token);
            token.ThrowIfCancellationRequested();
            _pendingNumber = null;
            Quietly(() =>
            {
                if (Items.FirstOrDefault(item => item.Number == number) is { } existing) Items[Items.IndexOf(existing)] = pullRequest;
                else Items.Insert(0, pullRequest);
                Selected = pullRequest;
            });
            await LoadDetailsCoreAsync(provider, pullRequest, token);
        }, host.ReportError, loading => IsDetailLoading = loading);
    }

    internal Task WhenLoadedAsync() => Task.WhenAll(_list.Task, _details.Task, _file.Task);

    private void LoadList()
    {
        if (host.Provider is not { } provider || host.User is not { } me || SelectedList is not { } choice) return;
        _list.Start(async token =>
        {
            var items = await provider.ListPullRequestsAsync(choice.List, me, ListSize, token);
            token.ThrowIfCancellationRequested();
            var selected = Selected;
            Quietly(() =>
            {
                GitViewModel.Replace(Items, items);
                // A request opened from elsewhere stays in view while its list loads around it.
                if (selected is not null && selected.Number == _shownNumber && !Items.Any(item => item.Number == selected.Number))
                    Items.Insert(0, selected);
                Selected = selected is null ? null : Items.FirstOrDefault(item => item.Number == selected.Number);
            });
            if (Selected is not null) LoadDetails();
            else if (_pendingNumber is null && Items.Count > 0) Selected = Items[0];
            else if (_pendingNumber is null)
            {
                _details.Cancel();
                _file.Cancel();
                ClearDetails();
            }
        }, host.ReportError, loading => IsLoading = loading);
    }

    private void LoadDetails()
    {
        _file.Cancel();
        if (host.Provider is not { } provider || Selected is not { } pullRequest)
        {
            _details.Cancel();
            ClearDetails();
            return;
        }
        // Keep the reader's place in the file list when the same request is read again after an action.
        var sameRequest = Details?.PullRequest.Number == pullRequest.Number;
        if (!sameRequest) ClearDetails();
        _details.Start(token => LoadDetailsCoreAsync(provider, pullRequest, token), host.ReportError, loading => IsDetailLoading = loading);
    }

    private async Task LoadDetailsCoreAsync(IDevOpsProvider provider, DevOpsPullRequest pullRequest, CancellationToken token)
    {
        var detailsTask = provider.GetPullRequestDetailsAsync(pullRequest, token);
        var filesTask = provider.GetPullRequestFilesAsync(pullRequest, token);
        await Task.WhenAll(detailsTask, filesTask);
        token.ThrowIfCancellationRequested();
        var details = await detailsTask;
        var files = await filesTask;
        var selectedPath = SelectedFile?.Path;
        Details = details;
        GitViewModel.Replace(Threads, details.Threads.Where(thread => !thread.IsSystem).Select(thread => new PullRequestThreadViewModel(thread, this)));
        Quietly(() =>
        {
            GitViewModel.Replace(Files, files);
            SelectedFile = Files.FirstOrDefault(file => file.Path == selectedPath) ?? Files.FirstOrDefault();
        });
        // The list entry shows the votes the details brought.
        if (Items.FirstOrDefault(item => item.Number == details.PullRequest.Number) is { } listed && !ReferenceEquals(listed, details.PullRequest))
            Quietly(() =>
            {
                var index = Items.IndexOf(listed);
                var wasSelected = ReferenceEquals(Selected, listed);
                Items[index] = details.PullRequest;
                if (wasSelected) Selected = details.PullRequest;
            });
        LoadFile();
    }

    private void LoadFile()
    {
        if (host.Provider is not { } provider || Current is not { } pullRequest || SelectedFile is not { } file)
        {
            _file.Cancel();
            FileDiff = null;
            return;
        }
        _file.Start(async token =>
        {
            var versions = await provider.GetPullRequestFileVersionsAsync(pullRequest, file, token);
            token.ThrowIfCancellationRequested();
            FileDiff = new($"!{pullRequest.Number} · {file.Path}", file.Path, "", versions.Before, versions.After,
                file.Change == FileChangeKind.Added ? "Not in " + pullRequest.TargetBranch : pullRequest.TargetBranch,
                file.Change == FileChangeKind.Deleted ? "Deleted in " + pullRequest.SourceBranch : pullRequest.SourceBranch, versions.Message);
        }, ex => FileDiff = new(file.Path, file.Path, "", Message: GitDiagnosticSanitizer.Redact(ex.Message)), loading => IsFileLoading = loading);
    }

    private void ClearDetails()
    {
        Details = null;
        Threads.Clear();
        Quietly(() =>
        {
            Files.Clear();
            SelectedFile = null;
        });
        FileDiff = null;
        NewComment = "";
        IsCompleteOpen = false;
    }

    // ------------------------------------------------------------------ Actions

    private bool CanUse() => host.CanAct && Current is not null;
    private bool CanUseActive() => CanUse() && Current!.IsActive;
    private bool CanVote() => CanUseActive();
    private bool CanComplete() => CanUseActive() && !Current!.IsDraft;
    /// <summary>For the Complete button that opens the merge options.</summary>
    public bool CanCompleteNow => CanComplete();
    private bool CanReactivate() => CanUse() && Current!.Status == PullRequestStatus.Abandoned;
    private bool CanPublish() => CanUseActive() && Current!.IsDraft;
    private bool CanMarkDraft() => CanUseActive() && !Current!.IsDraft;
    private bool CanAutoComplete() => CanComplete() && HasAutoComplete;
    private bool CanAddComment() => CanUse() && !string.IsNullOrWhiteSpace(NewComment);
    private bool CanCheckOut() => CanUseActive() && host.Git.HasRepository && !host.Git.IsBusy;

    [RelayCommand(CanExecute = nameof(CanVote))]
    private async Task VoteAsync(ReviewVote vote)
    {
        if (Current is not { } pullRequest) return;
        string? comment = null;
        if (!CanVoteOnService && vote is ReviewVote.ChangesRequested or ReviewVote.Rejected or ReviewVote.WaitingForAuthor)
        {
            comment = await host.PromptAsync("Request changes", $"Say what needs to change in !{pullRequest.Number}. GitHub posts it with your review.");
            if (string.IsNullOrWhiteSpace(comment)) return;
            vote = ReviewVote.ChangesRequested;
        }
        await host.RunAsync($"{DevOpsText.VoteLabel(vote)} !{pullRequest.Number}", async (provider, me, token) =>
        {
            await provider.VoteAsync(pullRequest, me, vote, comment, token);
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });
    }

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task CompleteAsync()
    {
        if (Current is not { } pullRequest || Strategy is not { } strategy) return;
        IsCompleteOpen = false;
        var failing = Details?.Checks.Count(check => check.State == CheckState.Failed && check.IsRequired) ?? 0;
        var question = $"{CompleteVerb} !{pullRequest.Number} “{pullRequest.Title}” into {pullRequest.TargetBranch} with “{strategy.Label}”"
            + (DeleteSourceBranch ? $", then delete {pullRequest.SourceBranch}?" : "?")
            + (failing > 0 ? $"\n\n{failing} required check{(failing == 1 ? " has" : "s have")} failed; {Capabilities?.ServiceName} may refuse." : "");
        if (!await host.ConfirmAsync($"{CompleteVerb} pull request", question)) return;
        var completion = new PullRequestCompletion(strategy.Strategy, DeleteSourceBranch, HasCompleteWorkItems && CompleteWorkItems);
        await host.RunAsync($"{CompleteVerb} !{pullRequest.Number}", async (provider, _, token) =>
        {
            var done = await provider.CompleteAsync(pullRequest, completion, token);
            Replace(done);
            host.ReportStatus(done.Status == PullRequestStatus.Completed
                ? $"!{done.Number} {(done.IsMerged ? "merged" : "completed")} into {done.TargetBranch}. Pull {done.TargetBranch} in the Git pane to get it."
                : $"!{done.Number} is being completed. Refresh to see when it finishes.");
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });
    }

    [RelayCommand(CanExecute = nameof(CanAutoComplete))]
    private Task ToggleAutoCompleteAsync()
    {
        if (Current is not { } pullRequest || Strategy is not { } strategy) return Task.CompletedTask;
        var enable = !pullRequest.AutoComplete;
        var completion = new PullRequestCompletion(strategy.Strategy, DeleteSourceBranch, CompleteWorkItems);
        return host.RunAsync(enable ? $"Set auto-complete on !{pullRequest.Number}" : $"Cancel auto-complete on !{pullRequest.Number}", async (provider, me, token) =>
        {
            Replace(await provider.SetAutoCompleteAsync(pullRequest, me, enable, completion, token));
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseActive))]
    private async Task AbandonAsync()
    {
        if (Current is not { } pullRequest) return;
        if (!await host.ConfirmAsync($"{AbandonVerb} pull request", $"{AbandonVerb} !{pullRequest.Number} “{pullRequest.Title}”? It can be {ReactivateVerb.ToLowerInvariant()}d later.")) return;
        await host.RunAsync($"{AbandonVerb} !{pullRequest.Number}", async (provider, _, token) =>
        {
            Replace(await provider.SetStatusAsync(pullRequest, PullRequestStatus.Abandoned, token));
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });
    }

    [RelayCommand(CanExecute = nameof(CanReactivate))]
    private Task ReactivateAsync() => Current is not { } pullRequest ? Task.CompletedTask :
        host.RunAsync($"{ReactivateVerb} !{pullRequest.Number}", async (provider, _, token) =>
        {
            Replace(await provider.SetStatusAsync(pullRequest, PullRequestStatus.Active, token));
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });

    [RelayCommand(CanExecute = nameof(CanPublish))]
    private Task PublishAsync() => SetDraftAsync(false);

    [RelayCommand(CanExecute = nameof(CanMarkDraft))]
    private Task MarkDraftAsync() => SetDraftAsync(true);

    private Task SetDraftAsync(bool draft) => Current is not { } pullRequest ? Task.CompletedTask :
        host.RunAsync(draft ? $"Mark !{pullRequest.Number} as a draft" : $"Publish !{pullRequest.Number}", async (provider, _, token) =>
        {
            Replace(await provider.SetDraftAsync(pullRequest, draft, token));
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task EditAsync()
    {
        if (Current is not { } pullRequest) return;
        var title = await host.PromptAsync("Rename pull request", $"A new title for !{pullRequest.Number}.", pullRequest.Title);
        if (string.IsNullOrWhiteSpace(title) || title == pullRequest.Title) return;
        await host.RunAsync($"Rename !{pullRequest.Number}", async (provider, _, token) =>
        {
            Replace(await provider.UpdatePullRequestAsync(pullRequest, title.Trim(), pullRequest.Description, token));
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });
    }

    [RelayCommand(CanExecute = nameof(CanAddComment))]
    private Task AddCommentAsync()
    {
        if (Current is not { } pullRequest) return Task.CompletedTask;
        var text = NewComment.Trim();
        return host.RunAsync($"Comment on !{pullRequest.Number}", async (provider, _, token) =>
        {
            await provider.AddCommentAsync(pullRequest, text, null, token);
            NewComment = "";
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });
    }

    internal Task ReplyAsync(PullRequestThreadViewModel thread)
    {
        if (Current is not { } pullRequest) return Task.CompletedTask;
        var text = thread.ReplyText.Trim();
        return host.RunAsync($"Reply on !{pullRequest.Number}", async (provider, _, token) =>
        {
            await provider.AddCommentAsync(pullRequest, text, thread.Thread, token);
            thread.ReplyText = "";
            thread.IsReplying = false;
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });
    }

    internal Task SetThreadStatusAsync(PullRequestThreadViewModel thread, ThreadStatus status)
    {
        if (Current is not { } pullRequest) return Task.CompletedTask;
        return host.RunAsync(status == ThreadStatus.Resolved ? "Resolve comment" : "Reactivate comment", async (provider, _, token) =>
        {
            await provider.SetThreadStatusAsync(pullRequest, thread.Thread, status, token);
            await RefreshSelectedAsync(provider, pullRequest.Number, token);
        });
    }

    /// <summary>Switches the working tree to the request's source branch through the Git pane, so its files can be built and run.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckOut))]
    private async Task CheckOutAsync()
    {
        if (Current is not { } pullRequest) return;
        var git = host.Git;
        var remote = git.RemoteName ?? "origin";
        var github = host.Provider?.Target.Kind == DevOpsProviderKind.GitHub;
        // A fork's branch is not on this remote; GitHub keeps every request's head under refs/pull, under a local name of its own.
        var (pullRef, local) = pullRequest.IsFromFork && github ? ($"refs/pull/{pullRequest.Number}/head", $"pr/{pullRequest.Number}") : ((string?)null, pullRequest.SourceBranch);
        host.ReportStatus($"Checking out {local}…");
        var done = await git.RunToolActionAsync($"Check out pull request !{pullRequest.Number}",
            (root, signIn, token) => host.GitService.CheckoutPullRequestAsync(root, remote, pullRequest.SourceBranch, pullRef, local, token, signIn),
            changesWorkspaceFiles: true, network: true);
        host.ReportStatus(done ? $"Checked out {local} for !{pullRequest.Number}"
            : git.HasError ? $"Git could not check out {local}: {git.Error}" : "Git is busy. Try again when the Git pane's action finishes.");
    }

    [RelayCommand]
    private void OpenInBrowser(DevOpsPullRequest? pullRequest)
    {
        if ((pullRequest ?? Current) is { } target) host.OpenUrl(target.WebUrl);
    }

    [RelayCommand]
    private void CopyLink(DevOpsPullRequest? pullRequest)
    {
        if ((pullRequest ?? Current) is { } target) host.Copy(target.WebUrl, $"the link to !{target.Number}");
    }

    [RelayCommand]
    private void OpenWorkItem(int id) => host.ShowWorkItem(id);

    [RelayCommand]
    private void OpenCheck(DevOpsCheck? check)
    {
        if (check?.Url is { } url) host.OpenUrl(url);
    }

    /// <summary>Opens the working tree's copy of a changed file in the editor.</summary>
    [RelayCommand]
    private void OpenFile(DevOpsPullRequestFile? file)
    {
        if ((file ?? SelectedFile) is { } target && host.Git.RepositoryRoot is { } root)
            host.OpenFile(Path.GetFullPath(Path.Combine(root, target.Path)));
    }

    [RelayCommand]
    private void StartCreate() => host.StartCreatePullRequest();

    /// <summary>Reads the request again after a change, then its details, keeping it selected.</summary>
    private async Task RefreshSelectedAsync(IDevOpsProvider provider, int number, CancellationToken token)
    {
        var fresh = await provider.GetPullRequestAsync(number, token);
        Replace(fresh);
    }

    /// <summary>Puts a newer read of a request into the list and, when it is selected, reads its details again.</summary>
    private void Replace(DevOpsPullRequest fresh)
    {
        var listed = Items.FirstOrDefault(item => item.Number == fresh.Number);
        var selected = Selected?.Number == fresh.Number;
        Quietly(() =>
        {
            if (listed is not null) Items[Items.IndexOf(listed)] = fresh;
            if (selected) Selected = fresh;
        });
        if (selected) LoadDetails();
    }

    internal void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanCompleteNow));
        foreach (var command in new IRelayCommand[] { VoteCommand, CompleteCommand, ToggleAutoCompleteCommand, AbandonCommand, ReactivateCommand,
            PublishCommand, MarkDraftCommand, EditCommand, AddCommentCommand, CheckOutCommand })
            command.NotifyCanExecuteChanged();
        foreach (var thread in Threads) thread.NotifyCommands();
    }

    partial void OnSelectedChanging(DevOpsPullRequest? value) => IsCompleteOpen = false;

    private void Quietly(Action action)
    {
        var previous = _quiet;
        _quiet = true;
        try { action(); }
        finally { _quiet = previous; }
    }

    public void Dispose()
    {
        _list.Cancel();
        _details.Cancel();
        _file.Cancel();
    }
}

/// <summary>One discussion of a pull request, with its reply box and resolve actions.</summary>
public sealed partial class PullRequestThreadViewModel(DevOpsThread thread, PullRequestsViewModel owner) : ObservableObject
{
    public DevOpsThread Thread { get; } = thread;
    public DevOpsComment First => Thread.Comments[0];
    public IReadOnlyList<DevOpsComment> Replies => Thread.Comments.Skip(1).ToList();
    public bool HasReplies => Thread.Comments.Count > 1;
    public bool HasLocation => Thread.Location.Length > 0;
    public bool CanResolveHere => owner.CanResolveThreads && Thread.CanResolve;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ReplyCommand))] public partial string ReplyText { get; set; } = "";
    [ObservableProperty] public partial bool IsReplying { get; set; }

    private bool CanReply() => owner.CanReplyToThreads && !string.IsNullOrWhiteSpace(ReplyText);
    private bool CanResolve() => owner.CanReplyToThreads && CanResolveHere && !Thread.IsResolved;
    private bool CanReactivate() => owner.CanReplyToThreads && CanResolveHere && Thread.IsResolved;

    [RelayCommand]
    private void StartReply() => IsReplying = !IsReplying;

    [RelayCommand(CanExecute = nameof(CanReply))]
    private Task ReplyAsync() => owner.ReplyAsync(this);

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private Task ResolveAsync() => owner.SetThreadStatusAsync(this, ThreadStatus.Resolved);

    [RelayCommand(CanExecute = nameof(CanReactivate))]
    private Task ReactivateAsync() => owner.SetThreadStatusAsync(this, ThreadStatus.Active);

    internal void NotifyCommands()
    {
        ReplyCommand.NotifyCanExecuteChanged();
        ResolveCommand.NotifyCanExecuteChanged();
        ReactivateCommand.NotifyCanExecuteChanged();
    }
}
