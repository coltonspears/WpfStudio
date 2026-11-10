using System.Text;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Wpf;

/// <summary>Project file edits. Each one works on text parsed with its whitespace kept and hands back a <see cref="FileChange"/> for the caller to review and apply.</summary>
public sealed class ProjectFileEditor
{
    private const string ToolkitPackage = "CommunityToolkit.Mvvm";
    private const string ToolkitVersion = "8.4.2";
    private readonly string _projectPath;
    private readonly string _projectText;
    private readonly XDocument _project;
    private readonly string _rootDirectory;

    public ProjectFileEditor(string projectPath, string projectText)
    {
        _projectPath = projectPath; _projectText = projectText;
        _project = XDocument.Parse(projectText, LoadOptions.PreserveWhitespace);
        _rootDirectory = Path.GetDirectoryName(projectPath)!;
    }

    /// <summary>The RootNamespace the file itself declares, for when MSBuild could not evaluate the project.</summary>
    public string? DeclaredRootNamespace => _project.Descendants().FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value;

    private FileChange Change(string description) => new(_projectPath, _projectText, Serialize(_project, _projectText), description);

    /// <summary>
    /// The edited document as text that differs from <paramref name="original"/> only by the edit. <c>XDocument.ToString()</c>
    /// alone drops the XML declaration, and writes every line break in the operating system's style whatever the file used.
    /// </summary>
    private static string Serialize(XDocument document, string original)
    {
        var builder = new StringBuilder(original.Length + 256);
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        // Parsing turned every line break into a line feed; the file's own style is put back.
        if (original.Contains("\r\n", StringComparison.Ordinal)) builder.Replace("\r\n", "\n").Replace("\n", "\r\n");
        // The declaration is kept as written: a writer into text would declare UTF-16 whatever the file is saved in.
        var declarationEnd = original.StartsWith("<?xml", StringComparison.Ordinal) ? original.IndexOf("?>", StringComparison.Ordinal) : -1;
        return declarationEnd < 0 ? builder.ToString() : builder.Insert(0, original.AsSpan(0, declarationEnd + 2)).ToString();
    }

    /// <summary>The edits that make the toolkit available, or none when the project already has it.</summary>
    public async Task<IReadOnlyList<FileChange>> AddToolkitAsync(ScaffoldProjectContext? context, CancellationToken token)
    {
        if (context?.HasToolkit == true || _project.Descendants().Any(e => e.Name.LocalName == "PackageReference" && IsToolkitPackage(e))) return [];
        var changes = new List<FileChange>();
        var group = new XElement(_project.Root!.Name.Namespace + "ItemGroup");
        var package = new XElement(_project.Root.Name.Namespace + "PackageReference", new XAttribute("Include", ToolkitPackage));
        var centralPath = FindCentralPackages(_rootDirectory);
        if (context?.ManageVersionsCentrally == true && centralPath == null) throw new InvalidOperationException($"This project imports central package management from a custom location. Add {ToolkitPackage} to its central versions file first.");
        if (centralPath != null && context?.ManageVersionsCentrally == true)
        {
            var centralText = await File.ReadAllTextAsync(centralPath, token);
            var central = XDocument.Parse(centralText, LoadOptions.PreserveWhitespace);
            if (!central.Descendants().Any(e => e.Name.LocalName == "PackageVersion" && IsToolkitPackage(e)))
            {
                var centralNamespace = central.Root!.Name.Namespace;
                central.Root.Add(new XElement(centralNamespace + "ItemGroup", new XElement(centralNamespace + "PackageVersion", new XAttribute("Include", ToolkitPackage), new XAttribute("Version", ToolkitVersion))));
                changes.Add(new(centralPath, centralText, Serialize(central, centralText), $"Add {ToolkitPackage} central package version"));
            }
        }
        else package.Add(new XAttribute("Version", ToolkitVersion));
        group.Add(package); _project.Root.Add(group);
        changes.Add(Change($"Add {ToolkitPackage} dependency"));
        return changes;
    }

    /// <summary>Lists new files explicitly where the project turned its default globs off. Null when none are affected; otherwise the change also carries any earlier edit to the project.</summary>
    public FileChange? AddExplicitItems(IEnumerable<string> newFiles, ScaffoldProjectContext? context)
    {
        var ns = _project.Root!.Name.Namespace;
        var explicitItems = new XElement(ns + "ItemGroup");
        foreach (var path in newFiles)
        {
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && context?.DefaultCompileItems == false)
                explicitItems.Add(new XElement(ns + "Compile", new XAttribute("Include", Path.GetRelativePath(_rootDirectory, path))));
            if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && context?.DefaultPageItems == false)
                explicitItems.Add(new XElement(ns + "Page", new XAttribute("Include", Path.GetRelativePath(_rootDirectory, path))));
        }
        if (!explicitItems.HasElements) return null;
        _project.Root.Add(explicitItems);
        return Change("Update project dependencies and explicit compile items");
    }

    /// <summary>NuGet package ids are case-insensitive, so a project that references <c>communitytoolkit.mvvm</c> already has the toolkit.</summary>
    private static bool IsToolkitPackage(XElement element) => string.Equals((string?)element.Attribute("Include"), ToolkitPackage, StringComparison.OrdinalIgnoreCase);

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
        return new(projectPath, projectText, Serialize(project, projectText), $"Set {relative} build action to {buildAction}");
    }
}
