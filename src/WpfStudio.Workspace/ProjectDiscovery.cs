using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public static partial class ProjectDiscovery
{
    private static readonly string[] FileItemTypes = ["Compile", "Page", "ApplicationDefinition", "Resource", "Content", "None"];

    public static IReadOnlyList<string> GetProjectPaths(string workspacePath)
    {
        workspacePath = System.IO.Path.GetFullPath(workspacePath);
        var directory = System.IO.Path.GetDirectoryName(workspacePath)!;
        if (workspacePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return [workspacePath];
        if (workspacePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            return XDocument.Load(workspacePath).Descendants().Where(e => e.Name.LocalName == "Project")
                .Select(e => (string?)e.Attribute("Path")).OfType<string>()
                .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(p => System.IO.Path.GetFullPath(p.Replace('\\', System.IO.Path.DirectorySeparatorChar), directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (workspacePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return SolutionProjectRegex().Matches(File.ReadAllText(workspacePath)).Select(m => System.IO.Path.GetFullPath(m.Groups[1].Value.Replace('\\', System.IO.Path.DirectorySeparatorChar), directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        throw new ArgumentException("Open a .sln, .slnx, or .csproj file.", nameof(workspacePath));
    }

    /// <summary>
    /// Evaluates with the .NET SDK, or with <paramref name="msbuildPath"/> and the <paramref name="solutionPath"/> folder as SolutionDir
    /// to see a legacy project the way Visual Studio's MSBuild built it.
    /// </summary>
    internal static async Task<EvaluatedProject> EvaluateAsync(string projectPath, string configuration, string? framework, CancellationToken cancellationToken,
        string? solutionPath = null, string? msbuildPath = null)
    {
        var arguments = new List<string> { projectPath, "-nologo", $"-p:Configuration={configuration}", "-getProperty:TargetFramework,TargetFrameworks,OutputType,TargetPath,AssemblyName,Configurations,ProjectAssetsFile,TargetFrameworkMoniker", "-getItem:" + string.Join(',', FileItemTypes.Concat(DependencyItemTypes)) };
        if (!string.IsNullOrWhiteSpace(framework)) arguments.Add($"-p:TargetFramework={framework}");
        if (SolutionDirArgument(projectPath, solutionPath) is { } solutionDir) arguments.Add(solutionDir);
        var directory = System.IO.Path.GetDirectoryName(projectPath)!;
        var result = msbuildPath is null ? (ExitCode: -1, Output: "", Error: "")
            : await ProcessRunner.CaptureAsync(ProcessRunner.Create(msbuildPath, directory, arguments), cancellationToken);
        // MSBuild before Visual Studio 2022 17.8 has no -getProperty switch; the .NET SDK evaluates most legacy projects too.
        if (result.ExitCode != 0) result = await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, ["msbuild", .. arguments]), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Project evaluation failed for {projectPath}: {result.Error} {result.Output}");
        var start = result.Output.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("MSBuild did not return project evaluation data.");
        using var json = JsonDocument.Parse(result.Output[start..]);
        var properties = json.RootElement.GetProperty("Properties");
        string Property(string name) => properties.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        var files = new Dictionary<string, WorkspaceFile>(StringComparer.OrdinalIgnoreCase);
        var resources = new List<EvaluatedXamlResource>();
        var items = json.RootElement.GetProperty("Items");
        foreach (var group in items.EnumerateObject().Where(group => FileItemTypes.Contains(group.Name)))
        foreach (var item in group.Value.EnumerateArray())
        {
            var path = item.TryGetProperty("FullPath", out var fullPath) ? fullPath.GetString() : null;
            if (string.IsNullOrEmpty(path)) continue;
            var link = item.TryGetProperty("Link", out var logicalPath) ? logicalPath.GetString() : null;
            files.TryAdd(path, new WorkspaceFile(path, System.IO.Path.GetFileName(path), group.Name, LogicalPath: string.IsNullOrWhiteSpace(link) ? null : link));
            if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && group.Name is "Page" or "ApplicationDefinition" or "Resource" or "Content")
            {
                string? Metadata(string name) => item.TryGetProperty(name, out var value) ? value.GetString() : null;
                string? identity = group.Name == "Content" ? Metadata("TargetPath") : Metadata("LogicalName");
                if (group.Name == "Content" && Metadata("CopyToOutputDirectory") is not ("Always" or "PreserveNewest" or "IfDifferent"))
                    identity = null;
                // WPF carries LogicalName/Link from markup items into generated BAML.
                // Content needs an evaluated deployment path; its source location is
                // not proof of where a custom build will place it.
                if (group.Name != "Content" && string.IsNullOrWhiteSpace(identity))
                {
                    var perceived = string.IsNullOrWhiteSpace(link) ? path : System.IO.Path.GetFullPath(link, System.IO.Path.GetDirectoryName(projectPath)!);
                    identity = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(projectPath)!, perceived);
                    if (identity.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        identity = System.IO.Path.GetFileName(perceived); // WPF ResourcesGenerator's outside-project fallback.
                }
                identity = NormalizeResourcePath(identity);
                resources.Add(new(System.IO.Path.GetFullPath(path), identity, group.Name,
                    identity is null ? "The evaluated resource output path is unavailable or unsupported." : null));
            }
        }
        // The outer evaluation of a multi-target project falls back to Microsoft.Common's default framework identity; it has none of its own.
        var moniker = string.IsNullOrWhiteSpace(Property("TargetFramework")) && !string.IsNullOrWhiteSpace(Property("TargetFrameworks")) ? "" : Property("TargetFrameworkMoniker");
        return new EvaluatedProject(Property("TargetFramework"), Property("TargetFrameworks"), Property("TargetPath"), Property("OutputType"), files.Values.ToArray(), Property("AssemblyName"), Property("Configurations"), resources.ToArray(),
            string.IsNullOrWhiteSpace(Property("ProjectAssetsFile")) ? null : System.IO.Path.GetFullPath(Property("ProjectAssetsFile"), System.IO.Path.GetDirectoryName(projectPath)!),
            moniker.Length == 0 ? null : moniker, ReadEvaluatedDependencies(projectPath, items, Property("TargetFramework"), Property("TargetFrameworks"), moniker));
    }

    /// <summary>A solution build defines SolutionDir itself; a project built from a solution receives it the way Visual Studio passes it.</summary>
    internal static string? SolutionDirArgument(string targetPath, string? solutionPath) =>
        string.IsNullOrWhiteSpace(solutionPath) || !targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? null
            : "-p:SolutionDir=" + System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(solutionPath))!) + System.IO.Path.DirectorySeparatorChar;

    private static string? NormalizeResourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048) return null;
        path = path.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Contains('?') || path.Contains('#') || path.Any(char.IsControl)) return null;
        var parts = path.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) return null;
        return path;
    }

    internal static IReadOnlyList<WorkspaceFile> FallbackFiles(string projectPath)
    {
        var files = new List<WorkspaceFile>();
        var queue = new Queue<string>();
        queue.Enqueue(System.IO.Path.GetDirectoryName(projectPath)!);
        while (queue.TryDequeue(out var directory))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory)) files.Add(new WorkspaceFile(path, System.IO.Path.GetFileName(path), "File"));
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (System.IO.Path.GetFileName(child) is not ("bin" or "obj" or ".git" or ".vs" or "node_modules") && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) queue.Enqueue(child);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return files;
    }

    [GeneratedRegex("^Project\\(.*?\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+\\.csproj)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SolutionProjectRegex();

    public static IReadOnlyList<string> GetDeclaredConfigurations(string workspacePath)
    {
        try
        {
            if (workspacePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(File.ReadAllText(workspacePath), @"GlobalSection\(SolutionConfigurationPlatforms\)[^\r\n]*\r?\n(?<entries>.*?)EndGlobalSection", RegexOptions.Singleline);
                return match.Groups["entries"].Value.Split('\n').Where(line => line.Contains('='))
                    .Select(line => line.Split('=')[0].Trim().Split('|')[0]).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            var xml = XDocument.Load(workspacePath);
            if (workspacePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return xml.Descendants().Where(element => element.Name.LocalName == "BuildType").Select(element => (string?)element.Attribute("Name")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return xml.Descendants().Where(element => element.Name.LocalName == "Configurations").SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Where(name => !name.Contains("$(", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is IOException or System.Xml.XmlException) { return []; }
    }

    internal sealed record EvaluatedXamlResource(string Path, string? ResourcePath, string Kind, string? Status);
    internal sealed record EvaluatedProject(string TargetFramework, string TargetFrameworks, string OutputPath, string OutputType, IReadOnlyList<WorkspaceFile> Files, string AssemblyName, string Configurations, IReadOnlyList<EvaluatedXamlResource> XamlResources, string? ProjectAssetsPath = null,
        string? TargetFrameworkMoniker = null, IReadOnlyList<ProjectDependency>? Dependencies = null);
}
