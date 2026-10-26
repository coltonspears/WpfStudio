using System.Text.Json;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

/// <summary>
/// Asks MSBuild which assemblies a project references, the way a build resolves them: HintPath and file references,
/// project reference outputs, and the dependencies and satellites found beside them. Framework and NuGet reference
/// assemblies are left out; the preview takes package runtime assets from the build's .deps.json instead.
/// Referenced projects are followed, so their own HintPath references (often <c>Private=false</c>) are found too.
/// </summary>
public static class PreviewReferenceResolver
{
    public const int MaximumProjects = 32;

    public static async Task<PreviewReferenceSet> ResolveAsync(string projectPath, string configuration, string? framework, CancellationToken cancellationToken)
    {
        var assemblies = new Dictionary<string, PreviewReference>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Project, string? Framework)>([(Path.GetFullPath(projectPath), framework)]);
        while (pending.TryDequeue(out var next))
        {
            if (!visited.Add(next.Project)) continue;
            if (visited.Count > MaximumProjects) { warnings.Add($"Only the first {MaximumProjects} projects' references were resolved."); break; }
            string json;
            try { json = await QueryAsync(next.Project, configuration, next.Framework, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"References of {Path.GetFileName(next.Project)} could not be resolved: {exception.Message}");
                continue;
            }
            var result = Select(json);
            // The previewed project's own copy-local choices come first; referenced projects add what is still missing.
            foreach (var reference in result.Assemblies) assemblies.TryAdd(reference.RelativePath, reference);
            directories.UnionWith(result.SearchDirectories);
            // A referenced project may target other frameworks; MSBuild picks its nearest match only when it has one.
            foreach (var project in result.ProjectReferences) pending.Enqueue((project, null));
        }
        return new([.. assemblies.Values.OrderBy(reference => reference.RelativePath, StringComparer.OrdinalIgnoreCase)], [.. directories.Order(StringComparer.OrdinalIgnoreCase)], warnings);
    }

    private static async Task<string> QueryAsync(string projectPath, string configuration, string? framework, CancellationToken cancellationToken)
    {
        // BuildingProject=true makes ResolveAssemblyReferences search for dependencies, as a real build does;
        // BuildProjectReferences=false keeps it from compiling anything.
        var arguments = new List<string>
        {
            "msbuild", projectPath, "-nologo", "-t:ResolveAssemblyReferences", $"-p:Configuration={configuration}",
            "-p:BuildingProject=true", "-p:BuildProjectReferences=false",
            "-getItem:ReferencePath,ReferenceDependencyPaths,ReferenceSatellitePaths,ProjectReference"
        };
        if (!string.IsNullOrWhiteSpace(framework)) arguments.Add($"-p:TargetFramework={framework}");
        var result = await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(Path.GetDirectoryName(projectPath)!, [.. arguments]), cancellationToken).ConfigureAwait(false);
        var start = result.Output.IndexOf('{');
        if (result.ExitCode != 0 || start < 0)
            throw new InvalidOperationException(FirstLine(result.Error) ?? FirstLine(result.Output) ?? $"MSBuild exited with code {result.ExitCode}. Restore the project.");
        return result.Output[start..];
    }

    private static string? FirstLine(string text) => text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);

    public sealed record Selection(IReadOnlyList<PreviewReference> Assemblies, IReadOnlyList<string> SearchDirectories, IReadOnlyList<string> ProjectReferences);

    /// <summary>Picks the references a preview needs from MSBuild's <c>-getItem</c> JSON.</summary>
    public static Selection Select(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var items = document.RootElement.GetProperty("Items");
        var assemblies = new List<PreviewReference>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<string>();
        IEnumerable<JsonElement> Group(string name) => items.TryGetProperty(name, out var group) ? group.EnumerateArray() : [];
        string? Metadata(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.GetString() : null;
        foreach (var (group, item) in new[] { "ReferencePath", "ReferenceDependencyPaths", "ReferenceSatellitePaths" }.SelectMany(group => Group(group).Select(item => (group, item))))
        {
            if (Metadata(item, "FullPath") is not { Length: > 0 } path || !Path.IsPathRooted(path)) continue;
            // Framework packs and NuGet packages resolve to compile-only reference assemblies.
            if (Metadata(item, "NuGetPackageId") is { Length: > 0 } || Metadata(item, "FrameworkReferenceName") is { Length: > 0 }
                || Metadata(item, "FrameworkFile") == "true" || Metadata(item, "ExternallyResolved") == "true") continue;
            if (Path.GetExtension(path).ToLowerInvariant() is not (".dll" or ".exe")) continue;
            var subdirectory = (Metadata(item, "DestinationSubDirectory") ?? "").Replace('\\', '/').Trim('/');
            if (subdirectory.Split('/').Any(part => part is "." or ".." || part.Contains(':'))) continue;
            assemblies.Add(new(path, subdirectory.Length == 0 ? Path.GetFileName(path) : subdirectory + "/" + Path.GetFileName(path)));
            // HintPath folders often hold further assemblies that load late, such as plugins and design-time parts.
            if (group != "ReferenceSatellitePaths" && Metadata(item, "ReferenceSourceTarget") != "ProjectReference" && Path.GetDirectoryName(path) is { } directory)
                directories.Add(directory);
        }
        foreach (var item in Group("ProjectReference"))
            if (Metadata(item, "FullPath") is { Length: > 0 } project && project.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                && Metadata(item, "ReferenceOutputAssembly") is not "false") projects.Add(project);
        return new([.. assemblies.DistinctBy(reference => reference.RelativePath, StringComparer.OrdinalIgnoreCase)], [.. directories], projects);
    }
}
