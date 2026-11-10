using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Features.Git;

/// <summary>The History tab: the loaded commits, their filter and paging, and the selected commit's details and diffs.</summary>
public sealed partial class CommitHistoryViewModel(GitService service, IGitOperationHost host) : ObservableObject
{
    public const int PageSize = 50;

    // Commit details load beside Git actions (they only read), each cancelling its own previous request.
    private CancellationTokenSource? _commitLoad, _commitFileLoad;
    private Task _commitTask = Task.CompletedTask, _commitFileTask = Task.CompletedTask;
    private bool _quietSelection;
    // Set by editor history so the commit opens on the file it was opened from.
    private string? _preferredCommitFile;

    /// <summary>Every commit loaded so far, newest first.</summary>
    public ObservableCollection<GitCommit> History { get; } = [];
    /// <summary>The loaded commits that match <see cref="HistoryFilter"/>.</summary>
    public ObservableCollection<GitCommit> VisibleHistory { get; } = [];
    public ObservableCollection<GitCommitFile> CommitFiles { get; } = [];
    [ObservableProperty] public partial GitCommit? SelectedCommit { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsHistoryFiltered))] public partial string HistoryFilter { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HistoryHeading)), NotifyCanExecuteChangedFor(nameof(LoadMoreHistoryCommand))] public partial bool HasMoreHistory { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenCommitOnRemoteCommand))] public partial GitCommitDetails? CommitDetails { get; set; }
    [ObservableProperty] public partial bool IsCommitLoading { get; set; }
    [ObservableProperty] public partial GitCommitFile? SelectedCommitFile { get; set; }
    /// <summary>The selected commit file's diff against the commit's first parent.</summary>
    [ObservableProperty] public partial GitDiff? CommitDiff { get; set; }

    public bool IsHistoryFiltered => HistoryFilter.Trim().Length > 0;
    public bool HistoryFilterHasNoMatches => IsHistoryFiltered && VisibleHistory.Count == 0 && History.Count > 0;
    public string HistoryHeading => IsHistoryFiltered
        ? $"{VisibleHistory.Count} of {History.Count} loaded commits match"
        : $"{History.Count}{(HasMoreHistory ? "+" : "")} commit{(History.Count == 1 ? "" : "s")} on {host.Branch}";
    /// <summary>How many commits a refresh should keep: as many as the reader has paged in.</summary>
    internal int RefreshLimit => Math.Max(PageSize, History.Count);

    partial void OnHistoryFilterChanged(string value) => ApplyHistoryFilter();
    partial void OnSelectedCommitChanged(GitCommit? value) { if (!_quietSelection) LoadCommit(value).Forget(); }
    partial void OnSelectedCommitFileChanged(GitCommitFile? value) { if (!_quietSelection) LoadCommitFile(value).Forget(); }

    private bool CanLoadMoreHistory() => host.CanOperate && HasMoreHistory;
    private bool CanOpenCommitFile(GitCommitFile? file) => host.RepositoryRoot is not null && file is not null && file.Status != 'D';
    private bool CanOpenCommitOnRemote() => host.Remote is not null && CommitDetails is not null;

    [RelayCommand(CanExecute = nameof(CanOpenCommitFile))]
    private void OpenCommitFile(GitCommitFile? file)
    {
        if (file is null || host.RepositoryRoot is not { } root) return;
        var path = Path.GetFullPath(Path.Combine(root, file.Path));
        if (File.Exists(path)) host.OpenFile(path);
        else host.ReportError("This file no longer exists in the working tree.");
    }

    [RelayCommand(CanExecute = nameof(CanOpenCommitOnRemote))]
    private void OpenCommitOnRemote() => host.OpenUrl(host.Remote!.CommitUrl(CommitDetails!.Id));

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanLoadMoreHistory))]
    private Task LoadMoreHistoryAsync(CancellationToken cancel) => host.RunAsync("Load more history", async token =>
    {
        var version = host.WorkspaceVersion;
        var more = await service.LoadHistoryAsync(host.RepositoryRoot!, History.Count, PageSize + 1, token);
        if (version != host.WorkspaceVersion) return;
        foreach (var commit in more.Take(PageSize).Where(commit => !History.Any(existing => existing.Id == commit.Id))) History.Add(commit);
        HasMoreHistory = more.Count > PageSize;
        ApplyHistoryFilter();
        host.ReportStatus($"{History.Count} commits loaded");
    }, cancel);

    [RelayCommand]
    private void ClearHistoryFilter() => HistoryFilter = "";

    /// <summary>
    /// Shows a commit from editor blame, loading its details even when it is not in the loaded page.
    /// With <paramref name="relativePath"/>, that file's diff is selected instead of the commit's first file.
    /// </summary>
    internal void ShowCommit(BlameLine line, string? relativePath)
    {
        if (host.RepositoryRoot is null || line.IsUncommitted) return;
        _preferredCommitFile = relativePath?.Replace('\\', '/');
        host.ShowHistoryTab();
        SelectedCommit = VisibleHistory.FirstOrDefault(commit => commit.Id == line.Commit)
            ?? new GitCommit(line.Commit, line.ShortCommit, line.Author, line.Time.ToString("O"), line.Summary);
        // Already showing this commit: its details do not load again, so switch to the requested file here.
        if (CommitDetails?.Id == line.Commit && TakePreferredFile(CommitDetails.Files) is { } file) SelectedCommitFile = file;
    }

    /// <summary>Replaces the loaded commits with a refresh's, keeping the reader on the same commit.</summary>
    internal void ApplyRefresh(IReadOnlyList<GitCommit> commits, int limit, bool rootChanged)
    {
        var selected = SelectedCommit;
        // Editor blame can select a commit outside the loaded list. It stays selected, unlike one that left the list.
        var keepUnlisted = !rootChanged && selected is not null && !VisibleHistory.Any(x => x.Id == selected.Id);
        Quietly(() =>
        {
            GitViewModel.Replace(History, commits.Take(limit));
            ApplyHistoryFilter();
            SelectedCommit = selected is null ? null : VisibleHistory.FirstOrDefault(x => x.Id == selected.Id) ?? (keepUnlisted ? selected : null);
        });
        HasMoreHistory = commits.Count > limit;
        if (rootChanged || SelectedCommit is null) LoadCommit(SelectedCommit).Forget();
    }

    internal void Clear()
    {
        Quietly(() =>
        {
            History.Clear();
            VisibleHistory.Clear();
            SelectedCommit = null;
        });
        HasMoreHistory = false;
        ClearCommit();
    }

    /// <summary>Waits for commit reads started by selection changes.</summary>
    internal async Task WhenLoadedAsync()
    {
        await _commitTask;
        await _commitFileTask;
    }

    internal void CancelLoads()
    {
        foreach (var load in new[] { _commitLoad, _commitFileLoad }) load?.Cancel();
    }

    internal void NotifyHeading() => OnPropertyChanged(nameof(HistoryHeading));

    internal void NotifyCommands()
    {
        OpenCommitFileCommand.NotifyCanExecuteChanged();
        LoadMoreHistoryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The file editor history asked for, once; null when none was asked for or the commit did not change it.</summary>
    private GitCommitFile? TakePreferredFile(IReadOnlyList<GitCommitFile> files)
    {
        var preferred = _preferredCommitFile;
        _preferredCommitFile = null;
        return preferred is null ? null : files.FirstOrDefault(file => string.Equals(file.Path.Replace('\\', '/'), preferred, StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.OriginalPath?.Replace('\\', '/'), preferred, StringComparison.OrdinalIgnoreCase));
    }

    private Task LoadCommit(GitCommit? commit)
    {
        _commitLoad?.Cancel();
        _commitLoad = null;
        IsCommitLoading = false;
        if (commit is not null && CommitDetails?.Id == commit.Id) return _commitTask = Task.CompletedTask;
        if (commit is null || host.RepositoryRoot is not { } root)
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
            var details = await service.GetCommitDetailsAsync(root, commit.Id, load.Token);
            if (load.IsCancellationRequested) return;
            CommitDetails = details;
            Quietly(() =>
            {
                GitViewModel.Replace(CommitFiles, details.Files);
                SelectedCommitFile = TakePreferredFile(details.Files) ?? details.Files.FirstOrDefault();
            });
            await LoadCommitFile(SelectedCommitFile);
        }
        catch (Exception exception)
        {
            if (!load.IsCancellationRequested && exception is not OperationCanceledException)
            {
                ClearCommit();
                host.ReportError(GitDiagnosticSanitizer.Redact(exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_commitLoad, load)) { _commitLoad = null; IsCommitLoading = false; }
            load.Dispose();
        }
    }

    private Task LoadCommitFile(GitCommitFile? file)
    {
        _commitFileLoad?.Cancel();
        _commitFileLoad = null;
        OpenCommitFileCommand.NotifyCanExecuteChanged();
        if (file is null || CommitDetails is not { } details || host.RepositoryRoot is not { } root)
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
            var diff = await service.GetCommitFileDiffAsync(root, details, file, load.Token);
            if (!load.IsCancellationRequested) CommitDiff = diff;
        }
        catch (Exception exception)
        {
            if (!load.IsCancellationRequested && exception is not OperationCanceledException)
                CommitDiff = new(file.Path, Path.Combine(root, file.Path), "", Message: GitDiagnosticSanitizer.Redact(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_commitFileLoad, load)) _commitFileLoad = null;
            load.Dispose();
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

    /// <summary>Keeps the visible list in history order while changing it in place, so the selection survives filtering.</summary>
    private void ApplyHistoryFilter()
    {
        var filter = HistoryFilter.Trim();
        bool Matches(GitCommit commit) => filter.Length == 0
            || commit.Subject.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || commit.Author.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || commit.RefList.Any(reference => reference.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
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

    private void Quietly(Action action)
    {
        var previous = _quietSelection;
        _quietSelection = true;
        try { action(); }
        finally { _quietSelection = previous; }
    }
}
