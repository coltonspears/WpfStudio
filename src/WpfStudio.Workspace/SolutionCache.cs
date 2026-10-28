using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

/// <summary>
/// The project model from the last full MSBuild load, saved so the next start can offer language services in
/// seconds instead of waiting for design-time builds. A cached start is always followed by a full load in the
/// background that replaces it. The cache is ignored when the SDK, a project file, an MSBuild import beside it
/// or the restore output changed since it was written.
/// </summary>
internal static class SolutionCache
{
    private const int Format = 1;
    private const int MaximumEntries = 24;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Overridden by tests; otherwise a per-user folder that survives restarts.</summary>
    internal static string Directory =>
        Environment.GetEnvironmentVariable("WPFSTUDIO_WORKSPACE_CACHE") is { Length: > 0 } custom ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio", "WorkspaceCache");

    internal sealed record Entry(int Format, string Path, string Configuration, string? TargetFramework, string Sdk,
        IReadOnlyList<Stamp> Inputs, IReadOnlyList<CachedProject> Projects, IReadOnlyList<CachedEvaluation> Evaluations,
        IReadOnlyList<WorkspaceProject> Models);
    internal sealed record Stamp(string Path, long Ticks, long Length);
    internal sealed record CachedEvaluation(string ProjectPath, ProjectDiscovery.EvaluatedProject Project);
    internal sealed record CachedDocument(string Path, string Name, IReadOnlyList<string> Folders, bool Script);
    internal sealed record CachedReference(string Path, IReadOnlyList<string> Aliases, bool EmbedInteropTypes);
    internal sealed record CachedProjectReference(Guid Project, IReadOnlyList<string> Aliases, bool EmbedInteropTypes);
    internal sealed record CachedProject(Guid Id, string Name, string AssemblyName, string? FilePath, string? OutputFilePath,
        string? OutputRefFilePath, string? CompilationOutputPath, string? DefaultNamespace, CachedCompilation Compilation, CachedParse Parse,
        IReadOnlyList<CachedDocument> Documents, IReadOnlyList<CachedDocument> AdditionalDocuments, IReadOnlyList<CachedDocument> AnalyzerConfigDocuments,
        IReadOnlyList<CachedReference> MetadataReferences, IReadOnlyList<string> Analyzers, IReadOnlyList<CachedProjectReference> ProjectReferences);
    internal sealed record CachedCompilation(OutputKind OutputKind, string? ModuleName, string? MainTypeName, IReadOnlyList<string> Usings,
        OptimizationLevel Optimization, bool CheckOverflow, bool AllowUnsafe, Platform Platform, ReportDiagnostic GeneralDiagnosticOption,
        int WarningLevel, IReadOnlyDictionary<string, ReportDiagnostic> SpecificDiagnosticOptions, NullableContextOptions Nullable,
        bool Deterministic, string? CryptoKeyFile, bool? DelaySign, bool PublicSign, bool ConcurrentBuild);
    internal sealed record CachedParse(LanguageVersion LanguageVersion, DocumentationMode DocumentationMode, IReadOnlyList<string> PreprocessorSymbols,
        IReadOnlyDictionary<string, string> Features);

