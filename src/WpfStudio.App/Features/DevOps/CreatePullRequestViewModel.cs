using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Git;
using WpfStudio.Core.DevOps;

namespace WpfStudio.App.Features.DevOps;

/// <summary>
/// Creates a pull request from the Git pane's current branch. The form starts filled in: the repository's default branch as the
/// target, a title and description from the branch's commits, and the work items its name and commits mention. A branch the remote
/// does not have yet, or that has commits it lacks, is published first through the Git pane.
/// </summary>
public sealed partial class CreatePullRequestViewModel(IDevOpsHost host) : ObservableObject, IDisposable
{
    private readonly LatestLoad _prefill = new();

    public ObservableCollection<string> TargetBranches { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateCommand))] public partial bool IsOpen { get; private set; }
    [ObservableProperty] public partial string SourceBranch { get; private set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateCommand))] public partial string? TargetBranch { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateCommand))] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial bool IsDraft { get; set; }
    [ObservableProperty] public partial bool AutoComplete { get; set; }
    [ObservableProperty] public partial string WorkItems { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateCommand))] public partial bool IsLoading { get; private set; }
    /// <summary>An active request from this branch already exists; creating another would fail.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasExisting)), NotifyCanExecuteChangedFor(nameof(CreateCommand))] public partial DevOpsPullRequest? Existing { get; private set; }

    public bool HasExisting => Existing is not null;
    public bool HasAutoComplete => host.Provider?.Capabilities.AutoComplete == true;
    public string WorkItemsLabel => host.Provider?.Capabilities.WorkItemsName ?? "Work items";
    /// <summary>The branch has no upstream, or commits its upstream lacks: the service cannot see them yet.</summary>
    public bool NeedsPublish => DevOpsViewModel.IsBranchName(host.Git.Branch) && (host.Git.Upstream is null || host.Git.AheadCount > 0);
    public string PublishText => host.Git.Upstream is null
        ? $"{SourceBranch} is not on the remote yet. Publish it so the pull request can be created."
        : $"{host.Git.AheadCount} commit{(host.Git.AheadCount == 1 ? " is" : "s are")} not pushed yet. Push to include {(host.Git.AheadCount == 1 ? "it" : "them")}.";
    public string PublishButtonText => host.Git.Upstream is null ? "Publish branch" : "Push";

    internal async Task OpenAsync()
    {
        if (host.Provider is not { } provider) return;
        var branch = host.Git.Branch;
        if (!DevOpsViewModel.IsBranchName(branch))
        {
            host.ReportStatus("Switch to a branch in the Git pane to create a pull request from it.");
            return;
        }
        SourceBranch = branch;
        Existing = null;
        Title = DevOpsText.TitleFromBranch(branch);
        Description = "";
        IsDraft = false;
        AutoComplete = false;
        WorkItems = DevOpsText.BranchWorkItem(branch) is { } fromBranch ? fromBranch.ToString(CultureInfo.InvariantCulture) : "";
        IsOpen = true;
        RaiseBranchState();
        await _prefill.Run(async token =>
        {
            var branchesTask = provider.ListBranchesAsync(token);
            var defaultTask = provider.GetDefaultBranchAsync(token);
            var existingTask = provider.FindPullRequestAsync(branch, token);
            await Task.WhenAll(branchesTask, defaultTask, existingTask);
            token.ThrowIfCancellationRequested();
            var branches = (await branchesTask).Where(name => name != branch).ToList();
            GitViewModel.Replace(TargetBranches, branches);
            var target = await defaultTask is { } preferred && preferred != branch ? preferred : branches.FirstOrDefault(name => name is "main" or "master" or "develop") ?? branches.FirstOrDefault();
            TargetBranch = target;
            Existing = await existingTask;
            await PrefillFromCommitsAsync(token);
        }, host.ReportError, loading => IsLoading = loading);
    }

    partial void OnTargetBranchChanged(string? value)
    {
        // The commits a request carries depend on its target.
        if (IsOpen && !IsLoading) _prefill.Start(PrefillFromCommitsAsync, host.ReportError, loading => IsLoading = loading);
    }

    /// <summary>Fills the title and description from the commits the target lacks, unless the person already wrote their own.</summary>
    private async Task PrefillFromCommitsAsync(CancellationToken token)
    {
        if (host.Git.RepositoryRoot is not { } root || TargetBranch is not { } target) return;
        var subjects = await host.GitService.GetCommitSubjectsAsync(root, host.Git.RemoteName, target, SourceBranch, token);
        token.ThrowIfCancellationRequested();
        if (subjects.Count == 1 && Title == DevOpsText.TitleFromBranch(SourceBranch)) Title = subjects[0];
        if (Description.Length == 0 || Description.StartsWith("- ", StringComparison.Ordinal))
            Description = subjects.Count > 1 ? string.Join('\n', subjects.Select(subject => "- " + subject)) : "";
        var mentioned = DevOpsText.WorkItemReferences([.. subjects]).Concat(DevOpsText.BranchWorkItem(SourceBranch) is { } id ? [id] : Array.Empty<int>())
            .Distinct().Order().Select(number => number.ToString(CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(WorkItems) || DevOpsText.BranchWorkItem(SourceBranch)?.ToString(CultureInfo.InvariantCulture) == WorkItems.Trim())
            WorkItems = string.Join(", ", mentioned);
    }

    /// <summary>The Git pane's branch or its upstream changed: the form follows, or closes when the branch is gone.</summary>
    internal void BranchChanged()
    {
        RaiseBranchState();
        if (IsOpen && host.Git.Branch != SourceBranch) Close();
    }

    private void RaiseBranchState()
    {
        OnPropertyChanged(nameof(NeedsPublish));
        OnPropertyChanged(nameof(PublishText));
        OnPropertyChanged(nameof(PublishButtonText));
        OnPropertyChanged(nameof(HasAutoComplete));
        OnPropertyChanged(nameof(WorkItemsLabel));
        PublishCommand.NotifyCanExecuteChanged();
    }

    internal void Close()
    {
        _prefill.Cancel();
        IsOpen = false;
        Existing = null;
    }

    internal IEnumerable<Task> Loads => [_prefill.Task];

    /// <summary>The work item numbers typed in the form; anything that is not a number is reported, not dropped silently.</summary>
    internal IReadOnlyList<int> ParseWorkItems()
    {
        var numbers = new List<int>();
        foreach (var part in WorkItems.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = part.Trim().TrimStart('#');
            if (text.StartsWith("AB#", StringComparison.OrdinalIgnoreCase)) text = text[3..];
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                throw new InvalidOperationException($"“{part}” is not a work item number. List numbers separated by commas, such as 12, 34.");
            if (!numbers.Contains(number)) numbers.Add(number);
        }
        return numbers;
    }

    private bool CanCreate() => IsOpen && host.CanAct && !IsLoading && Existing is null && !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(TargetBranch);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        IReadOnlyList<int> workItems;
        try { workItems = ParseWorkItems(); }
        catch (InvalidOperationException ex) { host.ReportError(ex); return; }
        if (NeedsPublish && !await host.ConfirmAsync("Branch not published",
                $"{PublishText}\n\nCreate the pull request anyway? It will show only what the remote has.")) return;
        var request = new NewPullRequest(SourceBranch, TargetBranch!, Title.Trim(), Description.Trim(), IsDraft, workItems, HasAutoComplete && AutoComplete);
        await host.RunAsync($"Create pull request from {SourceBranch}", async (provider, _, token) =>
        {
            var created = await provider.CreatePullRequestAsync(request, token);
            Close();
            host.ReportStatus($"Created !{created.Number}: {created.Title}");
            host.ShowPullRequest(created.Number);
        });
    }

    private bool CanPublish() => IsOpen && NeedsPublish && host.CanAct && !host.Git.IsBusy;

    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task PublishAsync()
    {
        var git = host.Git;
        var branch = SourceBranch;
        var remote = git.RemoteName ?? "origin";
        var done = git.Upstream is null
            ? await git.RunToolActionAsync($"Publish {branch}", (root, signIn, token) => host.GitService.PublishBranchAsync(root, remote, branch, token, signIn), changesWorkspaceFiles: false, network: true)
            : await git.RunToolActionAsync("Push", (root, signIn, token) => host.GitService.PushAsync(root, token, signIn), changesWorkspaceFiles: false, network: true);
        host.ReportStatus(done ? $"{branch} is on {remote}" : git.HasError ? "Git: " + git.Error : "Git is busy. Try again when the Git pane's action finishes.");
        RaiseBranchState();
    }

    [RelayCommand]
    private void Cancel() => Close();

    [RelayCommand]
    private void OpenExisting()
    {
        if (Existing is not { } existing) return;
        Close();
        host.ShowPullRequest(existing.Number);
    }

    internal void NotifyCommands()
    {
        CreateCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _prefill.Cancel();
}
