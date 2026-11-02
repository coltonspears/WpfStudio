using System.IO;
using System.Text.RegularExpressions;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Workbench;

/// <summary>
/// Finds the files that belong with a source file: the conventional view / code-behind / view-model trio by name,
/// and views and view models linked through a declared DataContext in the WPF index. Pure: callers supply the
/// candidate files and how to read text, so it is testable without a workspace.
/// </summary>
public static partial class RelatedFileLocator
{
    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    /// <summary>Conventional pairs, in the order Switch view ⇄ view model cycles through them.</summary>
    public static List<string> Conventional(string path, IEnumerable<string> candidateFiles, Func<string, bool> exists)
    {
        var files = candidateFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new List<string>();
        void AddName(string name)
        {
            candidates.Add(Path.Combine(Path.GetDirectoryName(path)!, name));
            candidates.AddRange(files.Where(f => Path.GetFileName(f).Equals(name, IgnoreCase)));
        }
        if (path.EndsWith(".xaml.cs", IgnoreCase))
        {
            var view = Path.GetFileName(path)[..^8];
            AddName(view + "Model.cs"); AddName(view + "ViewModel.cs"); AddName(view + ".xaml");
        }
        else if (path.EndsWith(".xaml", IgnoreCase))
        {
            var view = Path.GetFileNameWithoutExtension(path);
            AddName(view + ".xaml.cs"); AddName(view + "Model.cs"); AddName(view + "ViewModel.cs");
        }
        else if (path.EndsWith("ViewModel.cs", IgnoreCase))
        {
            var stem = Path.GetFileName(path)[..^12];
            AddName(stem + ".xaml"); AddName(stem + "View.xaml");
        }
        return candidates.Where(p => !p.Equals(path, IgnoreCase) && exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Views and view models linked through a declared DataContext, found in the WPF index (at most four).</summary>
    public static List<string> DataContext(string path, WpfIndexSnapshot? index, Func<string, string?> openText)
    {
        var results = new List<string>();
        if (index == null) return results;
        if (path.EndsWith(".xaml", IgnoreCase))
        {
            var text = openText(path) ?? (index.Texts.TryGetValue(path, out var indexed) ? indexed : null);
            if (text != null && DataContextType(text) is { } type)
                results.AddRange(index.Items.Where(i => i.Path.EndsWith(".cs", IgnoreCase) && (i.Name == type || i.Name.EndsWith("." + type, StringComparison.Ordinal))).Select(i => i.Path));
        }
        else if (path.EndsWith(".cs", IgnoreCase) && index.Texts.TryGetValue(path, out var source))
        {
            var types = TypeDeclaration().Matches(source).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var (xamlPath, xaml) in index.Texts.Where(t => t.Key.EndsWith(".xaml", IgnoreCase)))
                if (DataContextType(xaml) is { } type && types.Contains(type)) results.Add(xamlPath);
        }
        return results.Where(p => !p.Equals(path, IgnoreCase) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
    }

    /// <summary>The type named by a design-time or element DataContext declaration, without its namespace prefix.</summary>
    public static string? DataContextType(string xaml)
    {
        var match = DesignInstance().Match(xaml);
        if (!match.Success) match = ElementDataContext().Match(xaml);
        return match.Success ? match.Groups["type"].Value : null;
    }

    public static string RoleFor(string source, string related)
    {
        if (related.EndsWith(".xaml.cs", IgnoreCase)) return "Code-behind";
        if (related.EndsWith(".xaml", IgnoreCase)) return "View";
        if (related.EndsWith(".cs", IgnoreCase)) return source.EndsWith(".xaml.cs", IgnoreCase) || source.EndsWith(".xaml", IgnoreCase) ? "View model" : "Related";
        return "Related";
    }

    /// <summary>The file's folder relative to the workspace, starting with the workspace folder's name; the full folder outside it.</summary>
    public static string RelativeDirectory(string path, string workspaceRoot)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        if (workspaceRoot.Length > 0 && (directory.Equals(workspaceRoot, IgnoreCase) || directory.StartsWith(workspaceRoot + Path.DirectorySeparatorChar, IgnoreCase)))
        {
            var relative = Path.GetRelativePath(workspaceRoot, directory);
            return relative == "." ? Path.GetFileName(workspaceRoot) : Path.Combine(Path.GetFileName(workspaceRoot), relative);
        }
        return directory;
    }

    /// <summary>A breadcrumb for the tab header: at most the last three folders, prefixed with an ellipsis when deeper.</summary>
    public static string LocationFor(string path, string workspaceRoot)
    {
        var parts = RelativeDirectory(path, workspaceRoot).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 4) parts = ["…", .. parts[^3..]];
        return string.Join("  ›  ", parts);
    }

    [GeneratedRegex(@"\b(?:class|record)\s+(\w+)")] private static partial Regex TypeDeclaration();
    [GeneratedRegex(@"d:DataContext\s*=\s*[""']\{d:DesignInstance\s+(?:Type\s*=\s*)?(?:\w+:)?(?<type>\w+)")] private static partial Regex DesignInstance();
    [GeneratedRegex(@"<\w+\.DataContext\s*>\s*<(?:\w+:)?(?<type>\w+)\b")] private static partial Regex ElementDataContext();
}
