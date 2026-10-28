using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Git;

namespace WpfStudio.App.ViewModels;

/// <summary>
/// Open files changed on disk by another program (Git in a terminal, a formatter, another editor). They are listed in
/// a banner above the editors, each with its two choices and what each choice does, until the user decides.
/// </summary>
public sealed partial class ShellViewModel
{
    private bool _recheckFiles, _recheckFromApp;
    private OpenDocumentWatcher? _documentWatcher;
    public ObservableCollection<ExternalChange> ExternalChanges { get; } = [];
    [ObservableProperty] public partial bool IsExternalChangesExpanded { get; set; } = true;
    /// <summary>Files without unsaved edits follow the disk without asking; the banner then lists only conflicts.</summary>
    [ObservableProperty] public partial bool AutoReloadUnchangedFiles { get; set; }
    partial void OnAutoReloadUnchangedFilesChanged(bool value) { if (_initialized) _ = SaveSettingsAsync(); if (value) _ = CheckExternalChangesAsync(); }
    partial void OnIsExternalChangesExpandedChanged(bool value) => OnPropertyChanged(nameof(ExternalChangesToggleText));

    public bool HasExternalChanges => ExternalChanges.Count > 0;
    public string ExternalChangesToggleText => IsExternalChangesExpanded ? "Hide details" : "Review";
    public string ExternalChangesTitle => ExternalChanges.Count == 1
        ? $"{ExternalChanges[0].Name} was changed outside WpfStudio"
        : $"{ExternalChanges.Count} open files were changed outside WpfStudio";
    public string ExternalChangesHint
    {
        get
        {
            var unsaved = ExternalChanges.Count(change => change.HasUnsavedEdits);
            var deleted = ExternalChanges.Count(change => change.IsDeleted);
            var parts = new List<string> { "Nothing changes until you choose. The editor still shows your version." };
            if (unsaved > 0) parts.Add(unsaved == ExternalChanges.Count ? "You have unsaved edits in " + (unsaved == 1 ? "it." : "all of them.") : $"{unsaved} of them have unsaved edits.");
            if (deleted > 0) parts.Add(deleted == 1 && ExternalChanges.Count == 1 ? "It was deleted on disk." : $"{deleted} were deleted on disk.");
            return string.Join(" ", parts);
        }
    }

    private void InitializeExternalChanges()
    {
        ExternalChanges.CollectionChanged += ExternalChangesChanged;
        _documentWatcher = new OpenDocumentWatcher(() => _dispatcher.Post(() => _ = CheckExternalChangesAsync()));
        Documents.CollectionChanged += (_, args) =>
        {
            _documentWatcher.Watch(Documents.Select(document => document.State.Path));
            if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)
                foreach (var closed in ExternalChanges.Where(change => !Documents.Contains(change.Document)).ToArray()) ExternalChanges.Remove(closed);
        };
    }

    private void ExternalChangesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is not null) foreach (ExternalChange change in args.NewItems) change.PropertyChanged += ExternalChangeUpdated;
        if (args.OldItems is not null) foreach (ExternalChange change in args.OldItems) { change.PropertyChanged -= ExternalChangeUpdated; change.Detach(); }
        if (ExternalChanges.Count > 0 && args.Action == NotifyCollectionChangedAction.Add) IsExternalChangesExpanded = true;
        NotifyExternalChanges();
    }
    private void ExternalChangeUpdated(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => NotifyExternalChanges();
    private void NotifyExternalChanges()
    {
        OnPropertyChanged(nameof(HasExternalChanges)); OnPropertyChanged(nameof(ExternalChangesTitle)); OnPropertyChanged(nameof(ExternalChangesHint));
    }

    private void ForgetExternalChange(EditorViewModel document)
    {
        if (ExternalChanges.FirstOrDefault(change => ReferenceEquals(change.Document, document)) is { } change) ExternalChanges.Remove(change);
        document.State.HasExternalChange = false;
    }

    [RelayCommand] private Task LoadDiskVersionAsync(ExternalChange? change) => change is null ? Task.CompletedTask : GuardAsync(() => LoadDiskVersionCoreAsync(change));
    private async Task LoadDiskVersionCoreAsync(ExternalChange change)
    {
        if (change.IsDeleted) { await CloseDocumentAsync(change.Document, discardChanges: true); ForgetExternalChange(change.Document); return; }
        await _store.ReloadAsync(change.Document.State);
        ForgetExternalChange(change.Document);
        _store.DeleteRecovery(change.Document.State.Path);
        Status = $"Loaded {change.Name} from disk";
    }

    [RelayCommand] private void KeepEditorVersion(ExternalChange? change)
    {
        if (change is null) return;
        change.Document.State.KeepEditorVersion(change.DiskText, change.DiskHash);
        ForgetExternalChange(change.Document);
        Status = change.IsDeleted ? $"Kept {change.Name}; save it to create the file again" : $"Kept your version of {change.Name}; save it to replace the disk version";
    }

    [RelayCommand] private Task LoadAllDiskVersionsAsync() => GuardAsync(async () => { foreach (var change in ExternalChanges.ToArray()) await LoadDiskVersionCoreAsync(change); });
    [RelayCommand] private void KeepAllEditorVersions() { foreach (var change in ExternalChanges.ToArray()) KeepEditorVersion(change); }
    [RelayCommand] private void ToggleExternalChanges() => IsExternalChangesExpanded = !IsExternalChangesExpanded;
}

