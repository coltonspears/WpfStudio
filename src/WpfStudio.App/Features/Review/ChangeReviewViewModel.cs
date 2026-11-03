using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Text;

namespace WpfStudio.App.Features.Review;

/// <summary>One file in the change review: where it is, what kind of change it is, and how many lines change.</summary>
public sealed class ChangeReviewFile
{
    public ChangeReviewFile(FileChange change, string? root)
    {
        Change = change;
        var diff = TextDiff.Compute(change.Before, change.After);
        Added = diff.AddedLines; Removed = diff.RemovedLines;
        IsNew = change.Before.Length == 0 && change.After.Length > 0;
        IsDeleted = change.After.Length == 0 && change.Before.Length > 0;
        Name = Path.GetFileName(change.Path);
        var directory = Path.GetDirectoryName(change.Path) ?? "";
        Folder = root is not null && ChangeReviewViewModel.IsWithin(directory, root)
            ? directory[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : directory;
        if (Folder.Length == 0) Folder = root is null ? "" : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
    }

    public FileChange Change { get; }
    public string Name { get; }
    /// <summary>Folder relative to the workspace (or to the files' common folder).</summary>
    public string Folder { get; }
    public string Summary => Change.Summary;
    public int Added { get; }
    public int Removed { get; }
    public bool IsNew { get; }
    public bool IsDeleted { get; }
    public string Status => IsNew ? "New" : IsDeleted ? "Deleted" : Added + Removed == 0 ? "Unchanged" : "Modified";
    public string AddedText => Added > 0 ? $"+{Added}" : "";
    public string RemovedText => Removed > 0 ? $"−{Removed}" : "";
}

/// <summary>What the change review needs beyond the workbench: someone to tell when it opens or closes.</summary>
public interface IChangeReviewHost : IWorkbenchHost
{
    void ReviewOpenChanged();
}

/// <summary>
/// The modal change review: a set of proposed file changes shown as a diff per file, which the user applies or
/// cancels. <see cref="ShowAsync"/> completes with that decision, so callers read as "if (await review) apply".
/// </summary>
public sealed partial class ChangeReviewViewModel : ObservableObject
{
    private readonly IChangeReviewHost _host;
    private readonly IUiDispatcher _dispatcher;
    private TaskCompletionSource<bool>? _completion;
    private bool _filesPending;

    public ChangeReviewViewModel(IChangeReviewHost host, IUiDispatcher dispatcher)
    {
        _host = host; _dispatcher = dispatcher;
        // A review adds its files one at a time; summarize them once, after the batch.
        Changes.CollectionChanged += (_, _) =>
        {
            if (_filesPending) return;
            _filesPending = true;
            _dispatcher.Post(RebuildFiles);
        };
    }

    public ObservableCollection<FileChange> Changes { get; } = [];
    public ObservableCollection<ChangeReviewFile> Files { get; } = [];
    [ObservableProperty] public partial bool IsOpen { get; private set; }
    partial void OnIsOpenChanged(bool value) => _host.ReviewOpenChanged();
    [ObservableProperty] public partial string Title { get; private set; } = "Preview changes";
    [ObservableProperty] public partial string Warnings { get; private set; } = "";
    [ObservableProperty] public partial FileChange? SelectedChange { get; set; }
    [ObservableProperty] public partial ChangeReviewFile? SelectedFile { get; set; }

    public string Summary
    {
        get
        {
            int added = Files.Sum(file => file.Added), removed = Files.Sum(file => file.Removed);
            return $"{Files.Count} file{(Files.Count == 1 ? "" : "s")}" + (added > 0 ? $" · +{added}" : "") + (removed > 0 ? $" −{removed}" : "");
        }
    }
    public bool HasManyFiles => Files.Count > 1;

    /// <summary>Opens the review and completes with true when the user applies the changes. False when cancelled, empty or already open.</summary>
    public async Task<bool> ShowAsync(string title, IReadOnlyList<FileChange> changes, string warnings)
    {
        if (_completion is { Task.IsCompleted: false }) { _host.ReportStatus("Finish the current change preview first"); return false; }
        if (changes.Count == 0) { _host.ReportStatus("No changes required"); return false; }
        Title = title; Warnings = warnings;
        Changes.Clear(); foreach (var change in changes) Changes.Add(change);
        SelectedChange = changes[0];
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously); IsOpen = true;
        return await _completion.Task;
    }
    [RelayCommand] private void Accept() { IsOpen = false; _completion?.TrySetResult(true); }
    [RelayCommand] private void Cancel() { IsOpen = false; _completion?.TrySetResult(false); }

    private void RebuildFiles()
    {
        _filesPending = false;
        var root = ReviewRoot();
        Files.Clear();
        foreach (var change in Changes) Files.Add(new ChangeReviewFile(change, root));
        SelectedFile = Files.FirstOrDefault(file => ReferenceEquals(file.Change, SelectedChange));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasManyFiles));
    }

    /// <summary>The workspace folder when every file is inside it, otherwise the files' common folder.</summary>
    private string? ReviewRoot()
    {
        var folders = Changes.Select(change => Path.GetDirectoryName(Path.GetFullPath(change.Path)) ?? "").ToArray();
        if (folders.Length == 0) return null;
        if (_host.WorkspacePath is { } workspace && Path.GetDirectoryName(Path.GetFullPath(workspace)) is { } workspaceFolder
            && folders.All(folder => IsWithin(folder, workspaceFolder))) return workspaceFolder;
        var common = folders[0];
        foreach (var folder in folders.Skip(1))
            while (common.Length > 0 && !IsWithin(folder, common))
                common = Path.GetDirectoryName(common) ?? "";
        return common.Length == 0 ? null : common;
    }

    internal static bool IsWithin(string folder, string root) => folder.Equals(root, StringComparison.OrdinalIgnoreCase)
        || folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedFileChanged(ChangeReviewFile? value)
    {
        if (value is not null && !ReferenceEquals(value.Change, SelectedChange)) SelectedChange = value.Change;
    }
    partial void OnSelectedChangeChanged(FileChange? value)
    {
        if (!ReferenceEquals(SelectedFile?.Change, value)) SelectedFile = Files.FirstOrDefault(file => ReferenceEquals(file.Change, value));
    }
}
