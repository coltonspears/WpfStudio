using System.Text.RegularExpressions;
using System.Xml.Linq;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Wpf;

public enum ScaffoldKind { ViewAndViewModel, UserControl, ResourceDictionary, Converter, ViewModel, ObservableProperty, RelayCommand }

public sealed partial class ScaffoldingService
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();
    private static readonly HashSet<string> Keywords = new("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while".Split(' '), StringComparer.Ordinal);
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public async Task<IReadOnlyList<FileChange>> CreateAsync(string projectPath, string directory, string name, ScaffoldKind kind, bool addDataTemplate = false, CancellationToken token = default)
    {
        if (!Identifier().IsMatch(name) || Keywords.Contains(name)) throw new ArgumentException("Enter a valid C# identifier that is not a reserved keyword.");
        projectPath = Path.GetFullPath(projectPath);
        var projectText = await File.ReadAllTextAsync(projectPath, token);
        var project = XDocument.Parse(projectText, LoadOptions.PreserveWhitespace);
        var rootDirectory = Path.GetDirectoryName(projectPath)!;
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(rootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(directory, rootDirectory, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Create files inside the selected project.");
        var context = await ScaffoldProjectContext.TryEvaluateAsync(projectPath, token);
        var rootNamespace = context?.RootNamespace ?? project.Descendants().FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value ?? Path.GetFileNameWithoutExtension(projectPath).Replace('-', '_');
        if (string.IsNullOrWhiteSpace(rootNamespace)) rootNamespace = Path.GetFileNameWithoutExtension(projectPath).Replace('-', '_');
        var relative = Path.GetRelativePath(rootDirectory, directory);
        var ns = rootNamespace + (relative == "." ? "" : "." + string.Join('.', relative.Split(Path.DirectorySeparatorChar).Select(SafeIdentifier)));
        if (ns.Split('.').Any(part => !Identifier().IsMatch(part) || Keywords.Contains(part))) throw new ArgumentException("The evaluated root namespace must contain valid C# identifiers.");
        var partialProperties = context?.SupportsPartialProperties == true;
        var changes = new List<FileChange>();
        void Add(string file, string text) { var path = Path.Combine(directory, file); if (File.Exists(path)) throw new IOException($"{file} already exists."); changes.Add(new(path, "", text.Replace("\n", Environment.NewLine), $"Create {file}")); }
        var vmName = name.EndsWith("ViewModel") ? name : name + "ViewModel";
        if (kind is ScaffoldKind.ViewAndViewModel or ScaffoldKind.UserControl)
        {
            Add(name + ".xaml", $"<UserControl x:Class=\"{ns}.{name}\"\n    xmlns=\"{PresentationNamespace}\"\n    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n    xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"\n    xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"\n    mc:Ignorable=\"d\" d:DesignWidth=\"800\" d:DesignHeight=\"450\">\n    <Grid>\n    </Grid>\n</UserControl>\n");
            Add(name + ".xaml.cs", $"using System.Windows.Controls;\n\nnamespace {ns};\n\npublic partial class {name} : UserControl\n{{\n    public {name}() => InitializeComponent();\n}}\n");
        }
        if (kind is ScaffoldKind.ViewAndViewModel or ScaffoldKind.ViewModel)
        {
            Add(vmName + ".cs", $"using System.Threading;\nusing System.Threading.Tasks;\nusing CommunityToolkit.Mvvm.ComponentModel;\nusing CommunityToolkit.Mvvm.Input;\n\nnamespace {ns};\n\npublic partial class {vmName} : ObservableObject\n{{\n{PropertyTemplate("Title", partialProperties)}\n    [RelayCommand]\n    private async Task RefreshAsync(CancellationToken cancellationToken)\n    {{\n        await Task.CompletedTask;\n    }}\n}}\n");
            if (context?.HasToolkit != true && !project.Descendants().Any(e => e.Name.LocalName == "PackageReference" && (string?)e.Attribute("Include") == "CommunityToolkit.Mvvm"))
            {
                var group = new XElement(project.Root!.Name.Namespace + "ItemGroup");
                var package = new XElement(project.Root.Name.Namespace + "PackageReference", new XAttribute("Include", "CommunityToolkit.Mvvm"));
                var centralPath = FindCentralPackages(rootDirectory);
                if (context?.ManageVersionsCentrally == true && centralPath == null) throw new InvalidOperationException("This project imports central package management from a custom location. Add CommunityToolkit.Mvvm to its central versions file first.");
                if (centralPath != null && context?.ManageVersionsCentrally == true)
                {
                    var centralText = await File.ReadAllTextAsync(centralPath, token);
                    var central = XDocument.Parse(centralText, LoadOptions.PreserveWhitespace);
                    if (!central.Descendants().Any(e => e.Name.LocalName == "PackageVersion" && (string?)e.Attribute("Include") == "CommunityToolkit.Mvvm"))
                    {
                        var centralNamespace = central.Root!.Name.Namespace;
                        central.Root.Add(new XElement(centralNamespace + "ItemGroup", new XElement(centralNamespace + "PackageVersion", new XAttribute("Include", "CommunityToolkit.Mvvm"), new XAttribute("Version", "8.4.2"))));
                        changes.Add(new(centralPath, centralText, central.ToString(), "Add CommunityToolkit.Mvvm central package version"));
                    }
                }
                else package.Add(new XAttribute("Version", "8.4.2"));
                group.Add(package); project.Root.Add(group);
                changes.Add(new(projectPath, projectText, project.ToString(), "Add CommunityToolkit.Mvvm dependency"));
            }
        }
        if (kind == ScaffoldKind.ResourceDictionary)
            Add(name + ".xaml", $"<ResourceDictionary xmlns=\"{PresentationNamespace}\"\n    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n    <SolidColorBrush x:Key=\"{name}Brush\" Color=\"#407FA3\" />\n</ResourceDictionary>\n");
        if (kind == ScaffoldKind.Converter)
            Add(name + ".cs", $"using System;\nusing System.Globalization;\nusing System.Windows;\nusing System.Windows.Data;\n\nnamespace {ns};\n\npublic sealed class {name} : IValueConverter\n{{\n    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value;\n    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;\n}}\n");
        if (addDataTemplate && kind == ScaffoldKind.ViewAndViewModel)
            Add(name + "Templates.xaml", $"<ResourceDictionary xmlns=\"{PresentationNamespace}\"\n    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n    xmlns:local=\"clr-namespace:{ns}\">\n    <DataTemplate DataType=\"{{x:Type local:{vmName}}}\">\n        <local:{name} />\n    </DataTemplate>\n</ResourceDictionary>\n");
        var explicitItems = new XElement(project.Root!.Name.Namespace + "ItemGroup");
        foreach (var change in changes.Where(c => c.Before.Length == 0))
        {
            if (change.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && context?.DefaultCompileItems == false)
                explicitItems.Add(new XElement(project.Root.Name.Namespace + "Compile", new XAttribute("Include", Path.GetRelativePath(rootDirectory, change.Path))));
            if (change.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && context?.DefaultPageItems == false)
                explicitItems.Add(new XElement(project.Root.Name.Namespace + "Page", new XAttribute("Include", Path.GetRelativePath(rootDirectory, change.Path))));
        }
        if (explicitItems.HasElements)
        {
            project.Root.Add(explicitItems);
            changes.RemoveAll(c => c.Path == projectPath);
            changes.Add(new(projectPath, projectText, project.ToString(), "Update project dependencies and explicit compile items"));
        }
        return changes;
    }
    private static string SafeIdentifier(string value)
    {
        var safe = Regex.Replace(value, "[^A-Za-z0-9_]", "_");
        if (safe.Length == 0 || char.IsDigit(safe[0]) || Keywords.Contains(safe)) safe = "_" + safe;
        return safe;
    }
    public static string PropertyTemplate(string name, bool partialProperties) => partialProperties
        ? $"    [ObservableProperty]\n    public partial string {name} {{ get; set; }} = string.Empty;\n"
        : $"    private string _{char.ToLowerInvariant(name[0])}{name[1..]} = string.Empty;\n    public string {name}\n    {{\n        get => _{char.ToLowerInvariant(name[0])}{name[1..]};\n        set => SetProperty(ref _{char.ToLowerInvariant(name[0])}{name[1..]}, value);\n    }}\n";
    public static string? FindCentralPackages(string directory)
    {
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "Directory.Packages.props");
            if (File.Exists(path)) return path;
        }
        return null;
    }
    public static string PackUri(string projectPath, string assetPath, string? assemblyName = null, string? logicalPath = null)
    {
        var relative = (string.IsNullOrWhiteSpace(logicalPath) ? Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, Path.GetFullPath(assetPath)) : logicalPath).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is ".." or "." or "")) throw new ArgumentException("Linked assets need their evaluated logical project resource path to create a pack URI. Refresh the workspace after editing Link metadata.");
        assemblyName ??= XDocument.Load(projectPath).Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value ?? Path.GetFileNameWithoutExtension(projectPath);
        return $"pack://application:,,,/{assemblyName};component/{string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))}";
    }
    public static FileChange SetBuildAction(string projectPath, string projectText, string assetPath, string buildAction)
    {
        if (buildAction is not ("Resource" or "Content" or "None" or "Page")) throw new ArgumentException("Invalid build action.");
        var project = XDocument.Parse(projectText, LoadOptions.PreserveWhitespace);
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        assetPath = Path.GetFullPath(assetPath);
        var relative = Path.GetRelativePath(projectDirectory, assetPath);
        var ns = project.Root!.Name.Namespace;
        bool MatchesAsset(XElement item)
        {
            var identity = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update") ?? (string?)item.Attribute("Remove");
            if (identity == null || identity.IndexOfAny(['*', '?', ';']) >= 0 || identity.Contains("$(", StringComparison.Ordinal)) return false;
            try { return string.Equals(Path.GetFullPath(identity.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar), projectDirectory), assetPath, StringComparison.OrdinalIgnoreCase); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
        var metadataUpdates = new List<XElement>();
        foreach (var item in project.Descendants().Where(e => e.Name.LocalName is "Resource" or "Content" or "None" or "Page").Where(MatchesAsset).ToArray())
        {
            if (item.Attribute("Remove") == null)
            {
                // Include once below, then replay metadata in declaration order. Keeping updates separate
                // preserves conditional CopyToOutputDirectory/Link values without creating duplicate items.
                var update = new XElement(ns + buildAction, new XAttribute("Update", relative));
                foreach (var attribute in item.Attributes().Where(a => a.Name.LocalName is not ("Include" or "Update" or "Remove" or "Exclude" or "KeepDuplicates" or "Condition"))) update.Add(new XAttribute(attribute));
                foreach (var node in item.Nodes()) update.Add(node is XElement element ? new XElement(element) : node);
                var conditions = item.AncestorsAndSelf().Attributes("Condition").Select(a => a.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Reverse().ToArray();
                if (conditions.Length > 0) update.SetAttributeValue("Condition", string.Join(" And ", conditions.Select(condition => "(" + condition + ")")));
                if (update.HasElements || update.Attributes().Any(a => a.Name.LocalName is not ("Update" or "Condition" or "Label"))) metadataUpdates.Add(update);
            }
            item.Remove();
        }
        var group = new XElement(ns + "ItemGroup");
        foreach (var action in new[] { "None", "Page", "Resource", "Content" }.Where(a => a != buildAction)) group.Add(new XElement(ns + action, new XAttribute("Remove", relative)));
        // None and Page have SDK default globs; remove then explicitly include to avoid duplicates.
        group.Add(new XElement(ns + buildAction, new XAttribute("Remove", relative)));
        group.Add(new XElement(ns + buildAction, new XAttribute("Include", relative)));
        group.Add(metadataUpdates);
        project.Root.Add(group);
        return new(projectPath, projectText, project.ToString(), $"Set {relative} build action to {buildAction}");
    }
}
