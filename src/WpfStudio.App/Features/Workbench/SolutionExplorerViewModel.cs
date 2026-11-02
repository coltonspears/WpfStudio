using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Workbench;

/// <summary>
/// The Solution Explorer tree: projects under their solution, a name filter that expands every match, and the
/// choice to nest code-behind and partial files under their main file.
/// </summary>
public sealed partial class SolutionExplorerViewModel : ObservableObject
{
    private readonly IWorkbenchHost _host;
    private readonly ObservableCollection<WorkspaceProject> _projects;
    private (HashSet<string> Expanded, string? Selected)? _stateBeforeFilter;

    /// <param name="projects">The loaded projects, owned by the shell; the tree is rebuilt from them on request.</param>
    public SolutionExplorerViewModel(IWorkbenchHost host, ObservableCollection<WorkspaceProject> projects)
    {
        _host = host; _projects = projects;
        Nodes.CollectionChanged += (_, _) => NotifyPlaceholders();
    }

    public ObservableCollection<ExplorerNode> Nodes { get; } = [];
    [ObservableProperty] public partial ExplorerNode? SelectedNode { get; set; }
    /// <summary>Shows only files whose name contains this text, with their folders expanded.</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "";
    /// <summary>Nests code-behind under its XAML (MainWindow.xaml.cs under MainWindow.xaml) and partial files under their main file.</summary>
    [ObservableProperty] public partial bool NestRelatedFiles { get; set; } = true;
    public bool IsFiltered => Filter.Trim().Length > 0;
    /// <summary>The "No workspace open" prompt; a filter with no matches shows its own message instead.</summary>
    public bool ShowPlaceholder => Nodes.Count == 0 && !IsFiltered;
    public bool ShowNoMatches => Nodes.Count == 0 && IsFiltered;
    private void NotifyPlaceholders() { OnPropertyChanged(nameof(ShowPlaceholder)); OnPropertyChanged(nameof(ShowNoMatches)); }

    partial void OnFilterChanged(string value)
    {
        var filtering = value.Trim().Length > 0;
        if (filtering && _stateBeforeFilter == null) _stateBeforeFilter = ExplorerNode.CaptureState(Nodes);
        OnPropertyChanged(nameof(IsFiltered)); NotifyPlaceholders();
        if (_host.WorkspacePath == null) return;
        if (filtering) { Rebuild(keepState: false); return; }
        var state = _stateBeforeFilter; _stateBeforeFilter = null;
        Rebuild(keepState: false);
        if (state is { } previous) ExplorerNode.RestoreState(Nodes, previous);
    }
    partial void OnNestRelatedFilesChanged(bool value) { if (_host.WorkspacePath != null) Rebuild(keepState: true); }
    [RelayCommand] private void ClearFilter() => Filter = "";
    [RelayCommand] private void ToggleNesting()
    {
        NestRelatedFiles = !NestRelatedFiles;
        _host.ReportStatus(NestRelatedFiles ? "Related files nested in Solution Explorer" : "Related files listed separately in Solution Explorer");
    }

    /// <summary>Lists loose files from a folder, as shown while the project model is still loading or when it has no projects.</summary>
    public void ShowFiles(string root, IEnumerable<string> files)
    {
        Nodes.Clear();
        foreach (var node in ExplorerNode.FromFiles(root, files, nestRelated: NestRelatedFiles)) Nodes.Add(node);
    }

    /// <summary>
    /// A solution appears as one node above its projects; an opened project file appears on its own. A reload, such as
    /// the one after each build, keeps the nodes that were expanded and selected.
    /// </summary>
    public void Rebuild(bool keepState)
    {
        var state = ExplorerNode.CaptureState(Nodes);
        Nodes.Clear();
        var filter = Filter.Trim();
        var projects = _projects.Select(project => filter.Length == 0 ? ExplorerNode.ForProject(project, NestRelatedFiles) : ExplorerNode.ForFilteredProject(project, filter, NestRelatedFiles))
            .OfType<ExplorerNode>().ToArray();
        if (_host.WorkspacePath is { } workspace && projects.Length > 0 && !Path.GetExtension(workspace).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            Nodes.Add(ExplorerNode.ForSolution(workspace, projects));
        else foreach (var project in projects) Nodes.Add(project);
        // A filtered tree shows every match; the unfiltered layout comes back when the filter is cleared.
        if (filter.Length > 0) ExplorerNode.ExpandAll(Nodes);
        else if (keepState) ExplorerNode.RestoreState(Nodes, state);
    }

    /// <summary>Empties the tree and forgets the filter, as when the solution closes or another one starts loading.</summary>
    public void Clear()
    {
        _stateBeforeFilter = null; Filter = "";
        Nodes.Clear();
    }
}
