using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

public enum ExplorerNodeKind { File, Folder, Solution, Project, Dependencies, DependencyGroup, Dependency }

public sealed partial class ExplorerNode : ObservableObject
{
    private Func<IEnumerable<ExplorerNode>>? _load;
    private readonly string? _iconKind;
    public ExplorerNode(string name, string path, bool isFolder, Func<IEnumerable<ExplorerNode>>? load = null, string? projectPath = null)
        : this(name, path, isFolder ? ExplorerNodeKind.Folder : ExplorerNodeKind.File, load, projectPath) { }
    public ExplorerNode(string name, string path, ExplorerNodeKind kind, Func<IEnumerable<ExplorerNode>>? load = null, string? projectPath = null, string? detail = null, ProjectDependency? dependency = null, string? iconKind = null)
    {
        Name = name; Path = path; Kind = kind; _load = load; ProjectPath = projectPath; Detail = detail ?? ""; Dependency = dependency; _iconKind = iconKind ?? dependency?.Kind;
        if (load != null) Children.Add(new ExplorerNode("Loading…", "", false));
    }
    public string Name { get; }
    public string Path { get; }
    public string? ProjectPath { get; }
    public ExplorerNodeKind Kind { get; }
    /// <summary>Muted text after the name, such as a package version.</summary>
    public string Detail { get; }
    public ProjectDependency? Dependency { get; }
    public bool IsFolder => Kind is not (ExplorerNodeKind.File or ExplorerNodeKind.Dependency);
    /// <summary>Solutions and projects can be built, rebuilt, cleaned and restored from their context menu.</summary>
    public bool CanBuild => Kind is ExplorerNodeKind.Solution or ExplorerNodeKind.Project;
    public string ToolTip => Path.Length > 0 ? Path : Detail.Length > 0 ? $"{Name} {Detail}" : Name;
    public string IconKind => _iconKind ?? Kind switch
    {
        ExplorerNodeKind.Solution => "Solution",
        ExplorerNodeKind.Dependencies => "Dependencies",
        _ => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
        {
            ".csproj" or ".sln" or ".slnx" => "Project",
            _ when IsFolder => "Folder",
            ".cs" => "CSharp", ".xaml" => "Xaml", ".sql" => "Sql",
            ".png" or ".jpg" or ".jpeg" or ".ico" or ".svg" or ".ttf" or ".otf" => "Asset", _ => "File"
        }
    };
    /// <summary>Identifies the same node in a rebuilt tree, so a reload keeps what was expanded and selected.</summary>
    public string Key => $"{Kind}|{ProjectPath}|{Path}|{Name}";
    public ObservableCollection<ExplorerNode> Children { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _load == null) return;
        var factory = _load; _load = null;
        Children.Clear(); foreach (var item in factory()) Children.Add(item);
    }

    public static ExplorerNode ForSolution(string solutionPath, IReadOnlyList<ExplorerNode> projects)
    {
        var node = new ExplorerNode($"Solution '{System.IO.Path.GetFileNameWithoutExtension(solutionPath)}'", solutionPath, ExplorerNodeKind.Solution,
            () => projects, detail: projects.Count == 1 ? "1 project" : $"{projects.Count} projects");
        node.IsExpanded = true;
        return node;
    }

    public static ExplorerNode ForProject(WorkspaceProject project, bool nestRelated = true)
    {
        var files = project.Files.Where(f => !f.IsGenerated).Select(f => f.Path).Append(project.ProjectPath).ToArray();
        return new(project.Name, project.ProjectPath, ExplorerNodeKind.Project, () =>
        {
            var children = FromFiles(System.IO.Path.GetDirectoryName(project.ProjectPath)!, files, project.ProjectPath, nestRelated);
            return project.Dependencies is { Count: > 0 } dependencies ? children.Prepend(ForDependencies(project, dependencies)) : children;
        }, project.ProjectPath);
    }

    /// <summary>
    /// The project with only the files and dependencies whose name contains <paramref name="filter"/>, or null when nothing matches.
    /// A filter with a path separator matches the path relative to the project folder instead.
    /// </summary>
    public static ExplorerNode? ForFilteredProject(WorkspaceProject project, string filter, bool nestRelated = true)
    {
        var root = System.IO.Path.GetDirectoryName(project.ProjectPath)!;
        var byPath = filter.Contains('/') || filter.Contains('\\');
        var needle = byPath ? filter.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar) : filter;
        bool Matches(string path) => (byPath ? System.IO.Path.GetRelativePath(root, path) : System.IO.Path.GetFileName(path)).Contains(needle, StringComparison.OrdinalIgnoreCase);
        var files = project.Files.Where(f => !f.IsGenerated).Select(f => f.Path).Append(project.ProjectPath).Where(Matches).ToArray();
        ProjectDependency[] dependencies = byPath ? [] : (project.Dependencies ?? []).Where(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0 && dependencies.Length == 0) return null;
        return new(project.Name, project.ProjectPath, ExplorerNodeKind.Project, () =>
        {
            var children = FromFiles(root, files, project.ProjectPath, nestRelated);
            return dependencies.Length > 0 ? children.Prepend(ForDependencies(project, dependencies)) : children;
        }, project.ProjectPath, detail: files.Length == 1 ? "1 match" : $"{files.Length} matches");
    }

    /// <summary>Expands every node that has children, loading them, so a filtered tree shows all its matches.</summary>
    public static void ExpandAll(IEnumerable<ExplorerNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Count == 0) continue;
            node.IsExpanded = true;
            ExpandAll(node.Children);
        }
    }

    /// <summary>Dependency kinds in the order Visual Studio lists them.</summary>
    private static readonly (string Kind, string Name)[] DependencyGroups = [("Assembly", "Assemblies"), ("Framework", "Frameworks"), ("Package", "Packages"), ("Project", "Projects")];
    /// <summary>SDK-style projects call their references Dependencies; legacy projects call them References, as Visual Studio does.</summary>
    private static ExplorerNode ForDependencies(WorkspaceProject project, IReadOnlyList<ProjectDependency> dependencies) =>
        new(project.IsSdkStyle ? "Dependencies" : "References", "", ExplorerNodeKind.Dependencies, () =>
            DependencyGroups.Select(group => (group.Kind, group.Name, Items: dependencies.Where(d => d.Kind == group.Kind).ToArray()))
                .Where(group => group.Items.Length > 0)
                .Select(group => new ExplorerNode(group.Name, "", ExplorerNodeKind.DependencyGroup, () => group.Items.Select(dependency =>
                    new ExplorerNode(dependency.Name, dependency.Path ?? "", ExplorerNodeKind.Dependency, projectPath: project.ProjectPath, detail: dependency.Version, dependency: dependency)),
                    project.ProjectPath, iconKind: group.Kind)),
            project.ProjectPath);

    /// <summary>Keys of the expanded nodes and of the selected node, in a tree whose children have been loaded.</summary>
    public static (HashSet<string> Expanded, string? Selected) CaptureState(IEnumerable<ExplorerNode> nodes)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? selected = null;
        void Visit(IEnumerable<ExplorerNode> level)
        {
            foreach (var node in level)
            {
                if (node.IsSelected) selected = node.Key;
                if (node.IsExpanded) { expanded.Add(node.Key); Visit(node.Children); }
            }
        }
        Visit(nodes);
        return (expanded, selected);
    }

    /// <summary>Expands and selects the nodes captured from an earlier tree; expanding loads their children.</summary>
    public static void RestoreState(IEnumerable<ExplorerNode> nodes, (HashSet<string> Expanded, string? Selected) state)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = state.Expanded.Contains(node.Key);
            if (node.Key == state.Selected) node.IsSelected = true;
            if (node.IsExpanded) RestoreState(node.Children, state);
        }
    }
    public static IEnumerable<ExplorerNode> FromFiles(string root, IEnumerable<string> paths, string? projectPath = null, bool nestRelated = true)
    {
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var groups = files.GroupBy(path => { var relative = System.IO.Path.GetRelativePath(root, path); var split = relative.IndexOf(System.IO.Path.DirectorySeparatorChar); return split < 0 ? "" : relative[..split]; });
        foreach (var group in groups.Where(g => g.Key != "").OrderBy(g => g.Key))
        {
            var subRoot = System.IO.Path.Combine(root, group.Key); var subFiles = group.ToArray();
            yield return new ExplorerNode(group.Key, subRoot, true, () => FromFiles(subRoot, subFiles, projectPath, nestRelated), projectPath);
        }
        var level = groups.Where(g => g.Key == "").SelectMany(g => g).OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        var nodes = level.ToDictionary(System.IO.Path.GetFileName, path => new ExplorerNode(System.IO.Path.GetFileName(path), path, false, projectPath: projectPath), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(nodes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var path in level)
        {
            var name = System.IO.Path.GetFileName(path);
            var parent = nestRelated ? NestingParent(name, names) : null;
            if (parent == null) yield return nodes[name];
            else nodes[parent].Children.Add(nodes[name]);
        }
    }

    private static readonly string[] DesignerOwners = [".resx", ".settings"];
    /// <summary>
    /// The file in the same folder that <paramref name="fileName"/> belongs under, the way Visual Studio nests files:
    /// MainWindow.xaml.cs under MainWindow.xaml, Resources.Designer.cs under Resources.resx, and ShellViewModel.Tools.cs or
    /// appsettings.Development.json under ShellViewModel.cs or appsettings.json. Null when the file stands on its own.
    /// </summary>
    /// <param name="siblings">The file names in the folder, compared without regard to case.</param>
    public static string? NestingParent(string fileName, IReadOnlySet<string> siblings)
    {
        bool Has(string candidate) => !candidate.Equals(fileName, StringComparison.OrdinalIgnoreCase) && siblings.Contains(candidate);
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        if (stem.Contains('.') && Has(stem)) return stem;
        if (stem.EndsWith(".Designer", StringComparison.OrdinalIgnoreCase))
            foreach (var owner in DesignerOwners) if (Has(stem[..^".Designer".Length] + owner)) return stem[..^".Designer".Length] + owner;
        for (var dot = stem.LastIndexOf('.'); dot > 0; dot = stem.LastIndexOf('.', dot - 1))
            if (Has(stem[..dot] + extension)) return stem[..dot] + extension;
        return null;
    }
}
public sealed record NavigationResult(string Path, int Line, int Column, string Text, int Start = 0,
    string? ExpectedTextHash = null, string? ProjectPath = null, string? ProjectName = null)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Location => $"{FileName}:{Line}" + (ProjectName is { Length: > 0 } ? $" · {ProjectName}" : "");
}
/// <summary>A quick-open file or command-palette entry.</summary>
public sealed record PaletteEntry(string Label, string Detail, Func<Task> Execute, string Category = "", string Shortcut = "", string Icon = "", string IconKind = "")
{
    public bool HasShortcut => Shortcut.Length > 0;
    public bool IsFile => IconKind.Length > 0;
}
/// <summary>A file related to the active document, such as its code-behind or view model.</summary>
public sealed record RelatedFile(string Role, string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string IconKind => WpfStudio.App.Controls.FileIconKindConverter.KindFor(Path);
}