    internal static string FileFor(LoadWorkspaceRequest request) =>
        Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Path.GetFullPath(request.Path).ToUpperInvariant()}\n{request.Configuration}\n{request.TargetFramework}")))[..32] + ".json");

    /// <summary>Returns the saved model when every recorded input is unchanged, otherwise null.</summary>
    public static async Task<Entry?> ReadAsync(LoadWorkspaceRequest request, string sdk, CancellationToken cancellationToken)
    {
        var file = FileFor(request);
        if (!File.Exists(file)) return null;
        Entry? entry;
        try
        {
            await using var stream = File.OpenRead(file);
            entry = await JsonSerializer.DeserializeAsync<Entry>(stream, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
        if (entry is null || entry.Format != Format || entry.Sdk != sdk || entry.Projects.Count == 0
            || !string.Equals(entry.Path, Path.GetFullPath(request.Path), StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var input in entry.Inputs)
            if (!Matches(input)) return null;
        return entry;
    }

    public static void Write(LoadWorkspaceRequest request, string sdk, Solution solution,
        IReadOnlyDictionary<string, ProjectDiscovery.EvaluatedProject?> evaluations, IReadOnlyList<WorkspaceProject> models)
    {
        // Only C# projects can be rebuilt from saved options; anything else waits for MSBuild every time.
        if (solution.Projects.Any(project => project.Language != LanguageNames.CSharp || project.FilePath is null)) return;
        if (evaluations.Values.Any(evaluation => evaluation is null)) return;
        var projects = solution.Projects.Select(Capture).ToArray();
        if (projects.Any(project => project is null)) return;
        var path = Path.GetFullPath(request.Path);
        var inputs = InputsFor(path, solution.Projects.Select(project => project.FilePath!), evaluations.Values.OfType<ProjectDiscovery.EvaluatedProject>());
        var entry = new Entry(Format, path, request.Configuration, request.TargetFramework, sdk, inputs, projects!,
            evaluations.Select(pair => new CachedEvaluation(pair.Key, pair.Value!)).ToArray(), models);
        var file = FileFor(request);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(entry, Json));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Prune(file);
    }

    // Keeps the most recently used workspaces; each entry can be a few megabytes for a large solution.
    private static void Prune(string keep)
    {
        try
        {
            foreach (var stale in new DirectoryInfo(Path.GetDirectoryName(keep)!).EnumerateFiles("*.json")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumEntries))
                stale.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public static void Delete(LoadWorkspaceRequest request)
    {
        try { File.Delete(FileFor(request)); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    // The files a design-time build depends on that change without the project file changing: imports found
    // by convention beside the project, the SDK pin, and restore output.
    private static Stamp[] InputsFor(string workspacePath, IEnumerable<string> projectPaths, IEnumerable<ProjectDiscovery.EvaluatedProject> evaluations)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { workspacePath };
        foreach (var project in projectPaths)
        {
            files.Add(project);
            var obj = Path.Combine(Path.GetDirectoryName(project)!, "obj");
            files.Add(Path.Combine(obj, "project.assets.json"));
            files.Add(Path.Combine(obj, Path.GetFileName(project) + ".nuget.g.props"));
            files.Add(Path.Combine(obj, Path.GetFileName(project) + ".nuget.g.targets"));
            for (var directory = Path.GetDirectoryName(project); directory is not null; directory = Path.GetDirectoryName(directory))
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config", "nuget.config" })
                    files.Add(Path.Combine(directory, name));
        }
        foreach (var evaluation in evaluations)
            if (evaluation.ProjectAssetsPath is { Length: > 0 } assets) files.Add(assets);
        return files.Select(StampOf).ToArray();
    }

    private static Stamp StampOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new(path, file.LastWriteTimeUtc.Ticks, file.Length) : new(path, 0, -1);
    }

    private static bool Matches(Stamp stamp) => StampOf(stamp.Path) == stamp;

    private static CachedProject? Capture(Project project)
    {
        if (project.CompilationOptions is not CSharpCompilationOptions compilation || project.ParseOptions is not CSharpParseOptions parse) return null;
        static CachedDocument Document(TextDocument document) => new(document.FilePath!, document.Name, document.Folders.ToArray(),
            document is Document { SourceCodeKind: SourceCodeKind.Script });
        if (project.Documents.Concat<TextDocument>(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments).Any(document => document.FilePath is null)) return null;
        var references = new List<CachedReference>();
        foreach (var reference in project.MetadataReferences)
        {
            if (reference is not PortableExecutableReference { FilePath: { } path } executable) return null;
            references.Add(new(path, executable.Properties.Aliases.ToArray(), executable.Properties.EmbedInteropTypes));
        }
        var analyzers = new List<string>();
        foreach (var analyzer in project.AnalyzerReferences)
        {
            if (analyzer.FullPath is not { } path) return null;
            analyzers.Add(path);
        }
        return new(project.Id.Id, project.Name, project.AssemblyName, project.FilePath, project.OutputFilePath, project.OutputRefFilePath,
            project.CompilationOutputInfo.AssemblyPath, project.DefaultNamespace,
            new(compilation.OutputKind, compilation.ModuleName, compilation.MainTypeName, compilation.Usings.ToArray(), compilation.OptimizationLevel,
                compilation.CheckOverflow, compilation.AllowUnsafe, compilation.Platform, compilation.GeneralDiagnosticOption, compilation.WarningLevel,
                compilation.SpecificDiagnosticOptions, compilation.NullableContextOptions, compilation.Deterministic, compilation.CryptoKeyFile,
                compilation.DelaySign, compilation.PublicSign, compilation.ConcurrentBuild),
            new(parse.SpecifiedLanguageVersion, parse.DocumentationMode, parse.PreprocessorSymbolNames.ToArray(), parse.Features),
            project.Documents.Select(Document).ToArray(), project.AdditionalDocuments.Select(Document).ToArray(),
            project.AnalyzerConfigDocuments.Select(Document).ToArray(), references, analyzers,
            project.ProjectReferences.Select(reference => new CachedProjectReference(reference.ProjectId.Id, reference.Aliases.ToArray(), reference.EmbedInteropTypes)).ToArray());
    }

    /// <summary>Rebuilds the saved solution in a plain workspace. Files deleted since the save are left out.</summary>
    public static (Microsoft.CodeAnalysis.Workspace Workspace, Solution Solution) Create(Entry entry)
    {
        var workspace = new AdhocWorkspace(MSBuildMefHostServices.DefaultServices, "WpfStudio cached workspace");
        // Shared instances let every project reuse one copy of each reference's metadata and symbols.
        var metadata = new Dictionary<(string, string, bool), MetadataReference>();
        var analyzers = new Dictionary<string, AnalyzerReference>(StringComparer.OrdinalIgnoreCase);
        var ids = entry.Projects.ToDictionary(project => project.Id, project => ProjectId.CreateFromSerialized(project.Id, project.Name));
        var projects = new List<ProjectInfo>();
        foreach (var project in entry.Projects)
        {
            var id = ids[project.Id];
            var directory = Path.GetDirectoryName(project.FilePath) ?? "";
            IEnumerable<DocumentInfo> Documents(IEnumerable<CachedDocument> documents) => documents.Where(document => File.Exists(document.Path))
                .Select(document => DocumentInfo.Create(DocumentId.CreateNewId(id, document.Name), document.Name, document.Folders,
                    document.Script ? SourceCodeKind.Script : SourceCodeKind.Regular, new FileTextLoader(document.Path, null), document.Path));
            var compilation = project.Compilation;
            var options = new CSharpCompilationOptions(compilation.OutputKind, moduleName: compilation.ModuleName, mainTypeName: compilation.MainTypeName,
                    usings: compilation.Usings, optimizationLevel: compilation.Optimization, checkOverflow: compilation.CheckOverflow,
                    allowUnsafe: compilation.AllowUnsafe, cryptoKeyFile: compilation.CryptoKeyFile, delaySign: compilation.DelaySign,
                    platform: compilation.Platform, generalDiagnosticOption: compilation.GeneralDiagnosticOption, warningLevel: compilation.WarningLevel,
                    specificDiagnosticOptions: compilation.SpecificDiagnosticOptions, concurrentBuild: compilation.ConcurrentBuild,
                    deterministic: compilation.Deterministic, publicSign: compilation.PublicSign, nullableContextOptions: compilation.Nullable)
                .WithXmlReferenceResolver(new XmlFileResolver(directory))
                .WithSourceReferenceResolver(new SourceFileResolver([], directory))
                .WithStrongNameProvider(new DesktopStrongNameProvider())
                .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
            var parse = new CSharpParseOptions(project.Parse.LanguageVersion, project.Parse.DocumentationMode, SourceCodeKind.Regular, project.Parse.PreprocessorSymbols)
                .WithFeatures(project.Parse.Features);
            var references = project.MetadataReferences.Where(reference => File.Exists(reference.Path)).Select(reference =>
            {
                var key = (reference.Path, string.Join(",", reference.Aliases), reference.EmbedInteropTypes);
                if (metadata.TryGetValue(key, out var existing)) return existing;
                var documentation = Path.ChangeExtension(reference.Path, ".xml");
                return metadata[key] = MetadataReference.CreateFromFile(reference.Path,
                    new MetadataReferenceProperties(MetadataImageKind.Assembly, [.. reference.Aliases], reference.EmbedInteropTypes),
                    File.Exists(documentation) ? XmlDocumentationProvider.CreateFromFile(documentation) : null);
            }).ToArray();
            var analyzerReferences = project.Analyzers.Where(File.Exists)
                .Select(path => analyzers.TryGetValue(path, out var existing) ? existing : analyzers[path] = new AnalyzerFileReference(path, AnalyzerLoader.Instance)).ToArray();
            var info = ProjectInfo.Create(id, VersionStamp.Create(), project.Name, project.AssemblyName, LanguageNames.CSharp, project.FilePath,
                    project.OutputFilePath, options, parse, Documents(project.Documents),
                    project.ProjectReferences.Where(reference => ids.ContainsKey(reference.Project))
                        .Select(reference => new ProjectReference(ids[reference.Project], [.. reference.Aliases], reference.EmbedInteropTypes)),
                    references, analyzerReferences, Documents(project.AdditionalDocuments), outputRefFilePath: project.OutputRefFilePath)
                .WithAnalyzerConfigDocuments(Documents(project.AnalyzerConfigDocuments))
                .WithDefaultNamespace(project.DefaultNamespace)
                .WithCompilationOutputInfo(default(CompilationOutputInfo).WithAssemblyPath(project.CompilationOutputPath));
            projects.Add(info);
        }
        var solution = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), entry.Path, projects));
        return (workspace, solution);
    }

    /// <summary>
    /// Loads analyzers and source generators for a cached solution: one context per folder, with the compiler and
    /// runtime shared from the host, read from memory so package and project outputs are never locked.
    /// </summary>
    private sealed class AnalyzerLoader : IAnalyzerAssemblyLoader
    {
        public static readonly AnalyzerLoader Instance = new();
        private readonly ConcurrentDictionary<string, FolderContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath) =>
            _contexts.GetOrAdd(Path.GetDirectoryName(fullPath)!, directory => new FolderContext(directory)).LoadFile(fullPath);

        private sealed class FolderContext(string directory) : AssemblyLoadContext("WpfStudio analyzers " + directory)
        {
            private readonly ConcurrentDictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

            public Assembly LoadFile(string path) => _loaded.GetOrAdd(Path.GetFullPath(path), file =>
            {
                using var stream = new MemoryStream(File.ReadAllBytes(file));
                return LoadFromStream(stream);
            });

            protected override Assembly? Load(AssemblyName name)
            {
                if (name.Name is not { } simple || simple.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                    || simple.StartsWith("System.", StringComparison.Ordinal) || simple is "netstandard" or "mscorlib") return null;
                var candidate = Path.Combine(directory, simple + ".dll");
                return File.Exists(candidate) ? LoadFile(candidate) : null;
            }
        }
    }
}
