using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Features.Documents;

/// <summary>
/// Open files changed on disk by another program (Git in a terminal, a formatter, another editor). They are listed in
/// a banner above the editors, each with its two choices and what each choice does, until the user decides.
/// </summary>
public sealed partial class ExternalChangesViewModel : ObservableObject, IDisposable
{
    private readonly IExternalChangesHost _host;
    private readonly DocumentStore _store;
    private readonly OpenDocumentWatcher _watcher;
    private Task? _check;
    private bool _recheckFiles, _recheckFromApp;
    private bool _disposed;

    public ExternalChangesViewModel(IExternalChangesHost host, DocumentStore store, IUiDispatcher dispatcher)
    {
        _host = host; _store = store;
        Changes.CollectionChanged += ChangesChanged;
        _watcher = new OpenDocumentWatcher(() => dispatcher.Post(() => _ = CheckAsync()));
        host.DocumentsChanged += () =>
        {
            _watcher.Watch(host.Documents.Select(document => document.State.Path));
            foreach (var closed in Changes.Where(change => !host.Documents.Contains(change.Document)).ToArray()) Changes.Remove(closed);
        };
    }

    public ObservableCollection<ExternalChange> Changes { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    /// <summary>Files without unsaved edits follow the disk without asking; the banner then lists only conflicts.</summary>
    [ObservableProperty] public partial bool AutoReloadUnchangedFiles { get; set; }
    partial void OnAutoReloadUnchangedFilesChanged(bool value) { _host.SettingChanged(); if (value) _ = CheckAsync(); }
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ToggleText));

    public bool HasChanges => Changes.Count > 0;
    public string ToggleText => IsExpanded ? "Hide details" : "Review";
    public string Title => Changes.Count == 1
        ? $"{Changes[0].Name} was changed outside WpfStudio"
        : $"{Changes.Count} open files were changed outside WpfStudio";
    public string Hint
    {
        get
        {
            var unsaved = Changes.Count(change => change.HasUnsavedEdits);
            var deleted = Changes.Count(change => change.IsDeleted);
            var parts = new List<string> { "Nothing changes until you choose. The editor still shows your version." };
            if (unsaved > 0) parts.Add(unsaved == Changes.Count ? "You have unsaved edits in " + (unsaved == 1 ? "it." : "all of them.") : $"{unsaved} of them have unsaved edits.");
            if (deleted > 0) parts.Add(deleted == 1 && Changes.Count == 1 ? "It was deleted on disk." : $"{deleted} were deleted on disk.");
            return string.Join(" ", parts);
        }
    }

    private void ChangesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is not null) foreach (ExternalChange change in args.NewItems) change.PropertyChanged += ChangeUpdated;
        if (args.OldItems is not null) foreach (ExternalChange change in args.OldItems) { change.PropertyChanged -= ChangeUpdated; change.Detach(); }
        if (Changes.Count > 0 && args.Action == NotifyCollectionChangedAction.Add) IsExpanded = true;
        NotifyChanges();
    }
    private void ChangeUpdated(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => NotifyChanges();
    private void NotifyChanges()
    {
        OnPropertyChanged(nameof(HasChanges)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Hint));
    }

    /// <summary>Drops the pending review of a document, as after it was reloaded or saved by the user.</summary>
    public void Forget(EditorViewModel document)
    {
        if (Changes.FirstOrDefault(change => ReferenceEquals(change.Document, document)) is { } change) Changes.Remove(change);
        document.State.HasExternalChange = false;
    }

    /// <summary>
    /// Compares open files with disk. Files whose disk version changed are offered for review instead of being
    /// replaced silently. <paramref name="fromAppOperation"/> marks changes WpfStudio made itself (a Git checkout,
    /// pull or merge): files without unsaved edits then follow the disk at once.
    /// </summary>
    public Task CheckAsync(bool fromAppOperation = false)
    {
        // A check already running reads the files again before it finishes; callers wait for that pass.
        if (_check is { IsCompleted: false } running) { _recheckFiles = true; _recheckFromApp |= fromAppOperation; return running; }
        return _check = CheckCoreAsync(fromAppOperation);
    }
    private async Task CheckCoreAsync(bool fromAppOperation)
    {
        var reloaded = new List<string>();
        try
        {
            do
            {
                fromAppOperation |= _recheckFromApp;
                _recheckFiles = false; _recheckFromApp = false;
                foreach (var doc in _host.Documents.ToArray())
                {
                    if (doc.IsReadOnly) continue;
                    var disk = await _store.ReadDiskAsync(doc.State);
                    var pending = Changes.FirstOrDefault(change => ReferenceEquals(change.Document, doc));
                    // The disk is back to what the editor last loaded or saved: nothing is left to decide.
                    if (disk?.Hash == doc.State.DiskHash) { if (pending is not null || doc.State.HasExternalChange) Forget(doc); continue; }
                    if (disk is { } same && same.Text == doc.State.Content)
                    {
                        // The disk now holds exactly the editor text, e.g. the same edit made elsewhere.
                        doc.State.MarkSavedSnapshot(same.Text, same.Hash);
                        if (pending is not null) Changes.Remove(pending);
                        continue;
                    }
                    if (disk is not null && !doc.State.IsDirty && (fromAppOperation || AutoReloadUnchangedFiles))
                    {
                        await _store.ReloadAsync(doc.State);
                        reloaded.Add(doc.State.Name);
                        if (pending is not null) Changes.Remove(pending);
                        continue;
                    }
                    if (pending is null) Changes.Add(pending = new ExternalChange(doc));
                    pending.Update(disk?.Text, disk?.Hash);
                    doc.State.HasExternalChange = true;
                }
            } while (_recheckFiles);
        }
        catch (Exception ex) { _host.AppendOutput("Checking files changed outside WpfStudio: " + ex.Message); }
        finally
        {
            if (reloaded.Count > 0)
            {
                _host.ReportStatus(reloaded.Count == 1 ? $"Loaded {reloaded[0]} from disk; it changed outside WpfStudio" : $"Loaded {reloaded.Count} files from disk that changed outside WpfStudio");
                _host.AppendOutput("Loaded from disk after outside changes: " + string.Join(", ", reloaded));
            }
            _host.DiskVersionsChecked();
        }
    }

    [RelayCommand] private Task LoadDiskVersionAsync(ExternalChange? change) => change is null ? Task.CompletedTask : _host.GuardAsync(() => LoadDiskVersionCoreAsync(change));
    private async Task LoadDiskVersionCoreAsync(ExternalChange change)
    {
        if (change.IsDeleted) { await _host.CloseDocumentAsync(change.Document, discardChanges: true); Forget(change.Document); return; }
        await _store.ReloadAsync(change.Document.State);
        Forget(change.Document);
        await _store.DeleteRecoveryAsync(change.Document.State.Path);
        _host.ReportStatus($"Loaded {change.Name} from disk");
    }

    [RelayCommand] private void KeepEditorVersion(ExternalChange? change)
    {
        if (change is null) return;
        change.Document.State.KeepEditorVersion(change.DiskText, change.DiskHash);
        Forget(change.Document);
        _host.ReportStatus(change.IsDeleted ? $"Kept {change.Name}; save it to create the file again" : $"Kept your version of {change.Name}; save it to replace the disk version");
    }

    [RelayCommand] private Task LoadAllDiskVersionsAsync() => _host.GuardAsync(async () => { foreach (var change in Changes.ToArray()) await LoadDiskVersionCoreAsync(change); });
    [RelayCommand] private void KeepAllEditorVersions() { foreach (var change in Changes.ToArray()) KeepEditorVersion(change); }
    [RelayCommand] private void Toggle() => IsExpanded = !IsExpanded;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Dispose();
    }
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