/// <summary>One open file whose disk version differs from what the editor last loaded or saved.</summary>
public sealed partial class ExternalChange : ObservableObject
{
    private readonly EditorViewModel _document;
    public ExternalChange(EditorViewModel document)
    {
        _document = document;
        document.State.PropertyChanged += StateChanged;
    }
    public EditorViewModel Document => _document;
    public string Name => _document.State.Name;
    public string Path => _document.State.Path;
    public string? DiskText { get; private set; }
    public string? DiskHash { get; private set; }
    public bool IsDeleted => DiskHash is null;
    public bool HasUnsavedEdits => _document.State.IsDirty;
    public string EditorText => _document.State.Content;
    [ObservableProperty] public partial string Summary { get; private set; } = "";
    [ObservableProperty] public partial bool IsComparing { get; set; }
    partial void OnIsComparingChanged(bool value) { OnPropertyChanged(nameof(CompareBefore)); OnPropertyChanged(nameof(CompareAfter)); }
    public bool CanCompare => !IsDeleted;
    // The diff is computed only while it is shown; the editor text changes with every keystroke.
    public string? CompareBefore => IsComparing ? EditorText : null;
    public string? CompareAfter => IsComparing ? DiskText : null;

    public string Situation => IsDeleted
        ? HasUnsavedEdits ? "Deleted on disk. You have unsaved edits in WpfStudio." : "Deleted on disk."
        : HasUnsavedEdits ? "Changed on disk, and you have unsaved edits in WpfStudio." : "Changed on disk. You have no unsaved edits to it.";
    public string LoadLabel => IsDeleted ? "Close tab" : "Load disk version";
    public string LoadConsequence => IsDeleted
        ? "Close tab: closes the editor" + (HasUnsavedEdits ? " and discards your unsaved edits." : ".")
        : "Load disk version: the editor shows the file as it is on disk now" + (HasUnsavedEdits ? "; your unsaved edits are discarded." : ".");
    public string KeepConsequence => IsDeleted
        ? "Keep my version: the tab stays open as unsaved (•). Saving creates the file again."
        : "Keep my version: the editor text stays as it is and the tab is marked unsaved (•). Saving replaces the disk version with yours.";

    internal void Update(string? diskText, string? diskHash)
    {
        DiskText = diskText; DiskHash = diskHash;
        if (diskText is null) Summary = "deleted";
        else
        {
            int added = 0, removed = 0;
            foreach (var change in LineChanges.Compute(EditorText, diskText))
            {
                var original = change.OriginalText.Length == 0 && change.Kind == LineChangeKind.Added ? 0 : change.OriginalText.Split('\n').Length;
                if (change.Kind != LineChangeKind.Deleted) added += change.LineCount;
                if (change.Kind != LineChangeKind.Added) removed += original;
            }
            Summary = added + removed == 0 ? "changed on disk" : $"+{added} −{removed} lines on disk";
        }
        if (IsDeleted) IsComparing = false;
        Refresh();
    }

    private void StateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DocumentState.IsDirty) or nameof(DocumentState.Content)) Refresh();
    }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(DiskText), nameof(IsDeleted), nameof(HasUnsavedEdits), nameof(EditorText), nameof(CompareBefore), nameof(CompareAfter), nameof(CanCompare), nameof(Situation), nameof(LoadLabel), nameof(LoadConsequence), nameof(KeepConsequence) })
            OnPropertyChanged(name);
    }
    internal void Detach() => _document.State.PropertyChanged -= StateChanged;
}
