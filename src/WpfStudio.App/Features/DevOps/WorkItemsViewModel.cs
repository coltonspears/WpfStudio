using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Git;
using WpfStudio.Core.DevOps;

namespace WpfStudio.App.Features.DevOps;

/// <summary>
/// The Work items tab (Issues on GitHub): built-in and saved lists, search, the selected item's description, discussion and links,
/// and what can be done to it: change its state, assign it, comment, create a branch for it in the Git pane, or copy it as Markdown.
/// </summary>
public sealed partial class WorkItemsViewModel(IDevOpsHost host) : ObservableObject, IDisposable
{
    private const int ListSize = 100;
    private readonly LatestLoad _queries = new(), _list = new(), _details = new();
    private int _loadedVersion = -1;
    private bool _quiet;
    private int? _pendingId;
    private string? _lastHeading;

    public ObservableCollection<DevOpsWorkItemQuery> Queries { get; } = [];
    public ObservableCollection<DevOpsWorkItem> Items { get; } = [];
    public ObservableCollection<string> Types { get; } = [];

    [ObservableProperty] public partial DevOpsWorkItemQuery? SelectedQuery { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] public partial DevOpsWorkItem? Selected { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BranchPreview), nameof(HasDetails))] public partial DevOpsWorkItemDetails? Details { get; private set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCommand))] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string ListHeading { get; private set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool IsDetailLoading { get; private set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyStateCommand))] public partial string? NewState { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddCommentCommand))] public partial string NewComment { get; set; } = "";
    [ObservableProperty] public partial bool IsCreating { get; private set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateCommand))] public partial string? NewType { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateCommand))] public partial string NewTitle { get; set; } = "";
    [ObservableProperty] public partial string NewDescription { get; set; } = "";
    [ObservableProperty] public partial bool AssignNewToMe { get; set; } = true;

    public bool HasSelection => Selected is not null;
    public bool HasDetails => Details is not null;
    public string ItemName => host.Provider?.Capabilities.WorkItemName ?? "Work item";
    public bool HasTypes => host.Provider?.Capabilities.WorkItemTypes == true;
    public string BranchPreview => Details is { } details ? DevOpsText.BranchName(host.Settings.BranchTemplate, details.Item.Id, details.Item.Title, details.Item.Type) : "";
    public string EmptyText => IsLoading ? "Loading…" : $"No {(host.Provider?.Capabilities.WorkItemsName ?? "work items").ToLowerInvariant()} here.";

    partial void OnSelectedQueryChanged(DevOpsWorkItemQuery? value)
    {
        if (_quiet || value is null) return;
        SearchText = "";
        LoadList();
    }

    partial void OnSelectedChanged(DevOpsWorkItem? value) { if (!_quiet) LoadDetails(); }
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(EmptyText));
    partial void OnDetailsChanged(DevOpsWorkItemDetails? value)
    {
        NewState = value?.Item.State;
        NotifyCommands();
    }

    internal void EnsureLoaded()
    {
        if (_loadedVersion == host.Version || host.Provider is not { } provider) return;
        _loadedVersion = host.Version;
        foreach (var name in new[] { nameof(ItemName), nameof(HasTypes), nameof(EmptyText) }) OnPropertyChanged(name);
        Quietly(() =>
        {
            GitViewModel.Replace(Queries, provider.BuiltInQueries);
            SelectedQuery = Queries.FirstOrDefault();
        });
        LoadList();
        if (!provider.Capabilities.SavedQueries) return;
        // Saved queries are added below the built-in lists when they arrive; a project without any shows only those.
        _queries.Start(async token =>
        {
            var saved = await provider.GetSavedQueriesAsync(token);
            token.ThrowIfCancellationRequested();
            foreach (var query in saved) Queries.Add(query);
        }, _ => { });
    }

    internal void Reload()
    {
        if (_loadedVersion != host.Version) EnsureLoaded();
        else if (SearchText.Trim().Length > 0) Search();
        else LoadList();
    }

    internal void Reset()
    {
        _queries.Cancel();
        _list.Cancel();
        _details.Cancel();
        _loadedVersion = -1;
        _pendingId = null;
        Quietly(() =>
        {
            Queries.Clear();
            SelectedQuery = null;
            Items.Clear();
            Selected = null;
        });
        Details = null;
        IsCreating = false;
        ListHeading = "";
        // Types belong to one project's process (or to GitHub); another connection reads its own.
        Types.Clear();
        NewType = null;
    }

    internal IEnumerable<Task> Loads => [_queries.Task, _list.Task, _details.Task];

    /// <summary>Selects a work item by number, reading it when no loaded list holds it.</summary>
    internal void Show(int id)
    {
        if (_loadedVersion != host.Version) EnsureLoaded();
        IsCreating = false;
        if (Items.FirstOrDefault(item => item.Id == id) is { } listed) { Selected = listed; return; }
        _pendingId = id;
        LoadDetails(id);
    }

    private void LoadList()
    {
        if (host.Provider is not { } provider || SelectedQuery is not { } query) return;
        ListHeading = query.Label;
        RunList(token => provider.RunQueryAsync(query, ListSize, token), query.Label);
    }

    private bool CanSearch() => host.Provider is not null && SearchText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void Search()
    {
        if (host.Provider is not { } provider) return;
        var text = SearchText.Trim();
        Quietly(() => SelectedQuery = null);
        RunList(token => provider.SearchWorkItemsAsync(text, ListSize, token), $"Search: {text}");
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = "";
        if (SelectedQuery is null) SelectedQuery = Queries.FirstOrDefault();
    }

    private void RunList(Func<CancellationToken, Task<IReadOnlyList<DevOpsWorkItem>>> read, string heading)
    {
        _list.Start(async token =>
        {
            var items = await read(token);
            token.ThrowIfCancellationRequested();
            var selected = Selected;
            Quietly(() =>
            {
                GitViewModel.Replace(Items, items);
                Selected = selected is null ? null : Items.FirstOrDefault(item => item.Id == selected.Id);
            });
            ListHeading = $"{heading} · {items.Count}{(items.Count >= ListSize ? "+" : "")}";
            // The item still selected is read again too: its state, discussion and revision may have changed, and a change refused
            // for an old revision succeeds only once the new one is read.
            if (Selected is not null) LoadDetails();
            // A new list opens on its first item; an item opened from a link stays shown until another is chosen.
            else if (_pendingId is null && Items.Count > 0 && (Details is null || Items.Any(item => item.Id == Details.Item.Id) || heading != _lastHeading)) Selected = Items[0];
            _lastHeading = heading;
        }, host.ReportError, loading => IsLoading = loading);
    }

    private void LoadDetails(int? id = null)
    {
        if (host.Provider is not { } provider || (id ?? Selected?.Id) is not { } target)
        {
            _details.Cancel();
            Details = null;
            return;
        }
        if (Details?.Item.Id != target) Details = null;
        _details.Start(async token =>
        {
            var details = await provider.GetWorkItemAsync(target, token);
            token.ThrowIfCancellationRequested();
            _pendingId = null;
            Details = details;
            NewComment = "";
            // The list shows what was just read.
            Quietly(() =>
            {
                if (Items.FirstOrDefault(item => item.Id == target) is { } listed)
                {
                    Items[Items.IndexOf(listed)] = details.Item;
                    if (Selected?.Id == target || Selected is null) Selected = details.Item;
                }
                else if (id is not null) Selected = null;
            });
        }, host.ReportError, loading => IsDetailLoading = loading);
    }

    // ------------------------------------------------------------------ Actions

    private bool CanUse() => host.CanAct && Details is not null;
    private bool CanApplyState() => CanUse() && !string.IsNullOrEmpty(NewState) && NewState != Details!.Item.State;
    private bool CanAssignToMe() => CanUse() && host.User is not null && !string.Equals(Details!.Item.AssignedTo, host.User.DisplayName, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Details.Item.AssignedTo, host.User.UniqueName, StringComparison.OrdinalIgnoreCase);
    private bool CanUnassign() => CanUse() && !string.IsNullOrEmpty(Details!.Item.AssignedTo);
    private bool CanAddComment() => CanUse() && !string.IsNullOrWhiteSpace(NewComment);
    private bool CanCreateBranch() => CanUse() && host.Git.HasRepository && !host.Git.IsBusy;

    [RelayCommand(CanExecute = nameof(CanApplyState))]
    private Task ApplyStateAsync()
    {
        if (Details is not { } details || NewState is not { } state) return Task.CompletedTask;
        return host.RunAsync($"Set {ItemName.ToLowerInvariant()} {details.Item.Id} to {state}", async (provider, _, token) =>
        {
            await provider.SetWorkItemStateAsync(details, state, token);
            LoadDetails(details.Item.Id);
        });
    }

    [RelayCommand(CanExecute = nameof(CanAssignToMe))]
    private Task AssignToMeAsync() => Details is not { } details ? Task.CompletedTask :
        host.RunAsync($"Assign {details.Item.Id} to me", async (provider, me, token) =>
        {
            await provider.AssignWorkItemAsync(details, me, token);
            LoadDetails(details.Item.Id);
        });

    [RelayCommand(CanExecute = nameof(CanUnassign))]
    private Task UnassignAsync() => Details is not { } details ? Task.CompletedTask :
        host.RunAsync($"Unassign {details.Item.Id}", async (provider, _, token) =>
        {
            await provider.AssignWorkItemAsync(details, null, token);
            LoadDetails(details.Item.Id);
        });

    [RelayCommand(CanExecute = nameof(CanAddComment))]
    private Task AddCommentAsync()
    {
        if (Details is not { } details) return Task.CompletedTask;
        var text = NewComment.Trim();
        return host.RunAsync($"Comment on {details.Item.Id}", async (provider, _, token) =>
        {
            await provider.AddWorkItemCommentAsync(details.Item, text, token);
            NewComment = "";
            LoadDetails(details.Item.Id);
        });
    }

    /// <summary>Creates and switches to a branch named for the work item, through the Git pane.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateBranch))]
    private async Task CreateBranchAsync()
    {
        if (Details is not { } details) return;
        var name = await host.PromptAsync("Create branch", $"A branch for {ItemName.ToLowerInvariant()} {details.Item.Id}, made from the current branch. The template is in Access and settings.", BranchPreview);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        var done = await host.Git.RunToolActionAsync($"Create branch {name}",
            (root, _, token) => host.GitService.SwitchBranchAsync(root, name, create: true, token), changesWorkspaceFiles: true, network: false);
        host.ReportStatus(done ? $"Switched to {name}. Its pull request will be linked to {details.Item.Id}."
            : host.Git.HasError ? "Git: " + host.Git.Error : "Git is busy. Try again when the Git pane's action finishes.");
    }

    [RelayCommand(CanExecute = nameof(HasDetails))]
    private void CopyMarkdown()
    {
        if (Details is { } details) host.Copy(DevOpsText.WorkItemMarkdown(details), $"{ItemName.ToLowerInvariant()} {details.Item.Id} as Markdown");
    }

    [RelayCommand]
    private void CopyLink(DevOpsWorkItem? item)
    {
        if ((item ?? Details?.Item) is { } target) host.Copy(target.WebUrl, $"the link to {target.Id}");
    }

    [RelayCommand]
    private void OpenInBrowser(DevOpsWorkItem? item)
    {
        if ((item ?? Details?.Item) is { } target) host.OpenUrl(target.WebUrl);
    }

    [RelayCommand]
    private void OpenLink(DevOpsWorkItemLink? link)
    {
        if (link is null) return;
        if (link.WorkItemId is { } id) Show(id);
        else if (link.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") host.OpenUrl(url);
        else host.ReportStatus(link.Title + " is shown on the web page of the work item.");
    }

    [RelayCommand(CanExecute = nameof(CanStartCreate))]
    private async Task StartCreateAsync()
    {
        if (host.Provider is not { } provider) return;
        NewTitle = "";
        NewDescription = "";
        IsCreating = true;
        if (Types.Count > 0) return;
        var version = host.Version;
        try
        {
            var types = await provider.GetWorkItemTypesAsync(CancellationToken.None);
            if (version != host.Version) return;
            GitViewModel.Replace(Types, types);
            NewType = Types.FirstOrDefault(type => type is "Task" or "Bug" or "Issue") ?? Types.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { host.ReportError(ex); }
    }

    private bool CanStartCreate() => host.CanAct;

    private bool CanCreate() => host.CanAct && IsCreating && !string.IsNullOrWhiteSpace(NewTitle) && !string.IsNullOrWhiteSpace(NewType);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync()
    {
        var (type, title, description, assign) = (NewType!, NewTitle.Trim(), NewDescription.Trim(), AssignNewToMe);
        return host.RunAsync($"Create {type.ToLowerInvariant()}", async (provider, me, token) =>
        {
            var created = await provider.CreateWorkItemAsync(type, title, description, assign ? me : null, token);
            IsCreating = false;
            host.ReportStatus($"Created {created.Type.ToLowerInvariant()} {created.Id}");
            Show(created.Id);
        });
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    internal void NotifyCommands()
    {
        foreach (var command in new IRelayCommand[] { ApplyStateCommand, AssignToMeCommand, UnassignCommand, AddCommentCommand, CreateBranchCommand, CopyMarkdownCommand,
            StartCreateCommand, CreateCommand, SearchCommand })
            command.NotifyCanExecuteChanged();
    }

    private void Quietly(Action action)
    {
        var previous = _quiet;
        _quiet = true;
        try { action(); }
        finally { _quiet = previous; }
    }

    public void Dispose()
    {
        _queries.Cancel();
        _list.Cancel();
        _details.Cancel();
    }
}
