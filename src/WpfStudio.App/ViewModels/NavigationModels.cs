using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

public sealed partial class ExplorerNode : ObservableObject
{
    private Func<IEnumerable<ExplorerNode>>? _load;
    public ExplorerNode(string name, string path, bool isFolder, Func<IEnumerable<ExplorerNode>>? load = null, string? projectPath = null)
    {
        Name = name; Path = path; IsFolder = isFolder; _load = load; ProjectPath = projectPath;
        if (load != null) Children.Add(new ExplorerNode("Loading…", "", false));
    }
    public string Name { get; }
    public string Path { get; }
    public string? ProjectPath { get; }
    public bool IsFolder { get; }
    public string IconKind => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
    {
        ".csproj" or ".sln" or ".slnx" => "Project",
        _ when IsFolder => "Folder",
        ".cs" => "CSharp", ".xaml" => "Xaml", ".sql" => "Sql",
        ".png" or ".jpg" or ".jpeg" or ".ico" or ".svg" or ".ttf" or ".otf" => "Asset", _ => "File"
    };
    public ObservableCollection<ExplorerNode> Children { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _load == null) return;
        var factory = _load; _load = null;
        Children.Clear(); foreach (var item in factory()) Children.Add(item);
    }
    public static IEnumerable<ExplorerNode> FromFiles(string root, IEnumerable<string> paths, string? projectPath = null)
    {
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var groups = files.GroupBy(path => { var relative = System.IO.Path.GetRelativePath(root, path); var split = relative.IndexOf(System.IO.Path.DirectorySeparatorChar); return split < 0 ? "" : relative[..split]; });
        foreach (var group in groups.Where(g => g.Key != "").OrderBy(g => g.Key))
        {
            var subRoot = System.IO.Path.Combine(root, group.Key); var subFiles = group.ToArray();
            yield return new ExplorerNode(group.Key, subRoot, true, () => FromFiles(subRoot, subFiles, projectPath), projectPath);
        }
        foreach (var path in groups.Where(g => g.Key == "").SelectMany(g => g).OrderBy(System.IO.Path.GetFileName)) yield return new ExplorerNode(System.IO.Path.GetFileName(path), path, false, projectPath: projectPath);
    }
}
public sealed record NavigationResult(string Path, int Line, int Column, string Text, int Start = 0)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Location => $"{FileName}:{Line}";
}
public sealed record PaletteEntry(string Label, string Detail, Func<Task> Execute);
