using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core;

namespace WpfStudio.App.ViewModels;

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
public sealed partial class ShellViewModel
{
    private readonly WorkspaceStateStore<FileGroup> _fileGroups = new("file-groups");
    private string _lastFileGroup = "";
    public ObservableCollection<FileGroupViewModel> FileGroups { get; } = [];

    private async Task LoadFileGroupsAsync()
    {
        FileGroups.Clear();
        if (Workspace == null) return;
        foreach (var group in await _fileGroups.LoadAsync(Workspace.Path, _lifetime.Token)) FileGroups.Add(new(group.Name, group.Files ?? []));
    }
    private Task SaveFileGroupsAsync() => Workspace == null ? Task.CompletedTask : _fileGroups.SaveAsync(Workspace.Path, FileGroups.Select(g => g.ToModel()), _lifetime.Token);

    /// <summary>The named group, created when no group has that name. Null when the prompt was cancelled.</summary>
    private async Task<FileGroupViewModel?> AskForFileGroupAsync(string title)
    {
        if (Workspace == null) { Status = "Open a solution to pin file groups for it"; return null; }
        var existing = FileGroups.Count == 0 ? "" : " Existing: " + string.Join(", ", FileGroups.Select(g => g.Name)) + ".";
        var name = (await _dialogs.PromptAsync(title, "Group name; a new name creates the group." + existing, _lastFileGroup.Length > 0 ? _lastFileGroup : "New feature"))?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        _lastFileGroup = name;
        var group = FileGroups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (group == null) { group = new(name, []); FileGroups.Add(group); }
        group.IsExpanded = true;
        return group;
    }
    private async Task AddToFileGroupAsync(string title, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { Status = "Nothing to add to a file group"; return; }
        if (await AskForFileGroupAsync(title) is not { } group) return;
        foreach (var path in paths) group.Add(path);
        await SaveFileGroupsAsync();
        Status = $"{group.Name}: {group.Summary}";
        ShowToolCommand.Execute("FileGroups");
    }
    [RelayCommand] private Task AddActiveToFileGroupAsync() => GuardAsync(() => AddToFileGroupAsync("Add file to group", ActiveDocument == null ? [] : [ActiveDocument.State.Path]));
    [RelayCommand] private Task AddOpenTabsToFileGroupAsync() => GuardAsync(() => AddToFileGroupAsync("Pin open tabs as a group", Documents.Select(d => d.State.Path).ToArray()));
    [RelayCommand] private Task AddSelectedToFileGroupAsync() => GuardAsync(() => AddToFileGroupAsync("Add to file group",
        SelectedNode is { Kind: ExplorerNodeKind.File, Path.Length: > 0 } node ? [node.Path] : []));

    /// <summary>Opens every file of the group that still exists, leaving other tabs open.</summary>
    [RelayCommand] private Task OpenFileGroupAsync(FileGroupViewModel? group) => group == null ? Task.CompletedTask : GuardAsync(async () =>
    {
        var missing = 0;
        foreach (var entry in group.Files.ToArray())
            if (entry.Exists) await OpenDocumentAsync(entry.Path); else missing++;
        Status = missing == 0 ? $"Opened {group.Name}" : $"Opened {group.Name}; {missing} file(s) no longer exist";
    });
    /// <summary>Closes tabs outside the group, asking about unsaved ones, then opens the group.</summary>
    [RelayCommand] private Task SwitchToFileGroupAsync(FileGroupViewModel? group) => group == null ? Task.CompletedTask : GuardAsync(async () =>
    {
        var keep = group.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var document in Documents.Where(d => !keep.Contains(d.State.Path)).ToArray())
            if (!await CloseDocumentAsync(document)) return;
        await OpenFileGroupCommand.ExecuteAsync(group);
    });
    [RelayCommand] private Task OpenFileGroupEntryAsync(FileGroupEntry? entry) => entry is not { Exists: true } ? Task.CompletedTask : GuardAsync(() => OpenDocumentAsync(entry.Path));
    [RelayCommand] private Task RemoveFileGroupEntryAsync(FileGroupEntry? entry) => entry == null ? Task.CompletedTask : GuardAsync(async () =>
    {
        entry.Group.Remove(entry);
        await SaveFileGroupsAsync();
    });
    [RelayCommand] private Task RenameFileGroupAsync(FileGroupViewModel? group) => group == null ? Task.CompletedTask : GuardAsync(async () =>
    {
        var name = (await _dialogs.PromptAsync("Rename file group", "New name", group.Name))?.Trim();
        if (string.IsNullOrEmpty(name) || name == group.Name) return;
        if (FileGroups.Any(g => g != group && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Status = $"A group named {name} already exists"; return; }
        group.Name = name;
        await SaveFileGroupsAsync();
    });
    [RelayCommand] private Task DeleteFileGroupAsync(FileGroupViewModel? group) => group == null ? Task.CompletedTask : GuardAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Delete file group", $"Delete the group {group.Name}? Its files are not changed.")) return;
        FileGroups.Remove(group);
        await SaveFileGroupsAsync();
    });

    private IEnumerable<PaletteEntry> FileGroupCommands()
    {
        yield return Command("View", "File groups", ShowToolCommand, "FileGroups", icon: "Pin", detail: "Pinned sets of files for a feature");
        yield return Command("Window", "Pin open tabs as a file group…", AddOpenTabsToFileGroupCommand, icon: "Pin");
        yield return Command("Window", "Add file to a file group…", AddActiveToFileGroupCommand, icon: "Pin");
        foreach (var group in FileGroups)
        {
            yield return new PaletteEntry("Open file group: " + group.Name, group.Summary, () => OpenFileGroupCommand.ExecuteAsync(group), "File groups", Icon: "Pin");
            yield return new PaletteEntry("Switch to file group: " + group.Name, "Close other tabs, then open " + group.Summary, () => SwitchToFileGroupCommand.ExecuteAsync(group), "File groups", Icon: "Pin");
        }
    }
}
