#nullable enable
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Text;

namespace WpfStudio.App.ViewModels;

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
        Folder = root is not null && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? directory[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : directory;
        if (Folder.Length == 0) Folder = root is null ? "" : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) ;
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

public sealed partial class ShellViewModel
{
    public ObservableCollection<ChangeReviewFile> PreviewFiles { get; } = [];
    [ObservableProperty] public partial ChangeReviewFile? SelectedPreviewFile { get; set; }
    public string PreviewSummary
    {
        get
        {
            int added = PreviewFiles.Sum(file => file.Added), removed = PreviewFiles.Sum(file => file.Removed);
            return $"{PreviewFiles.Count} file{(PreviewFiles.Count == 1 ? "" : "s")}" + (added > 0 ? $" · +{added}" : "") + (removed > 0 ? $" −{removed}" : "");
        }
    }
    public bool PreviewHasManyFiles => PreviewFiles.Count > 1;

    private bool _previewFilesPending;

    // A review adds its files one at a time; summarize them once, after the batch.
    private void InitializeChangeReview() => PreviewChanges.CollectionChanged += (_, _) =>
    {
        if (_previewFilesPending) return;
        _previewFilesPending = true;
        _dispatcher.Post(RebuildPreviewFiles);
    };

    private void RebuildPreviewFiles()
    {
        _previewFilesPending = false;
        var root = ReviewRoot();
        PreviewFiles.Clear();
        foreach (var change in PreviewChanges) PreviewFiles.Add(new ChangeReviewFile(change, root));
        SelectedPreviewFile = PreviewFiles.FirstOrDefault(file => ReferenceEquals(file.Change, SelectedPreviewChange));
        OnPropertyChanged(nameof(PreviewSummary));
        OnPropertyChanged(nameof(PreviewHasManyFiles));
    }

    /// <summary>The workspace folder when every file is inside it, otherwise the files' common folder.</summary>
    private string? ReviewRoot()
    {
        var folders = PreviewChanges.Select(change => Path.GetDirectoryName(Path.GetFullPath(change.Path)) ?? "").ToArray();
        if (folders.Length == 0) return null;
        if (Workspace?.Path is { } workspace && Path.GetDirectoryName(Path.GetFullPath(workspace)) is { } workspaceFolder
            && folders.All(folder => folder.StartsWith(workspaceFolder, StringComparison.OrdinalIgnoreCase))) return workspaceFolder;
        var common = folders[0];
        foreach (var folder in folders.Skip(1))
            while (common.Length > 0 && !(folder.Equals(common, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(common.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                common = Path.GetDirectoryName(common) ?? "";
        return common.Length == 0 ? null : common;
    }

    partial void OnSelectedPreviewFileChanged(ChangeReviewFile? value)
    {
        if (value is not null && !ReferenceEquals(value.Change, SelectedPreviewChange)) SelectedPreviewChange = value.Change;
    }

    partial void OnSelectedPreviewChangeChanged(FileChange? value)
    {
        if (!ReferenceEquals(SelectedPreviewFile?.Change, value)) SelectedPreviewFile = PreviewFiles.FirstOrDefault(file => ReferenceEquals(file.Change, value));
    }
}
