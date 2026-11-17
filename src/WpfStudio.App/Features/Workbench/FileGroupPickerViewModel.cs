using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WpfStudio.App.Features.Workbench;

/// <summary>A file about to be added to a group.</summary>
public sealed record FileGroupCandidate(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string IconKind => WpfStudio.App.Controls.FileIconKindConverter.KindFor(Path);
}

/// <summary>A row of the file group picker: an existing group, or the new group the name box creates when <see cref="Group"/> is null.</summary>
public sealed record FileGroupChoice(FileGroupViewModel? Group, int AlreadyIncluded, int Adding)
{
    public bool IsNew => Group is null;
    public string Name => Group?.Name ?? "New group";
    public string Icon => IsNew ? "Plus" : "Pin";
    public string Detail => Group is null ? "Name it below"
        : AlreadyIncluded == 0 ? Group.Summary
        : AlreadyIncluded == Adding ? Group.Summary + " · has all of these"
        : $"{Group.Summary} · has {AlreadyIncluded} of these";
}

/// <summary>
/// Chooses the file group that files are added to: one of the existing groups, listed, or a new one named in place.
/// Nothing changes until <see cref="AddCommand"/> runs.
/// </summary>
public sealed partial class FileGroupPickerViewModel : ObservableObject
{
    private readonly Func<FileGroupPickerViewModel, Task> _add;
    private bool _naming;

    public FileGroupPickerViewModel(Func<FileGroupPickerViewModel, Task> add) => _add = add;

    [ObservableProperty] public partial bool IsOpen { get; private set; }
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<FileGroupCandidate> Files { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<FileGroupChoice> Choices { get; private set; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNewGroup), nameof(AddLabel))] public partial FileGroupChoice? Selected { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddLabel))] public partial string NewGroupName { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    /// <summary>Asks the view to focus the name box; the view clears it once done.</summary>
    [ObservableProperty] public partial bool FocusNewName { get; set; }

    public bool IsNewGroup => Selected?.IsNew == true;
    public string FilesSummary => Files.Count == 1 ? "1 file" : $"{Files.Count} files";
    public string AddLabel => Selected is { Group: { } group } ? "Add to " + group.Name : "Create group";
    /// <summary>The group the files go to: the chosen group's name, or the new name.</summary>
    public string GroupName => Selected?.Group?.Name ?? NewGroupName.Trim();

    /// <summary>Lists <paramref name="groups"/> and shows the picker, with the group named <paramref name="preferred"/> chosen, or a new group.</summary>
    public void Open(string title, IReadOnlyList<string> paths, IEnumerable<FileGroupViewModel> groups, string? preferred)
    {
        Title = title;
        Files = paths.Select(path => new FileGroupCandidate(path)).ToArray();
        OnPropertyChanged(nameof(FilesSummary));
        var adding = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        Choices = [.. groups.Select(group => new FileGroupChoice(group, group.Files.Count(file => adding.Contains(file.Path)), adding.Count)), new FileGroupChoice(null, 0, adding.Count)];
        _naming = true;
        try { NewGroupName = ""; Selected = Choices.FirstOrDefault(choice => preferred is { Length: > 0 } && choice.Group?.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase) == true) ?? Choices[^1]; }
        finally { _naming = false; }
        Error = "";
        // Enter adds to the chosen group; typing names a new one instead.
        FocusNewName = true;
        IsOpen = true;
    }

    partial void OnSelectedChanged(FileGroupChoice? value)
    {
        Error = "";
        if (!_naming && value?.IsNew == true) FocusNewName = true;
    }

    partial void OnNewGroupNameChanged(string value)
    {
        Error = "";
        if (value.Length == 0 || Selected?.IsNew == true || Choices.Count == 0) return;
        // Typing a name chooses the new group, without moving the caret out of the box.
        _naming = true;
        try { Selected = Choices[^1]; }
        finally { _naming = false; }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!IsOpen) return;
        if (Selected is null) { Error = "Choose a group, or name a new one."; return; }
        if (GroupName.Length == 0) { Error = "Name the new group."; FocusNewName = true; return; }
        IsOpen = false;
        await _add(this);
    }

    [RelayCommand] private void Cancel() => IsOpen = false;
}
