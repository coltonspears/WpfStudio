using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core;

namespace WpfStudio.App.Features.Workbench;

/// <summary>A pinned set of files, such as the views, view models and services of one feature.</summary>
public sealed partial class FileGroupViewModel : ObservableObject
{
    public FileGroupViewModel(string name, IEnumerable<string> files) { Name = name; foreach (var file in files) Add(file); }
    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    public ObservableCollection<FileGroupEntry> Files { get; } = [];
    public string Summary => Files.Count == 1 ? "1 file" : $"{Files.Count} files";
    internal void Add(string path)
    {
        if (Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        Files.Add(new(this, path)); OnPropertyChanged(nameof(Summary));
    }
    internal void Remove(FileGroupEntry entry) { Files.Remove(entry); OnPropertyChanged(nameof(Summary)); }
    internal FileGroup ToModel() => new(Name, Files.Select(f => f.Path).ToList());
}
public sealed record FileGroupEntry(FileGroupViewModel Group, string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool Exists => File.Exists(Path);
    public string IconKind => WpfStudio.App.Controls.FileIconKindConverter.KindFor(Path);
}

/// <summary>Pinned file groups: open the files of a feature together instead of finding each one.</summary>
public sealed partial class FileGroupsViewModel : ObservableObject
{
    private readonly IWorkbenchHost _host;
    private readonly WorkspaceStateStore<FileGroup> _store;
    private string _lastGroupName = "";

    public FileGroupsViewModel(IWorkbenchHost host, WorkspaceStateStore<FileGroup>? store = null)
    {
        _host = host;
        _store = store ?? new("file-groups");
    }

    public ObservableCollection<FileGroupViewModel> Groups { get; } = [];

    /// <summary>Replaces the groups with the ones saved for the current workspace.</summary>
    public async Task LoadAsync()
    {
        Groups.Clear();
        if (_host.WorkspacePath is not { } workspace) return;
        foreach (var group in await _store.LoadAsync(workspace, _host.Lifetime)) Groups.Add(new(group.Name, group.Files ?? []));
    }
    private Task SaveAsync() => _host.WorkspacePath is { } workspace ? _store.SaveAsync(workspace, Groups.Select(g => g.ToModel()), _host.Lifetime) : Task.CompletedTask;

    /// <summary>The named group, created when no group has that name. Null when the prompt was cancelled.</summary>
    private async Task<FileGroupViewModel?> AskForGroupAsync(string title)
    {
        if (_host.WorkspacePath is null) { _host.ReportStatus("Open a solution to pin file groups for it"); return null; }
        var existing = Groups.Count == 0 ? "" : " Existing: " + string.Join(", ", Groups.Select(g => g.Name)) + ".";
        var name = (await _host.Dialogs.PromptAsync(title, "Group name; a new name creates the group." + existing, _lastGroupName.Length > 0 ? _lastGroupName : "New feature"))?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        _lastGroupName = name;
        var group = Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (group == null) { group = new(name, []); Groups.Add(group); }
        group.IsExpanded = true;
        return group;
    }
    private async Task AddToGroupAsync(string title, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { _host.ReportStatus("Nothing to add to a file group"); return; }
        if (await AskForGroupAsync(title) is not { } group) return;
        foreach (var path in paths) group.Add(path);
        await SaveAsync();
        _host.ReportStatus($"{group.Name}: {group.Summary}");
        _host.ShowTool(Workbenches.FileGroups);
    }
    [RelayCommand] private Task AddActiveAsync() => _host.GuardAsync(() => AddToGroupAsync("Add file to group", _host.ActiveLocation is { } active ? [active.Path] : []));
    [RelayCommand] private Task AddOpenTabsAsync() => _host.GuardAsync(() => AddToGroupAsync("Pin open tabs as a group", _host.OpenDocumentPaths));
    [RelayCommand] private Task AddSelectedAsync() => _host.GuardAsync(() => AddToGroupAsync("Add to file group", _host.SelectedExplorerFile is { Length: > 0 } path ? [path] : []));

    /// <summary>Opens every file of the group that still exists, leaving other tabs open.</summary>
    [RelayCommand] private Task OpenGroupAsync(FileGroupViewModel? group) => group == null ? Task.CompletedTask : _host.GuardAsync(async () =>
    {
        var missing = 0;
        foreach (var entry in group.Files.ToArray())
            if (entry.Exists) await _host.OpenDocumentAsync(entry.Path); else missing++;
        _host.ReportStatus(missing == 0 ? $"Opened {group.Name}" : $"Opened {group.Name}; {missing} file(s) no longer exist");
    });
    /// <summary>Closes tabs outside the group, asking about unsaved ones, then opens the group.</summary>
    [RelayCommand] private Task SwitchToGroupAsync(FileGroupViewModel? group) => group == null ? Task.CompletedTask : _host.GuardAsync(async () =>
    {
        var keep = group.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _host.OpenDocumentPaths.Where(p => !keep.Contains(p)).ToArray())
            if (!await _host.CloseDocumentAsync(path)) return;
        await OpenGroupCommand.ExecuteAsync(group);
    });
    [RelayCommand] private Task OpenEntryAsync(FileGroupEntry? entry) => entry is not { Exists: true } ? Task.CompletedTask : _host.GuardAsync(() => _host.OpenDocumentAsync(entry.Path));
    [RelayCommand] private Task RemoveEntryAsync(FileGroupEntry? entry) => entry == null ? Task.CompletedTask : _host.GuardAsync(async () =>
    {
        entry.Group.Remove(entry);
        await SaveAsync();
    });
    [RelayCommand] private Task RenameGroupAsync(FileGroupViewModel? group) => group == null ? Task.CompletedTask : _host.GuardAsync(async () =>
    {
        var name = (await _host.Dialogs.PromptAsync("Rename file group", "New name", group.Name))?.Trim();
        if (string.IsNullOrEmpty(name) || name == group.Name) return;
        if (Groups.Any(g => g != group && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { _host.ReportStatus($"A group named {name} already exists"); return; }
        group.Name = name;
        await SaveAsync();
    });
    [RelayCommand] private Task DeleteGroupAsync(FileGroupViewModel? group) => group == null ? Task.CompletedTask : _host.GuardAsync(async () =>
    {
        if (!await _host.Dialogs.ConfirmAsync("Delete file group", $"Delete the group {group.Name}? Its files are not changed.")) return;
        Groups.Remove(group);
        await SaveAsync();
    });
}
