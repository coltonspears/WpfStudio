using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

public enum SearchScopeKind { Solution, CurrentProject, OpenDocuments, CurrentDocument, Project, Folder }

/// <summary>
/// Where find in files looks. <paramref name="Path"/> is the project file of a <see cref="SearchScopeKind.Project"/> and the
/// folder of a <see cref="SearchScopeKind.Folder"/>; the other kinds are worked out when the search runs, so "current" is
/// whatever is current then.
/// </summary>
public sealed record SearchScope(SearchScopeKind Kind, string Label, string? Path = null)
{
    public static SearchScope Solution { get; } = new(SearchScopeKind.Solution, "Entire solution");
    public static SearchScope CurrentProject { get; } = new(SearchScopeKind.CurrentProject, "Current project");
    public static SearchScope OpenDocuments { get; } = new(SearchScopeKind.OpenDocuments, "Open documents");
    public static SearchScope CurrentDocument { get; } = new(SearchScopeKind.CurrentDocument, "Current document");
    public static SearchScope ForProject(WorkspaceProject project) => new(SearchScopeKind.Project, project.Name, project.ProjectPath);
    /// <param name="relative">The folder as Solution Explorer shows it, from the workspace folder.</param>
    public static SearchScope ForFolder(string folder, string relative) => new(SearchScopeKind.Folder, "Folder: " + relative, folder);

    public string IconKind => Kind switch
    {
        SearchScopeKind.Solution => "Solution",
        SearchScopeKind.CurrentProject or SearchScopeKind.Project => "Project",
        SearchScopeKind.Folder => "Folder",
        _ => "File",
    };
    public string ToolTip => Kind switch
    {
        SearchScopeKind.Solution => "Every file of the open solution or project",
        SearchScopeKind.CurrentProject => "The project of the document being edited; with no document open, the project selected in Solution Explorer or else the startup project",
        SearchScopeKind.OpenDocuments => "The documents that are open in tabs, as they stand in their editors",
        SearchScopeKind.CurrentDocument => "The document being edited",
        _ => Path ?? Label,
    };
}

/// <summary>
/// A file type among a search's matches: how many lines of how many files matched, and a switch that lists only the chosen
/// types. <paramref name="changed"/> is told when the switch is flipped by hand, not when it is set as the type is listed.
/// </summary>
public sealed partial class SearchFileType(string extension, int files, int matches, bool atLeast, bool isSelected, Action<SearchFileType> changed) : ObservableObject
{
    /// <summary>Lower case with its dot; empty for files whose name has no extension.</summary>
    public string Extension { get; } = extension;
    public string Label => Extension.Length > 0 ? Extension : "no extension";
    public int Files { get; } = files;
    public int Matches { get; } = matches;
    /// <summary>The search stopped at its limit, so there may be more than <see cref="Matches"/>.</summary>
    public string CountText => Matches.ToString("N0") + (atLeast ? "+" : "");
    public string ToolTip => (Matches == 1 ? "1 match" : $"{CountText} matches") + (Files == 1 ? " in 1 file" : $" in {Files:N0} files") + $" of type {Label}. "
        + (IsSelected ? "Click to list the other types again." : "Click to list only this type; several can be chosen.");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ToolTip))] public partial bool IsSelected { get; set; } = isSelected;
    partial void OnIsSelectedChanged(bool value) => changed(this);
}
