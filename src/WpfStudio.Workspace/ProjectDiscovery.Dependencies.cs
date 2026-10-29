using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public static partial class ProjectDiscovery
{
    /// <summary>Item types read as project references rather than project files.</summary>
    private static readonly string[] DependencyItemTypes = ["PackageReference", "PackageVersion", "ProjectReference", "Reference", "FrameworkReference"];

    /// <summary>
    /// SDK-style project files declare an SDK. A legacy project file imports Microsoft.CSharp.targets itself and
    /// relies on Visual Studio's MSBuild for WPF markup compilation and other .NET Framework build targets.
    /// An unreadable project is treated as SDK-style, the .NET SDK's own default.
    /// </summary>
    public static bool IsSdkStyle(string projectPath)
    {
        try { return IsSdkStyle(XDocument.Load(projectPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException) { return true; }
    }

    private static bool IsSdkStyle(XDocument project) => project.Root is not { } root || root.Attribute("Sdk") is not null
        || root.Elements().Any(element => element.Name.LocalName == "Sdk")
        || root.Descendants().Any(element => element.Name.LocalName == "Import" && element.Attribute("Sdk") is not null);

    /// <summary>
    /// True when the project, a project of the solution, or a project either one references is a legacy project file.
    /// The .NET SDK cannot build those completely, so the whole build goes to Visual Studio's MSBuild.
    /// </summary>
    public static bool RequiresFrameworkMSBuild(string workspacePath)
    {
        try
        {
            var pending = new Queue<string>(GetProjectPaths(workspacePath));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryDequeue(out var projectPath))
            {
                if (!visited.Add(projectPath) || !File.Exists(projectPath)) continue;
                XDocument project;
                try { project = XDocument.Load(projectPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException) { continue; }
                if (!IsSdkStyle(project)) return true;
                var directory = Path.GetDirectoryName(projectPath)!;
                foreach (var reference in project.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
                    if (ProjectReferenceDependency((string?)reference.Attribute("Include"), directory)?.Path is { } referencedPath) pending.Enqueue(referencedPath);
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or ArgumentException) { return false; }
    }

    /// <summary>True for a .NET Framework target framework moniker such as net48 or net472. .NET 5 and later use a dotted version.</summary>
    public static bool IsNetFrameworkTarget(string? targetFramework) =>
        targetFramework is { Length: > 3 } && targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase) && targetFramework[3..].All(char.IsAsciiDigit);

    /// <summary>Whether the project runs on .NET Framework when built for <paramref name="selectedFramework"/> (or its only framework).</summary>
    public static bool TargetsNetFramework(WorkspaceProject project, string? selectedFramework)
    {
        if (!string.IsNullOrWhiteSpace(selectedFramework)) return IsNetFrameworkTarget(selectedFramework);
        if (!string.IsNullOrWhiteSpace(project.TargetFramework)) return IsNetFrameworkTarget(project.TargetFramework);
        return project.TargetFrameworkMoniker?.StartsWith(".NETFramework,", StringComparison.OrdinalIgnoreCase) == true || !project.IsSdkStyle;
    }

    /// <summary>The project's packages.config (or packages.<i>ProjectName</i>.config) file, which NuGet uses instead of PackageReference items.</summary>
    public static string? FindPackagesConfig(string projectPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var named = Path.Combine(directory, $"packages.{Path.GetFileNameWithoutExtension(projectPath)}.config");
        if (File.Exists(named)) return named;
        var shared = Path.Combine(directory, "packages.config");
        return File.Exists(shared) ? shared : null;
    }

    public static IReadOnlyList<ProjectDependency> ReadPackagesConfig(string projectPath)
    {
        if (FindPackagesConfig(projectPath) is not { } path) return [];
        try
        {
            return XDocument.Load(path).Descendants().Where(element => element.Name.LocalName == "package")
                .Select(element => (Id: (string?)element.Attribute("id"), Version: (string?)element.Attribute("version")))
                .Where(package => !string.IsNullOrWhiteSpace(package.Id))
                .Select(package => new ProjectDependency("Package", package.Id!, package.Version)).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException) { return []; }
    }

    /// <summary>References declared in the project file itself, for a project MSBuild could not evaluate. Imports and conditions are not applied.</summary>
    public static IReadOnlyList<ProjectDependency> ReadDeclaredDependencies(string projectPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            var elements = XDocument.Load(projectPath).Descendants().ToArray();
            string Property(string name) => elements.LastOrDefault(element => element.Name.LocalName == name && element.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim() ?? "";
            IEnumerable<(string Include, XElement Item)> Items(string type) => elements
                .Where(element => element.Name.LocalName == type && element.Parent?.Name.LocalName == "ItemGroup" && !string.IsNullOrWhiteSpace((string?)element.Attribute("Include")))
                .Select(element => (element.Attribute("Include")!.Value.Trim(), element));
            static string? Metadata(XElement item, string name) => item.Attribute(name)?.Value ?? item.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
            var frameworkVersion = Property("TargetFrameworkVersion");
            var dependencies = FrameworkDependencies(Property("TargetFramework"), Property("TargetFrameworks"),
                frameworkVersion.Length > 0 ? ".NETFramework,Version=" + frameworkVersion : null, Items("FrameworkReference").Select(item => item.Include)).ToList();
            dependencies.AddRange(Items("PackageReference").Select(item => new ProjectDependency("Package", item.Include, Metadata(item.Item, "VersionOverride") ?? Metadata(item.Item, "Version"))));
            dependencies.AddRange(ReadPackagesConfig(projectPath));
            dependencies.AddRange(Items("ProjectReference").Select(item => ProjectReferenceDependency(item.Include, directory)).OfType<ProjectDependency>());
            dependencies.AddRange(Items("Reference").Select(item => AssemblyDependency(item.Include, Metadata(item.Item, "HintPath"), directory)));
            return Distinct(dependencies);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException) { return []; }
    }

    private static IReadOnlyList<ProjectDependency> ReadEvaluatedDependencies(string projectPath, JsonElement items, string targetFramework, string targetFrameworks, string targetFrameworkMoniker)
    {
        var directory = Path.GetDirectoryName(projectPath)!;
        IEnumerable<JsonElement> Items(string type) => items.TryGetProperty(type, out var group) && group.ValueKind == JsonValueKind.Array ? group.EnumerateArray() : [];
        static string? Metadata(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
        var dependencies = FrameworkDependencies(targetFramework, targetFrameworks, targetFrameworkMoniker,
            Items("FrameworkReference").Select(item => Metadata(item, "Identity")).OfType<string>()).ToList();
        // Central package management keeps versions on PackageVersion items until restore applies them.
        var centralVersions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items("PackageVersion"))
            if (Metadata(item, "Identity") is { } id) centralVersions[id] = Metadata(item, "Version");
        foreach (var item in Items("PackageReference"))
        {
            // SDK-added references (NETStandard.Library, reference assemblies) are not the project's own choice; `dotnet list package` omits them too.
            if (Metadata(item, "Identity") is not { } id || string.Equals(Metadata(item, "IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase)) continue;
            dependencies.Add(new("Package", id, Metadata(item, "VersionOverride") ?? Metadata(item, "Version") ?? centralVersions.GetValueOrDefault(id)));
        }
        dependencies.AddRange(ReadPackagesConfig(projectPath));
        foreach (var item in Items("ProjectReference"))
            if (ProjectReferenceDependency(Metadata(item, "FullPath") ?? Metadata(item, "Identity"), directory) is { } project) dependencies.Add(project);
        foreach (var item in Items("Reference"))
            if (Metadata(item, "Identity") is { } identity) dependencies.Add(AssemblyDependency(identity, Metadata(item, "HintPath"), directory));
        return Distinct(dependencies);
    }

    private static IEnumerable<ProjectDependency> FrameworkDependencies(string targetFramework, string targetFrameworks, string? targetFrameworkMoniker, IEnumerable<string> frameworkReferences)
    {
        var frameworks = new List<ProjectDependency>();
        // A multi-target project evaluated without a selected framework has no single framework identity.
        if (string.IsNullOrWhiteSpace(targetFramework))
            frameworks.AddRange(targetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(framework => new ProjectDependency("Framework", framework)));
        frameworks.AddRange(frameworkReferences.Select(reference => new ProjectDependency("Framework", reference)));
        if (frameworks.Count > 0) return frameworks;
        if (FrameworkDisplay(targetFrameworkMoniker) is { } display) return [display];
        return string.IsNullOrWhiteSpace(targetFramework) ? [] : [new ProjectDependency("Framework", targetFramework.Trim())];
    }

    /// <summary>".NETFramework,Version=v4.7.2" becomes ".NET Framework" 4.7.2.</summary>
    private static ProjectDependency? FrameworkDisplay(string? moniker)
    {
        var parts = moniker?.Split(",Version=v", 2, StringSplitOptions.TrimEntries);
        if (parts is not [{ Length: > 0 } identifier, { Length: > 0 } version]) return null;
        var name = identifier switch
        {
            ".NETFramework" => ".NET Framework",
            ".NETStandard" => ".NET Standard",
            ".NETCoreApp" => Version.TryParse(version, out var parsed) && parsed.Major < 5 ? ".NET Core" : ".NET",
            _ => identifier
        };
        return new("Framework", name, version);
    }

    private static ProjectDependency? ProjectReferenceDependency(string? include, string directory)
    {
        if (string.IsNullOrWhiteSpace(include)) return null;
        try
        {
            var path = Path.GetFullPath(include.Replace('\\', Path.DirectorySeparatorChar), directory);
            return new("Project", Path.GetFileNameWithoutExtension(path), Path: path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>A Reference is either an assembly name ("Newtonsoft.Json, Version=13.0.0.0, …") or a path to the assembly.</summary>
    private static ProjectDependency AssemblyDependency(string include, string? hintPath, string directory)
    {
        string? FullPath(string path)
        {
            try { return Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), directory); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        if (include.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || include.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return new("Assembly", Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar)), Path: FullPath(include));
        var parts = include.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var version = parts.Skip(1).Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(pair => pair.Length == 2 && pair[0].Equals("Version", StringComparison.OrdinalIgnoreCase))?[1];
        return new("Assembly", parts.Length > 0 ? parts[0] : include, version, string.IsNullOrWhiteSpace(hintPath) ? null : FullPath(hintPath));
    }

    private static IReadOnlyList<ProjectDependency> Distinct(IEnumerable<ProjectDependency> dependencies) => dependencies
        .GroupBy(dependency => dependency.Kind + "\0" + dependency.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
        .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
