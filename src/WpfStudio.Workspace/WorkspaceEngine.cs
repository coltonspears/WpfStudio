using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace;

/// <summary>Lives exclusively in WorkspaceHost. User project assemblies are never loaded into the editor process.</summary>
public sealed partial class WorkspaceEngine : IWorkspaceRpc, IDisposable
{
    public async Task<WorkspaceSnapshot> LoadAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken)
    {
        lock (_gate) _lifetime.BeginLoad();
        try
        {
            var acquired = request.UseCache ? await AcquireCachedAsync(request, cancellationToken).ConfigureAwait(false) : null;
            acquired ??= await AcquireAsync(request, cancellationToken).ConfigureAwait(false);
            var snapshot = await PublishAsync(request, acquired, cancellationToken).ConfigureAwait(false);
            // Discovering Roslyn's code fix providers takes a few seconds; do it before the first Ctrl+.
            Task.Run(CodeActionCatalog.Warm, CancellationToken.None).Forget(fault => Console.Error.WriteLine("Code action catalog: " + fault.Message));
            lock (_gate) _lifetime.InventoryPublished(CurrentSolution is not null);
            return snapshot;
        }
        finally
        {
            lock (_gate)
            {
                _lifetime.EndLoad();
                // Ownership is populated after the Roslyn solution is loaded. Publish
                // that completed inventory together with a new semantic generation.
                SemanticRevision++;
            }
        }
    }

    /// <summary>
    /// Loads the workspace with MSBuild and runs its source generators while the current (usually cached) solution
    /// keeps answering requests. <see cref="CommitRefreshAsync"/> then replaces it. A failed load leaves the current
    /// solution in place.
    /// </summary>
    public async Task<bool> PrepareRefreshAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            if (acquired.Solution is not { } solution)
                throw new InvalidOperationException(string.Join(" ", acquired.Issues.Select(issue => issue.Message)) is { Length: > 0 } reason
                    ? reason : "MSBuild could not load the workspace.");
            // MSBuild reports a project it cannot open and loads it with no documents, as with a half-merged project file.
            // The solution still lists it and the current model still answers for it, so swapping would only lose those answers.
            // Without a failure the project has simply stopped compiling anything, and that is a change to take.
            var failures = acquired.Issues.Where(issue => issue.Severity == "Error").Select(issue => issue.Message).ToArray();
            Solution? current;
            lock (_gate) current = _pages.Authored ?? CurrentSolution;
            if (failures.Length > 0 && current is not null && acquired.Paths.Any(path => HasDocuments(current, path) && !HasDocuments(solution, path)))
                throw new InvalidOperationException(string.Join(" ", failures));
            // Generators would otherwise run while publishing, when the client waits.
            foreach (var project in solution.Projects)
                await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { acquired.Workspace?.Dispose(); throw; }
        _lifetime.ExchangePrepared(acquired)?.Workspace?.Dispose();
        return true;
    }

    /// <summary>Replaces the current solution with the prepared one. Open buffers are not carried over; the client replays them.</summary>
    public async Task<WorkspaceSnapshot> CommitRefreshAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken)
    {
        var acquired = _lifetime.ExchangePrepared(null) ?? throw new InvalidOperationException("No refreshed project model is ready.");
        lock (_gate) _lifetime.BeginRefresh();
        try
        {
            var snapshot = await PublishAsync(request, acquired, cancellationToken).ConfigureAwait(false);
            lock (_gate) _lifetime.InventoryPublished(CurrentSolution is not null);
            return snapshot;
        }
        finally
        {
            lock (_gate) { _lifetime.EndLoad(); SemanticRevision++; }
            SemanticStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool HasDocuments(Solution solution, string projectPath) =>
        solution.Projects.Any(project => project.DocumentIds.Count > 0 && string.Equals(project.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a load produced before it replaces the engine's state: a solution, evaluations and timings.</summary>
    private sealed record Acquired(Microsoft.CodeAnalysis.Workspace? Workspace, Solution? Solution, string Sdk, bool Cached, IReadOnlyList<string> Paths,
        ConcurrentQueue<WorkspaceIssue> Issues, Func<string, Task<ProjectDiscovery.EvaluatedProject?>> Evaluate, LoadTimings Timings,
        IReadOnlyDictionary<string, WorkspaceProject> CachedModels, bool OutOfDate = false);

    private static async Task<Acquired> AcquireAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(request.Path);
        var issues = new ConcurrentQueue<WorkspaceIssue>();
        var timings = new LoadTimings();
        var paths = ProjectDiscovery.GetProjectPaths(path);
        // Each evaluation is a separate `dotnet msbuild` process that does not depend on the Roslyn
        // solution. Start them now so they overlap the solution load instead of running one by one after it.
        var evaluations = new ConcurrentDictionary<string, Task<ProjectDiscovery.EvaluatedProject?>>(StringComparer.OrdinalIgnoreCase);
        var evaluationSlots = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
        Task<ProjectDiscovery.EvaluatedProject?> Evaluate(string projectPath) => evaluations.GetOrAdd(projectPath, key => Task.Run<ProjectDiscovery.EvaluatedProject?>(async () =>
        {
            await evaluationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await ProjectDiscovery.EvaluateAsync(key, request.Configuration, request.TargetFramework, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { issues.Enqueue(new WorkspaceIssue(ex.Message)); return null; }
            finally { evaluationSlots.Release(); }
        }, cancellationToken));
        foreach (var projectPath in paths) _ = Evaluate(projectPath);
        var sdk = "Unavailable";
        MSBuildWorkspace? workspace = null;
        Solution? loaded = null;
        try
        {
            sdk = await ToolchainResolver.RegisterAsync(path, cancellationToken).ConfigureAwait(false);
            timings.Mark("SDK");
            var properties = new Dictionary<string, string> { ["Configuration"] = request.Configuration };
            if (!string.IsNullOrWhiteSpace(request.TargetFramework)) properties["TargetFramework"] = request.TargetFramework;
            workspace = MSBuildWorkspace.Create(properties);
            workspace.RegisterWorkspaceFailedHandler(args => issues.Enqueue(new WorkspaceIssue(args.Diagnostic.Message, args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "Error" : "Warning")));
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                loaded = (await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution;
            else loaded = await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            timings.Mark("projects");
        }
        catch (OperationCanceledException) { workspace?.Dispose(); throw; }
        catch (Exception ex)
        {
            issues.Enqueue(new WorkspaceIssue($"Language workspace could not load: {ex.Message}. File navigation remains available.", "Error"));
            workspace?.Dispose(); workspace = null; loaded = null;
        }
        return new(workspace, loaded, sdk, false, paths, issues, Evaluate, timings, new Dictionary<string, WorkspaceProject>());
    }

    // The model saved by the last full load. One behind the disk still answers at once, because a refresh follows every cached start.
    private static async Task<Acquired?> AcquireCachedAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken)
    {
        var timings = new LoadTimings();
        try
        {
            var path = Path.GetFullPath(request.Path);
            var sdk = await ToolchainResolver.RegisterAsync(path, cancellationToken).ConfigureAwait(false);
            var entry = await SolutionCache.ReadAsync(request, sdk, cancellationToken).ConfigureAwait(false);
            if (entry is null) return null;
            var paths = ProjectDiscovery.GetProjectPaths(path);
            var (workspace, solution) = SolutionCache.Create(entry);
            timings.Mark("cached projects");
            var evaluations = entry.Evaluations.ToDictionary(item => item.ProjectPath, item => Task.FromResult<ProjectDiscovery.EvaluatedProject?>(item.Project), StringComparer.OrdinalIgnoreCase);
            return new(workspace, solution, sdk, true, paths, new(),
                projectPath => evaluations.GetValueOrDefault(projectPath) ?? Task.FromResult<ProjectDiscovery.EvaluatedProject?>(null), timings,
                entry.Models.ToDictionary(model => model.ProjectPath, StringComparer.OrdinalIgnoreCase), SolutionCache.IsOutOfDate(entry));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A cache that cannot be read or rebuilt is only a missed shortcut; the full load follows.
            SolutionCache.Delete(request);
            return null;
        }
    }

    private async Task<WorkspaceSnapshot> PublishAsync(LoadWorkspaceRequest request, Acquired acquired, CancellationToken cancellationToken)
    {
        Microsoft.CodeAnalysis.Workspace? previous;
        lock (_gate)
        {
            CurrentSolution = null;
            _pages.Reset();
            SemanticRevision++;
            ResetProjectAnalysis();
            // With its solution unpublished above, no new request can start on the workspace closed below.
            previous = _lifetime.ReplaceWorkspace(acquired.Workspace);
        }
        var path = Path.GetFullPath(request.Path);
        var issues = acquired.Issues;
        var timings = acquired.Timings;
        var paths = acquired.Paths;
        var sdk = acquired.Sdk;
        Task<ProjectDiscovery.EvaluatedProject?> Evaluate(string projectPath) => acquired.Evaluate(projectPath);
        if (!ReferenceEquals(previous, acquired.Workspace)) previous?.Dispose();
        lock (_gate)
        {
            if (acquired.Solution is { } loaded) { SetAuthoredSolution(loaded); SemanticRevision++; }
            else CurrentSolution = null;
            _documents.Clear(); _completions.Clear(); OpenCompletions = null; _codeLensCounts.Clear(); _codeActions.Clear(); _ownership.Owners.Clear();
        }
        var configurations = new HashSet<string>(ProjectDiscovery.GetDeclaredConfigurations(path), StringComparer.OrdinalIgnoreCase);
        var loadedProjects = acquired.Solution?.Projects.ToArray() ?? [];
        async Task<(WorkspaceProject Model, IEnumerable<string> Configurations)> BuildProjectAsync(string projectPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = loadedProjects.FirstOrDefault(p => string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));
            var evaluated = await Evaluate(projectPath).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                foreach (var context in loadedProjects.Where(p => string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase)))
                {
                    if (evaluated is null) _ownership.Unavailable.Add(context.Id);
                    else
                    {
                        _ownership.Files[context.Id] = evaluated.Files.Where(file => !file.IsGenerated && !IsGenerated(file.Path)
                            && file.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)).Select(file => Path.GetFullPath(file.Path))
                            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                        _ownership.Resources[context.Id] = new(evaluated.XamlResources.Where(file => !IsGenerated(file.Path)).ToArray(),
                            evaluated.OutputType is "Exe" or "WinExe");
                    }
                }
            var projectConfigurations = evaluated?.Configurations.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? ProjectDiscovery.GetDeclaredConfigurations(projectPath);
            var files = (evaluated?.Files ?? ProjectDiscovery.FallbackFiles(projectPath)).Where(f => !acquired.OutOfDate || File.Exists(f.Path)).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            if (project is not null)
            {
                foreach (var document in project.Documents)
                {
                    if (document.FilePath is not { } documentPath) continue;
                    var generated = IsGenerated(documentPath);
                    files[documentPath] = new WorkspaceFile(documentPath, document.Name, "Compile", generated, document.Id.ToString(), files.GetValueOrDefault(documentPath)?.LogicalPath);
                }
                // A cached start lists the generated files the last full load saw; running generators here would compile
                // every project before the editor is usable. The background refresh lists them afresh.
                if (acquired.Cached && acquired.CachedModels.TryGetValue(projectPath, out var cachedModel))
                    foreach (var generatedFile in cachedModel.Files.Where(file => file.Kind == "Generated" && File.Exists(file.Path)))
                        files[generatedFile.Path] = generatedFile;
                // Roslyn runs source generators; exposing these documents makes generated Toolkit members navigable.
                else foreach (var document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
                    if (document.FilePath is { } generatedPath)
                    {
                        var generatedText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        var cachePath = await CacheGeneratedAsync(generatedPath, generatedText, cancellationToken).ConfigureAwait(false);
                        files[cachePath] = new WorkspaceFile(cachePath, document.Name, "Generated", true, document.Id.ToString());
                    }
            }
            files.TryAdd(projectPath, new WorkspaceFile(projectPath, Path.GetFileName(projectPath), "Project"));
            return (new WorkspaceProject(project?.Id.ToString() ?? projectPath, project?.Name ?? Path.GetFileNameWithoutExtension(projectPath), projectPath,
                evaluated?.TargetFramework, evaluated?.OutputPath ?? project?.OutputFilePath, evaluated?.OutputType is "Exe" or "WinExe",
                files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(), evaluated?.AssemblyName, evaluated?.ProjectAssetsPath,
                ProjectDiscovery.IsSdkStyle(projectPath), evaluated?.TargetFrameworkMoniker, evaluated?.Dependencies ?? ProjectDiscovery.ReadDeclaredDependencies(projectPath)), projectConfigurations);
        }
        // Projects are independent here: generator runs and evaluations proceed together, results keep solution order.
        var built = await Task.WhenAll(paths.Concat(loadedProjects.Select(p => p.FilePath).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(BuildProjectAsync)).ConfigureAwait(false);
        var models = built.Select(result => result.Model).ToList();
        timings.Mark(acquired.Cached ? "project model" : "evaluation and generators");
        foreach (var configuration in built.SelectMany(result => result.Configurations)) configurations.Add(configuration);
        lock (_gate)
        {
            CaptureDiskDocuments(loadedProjects);
            foreach (var model in models)
            foreach (var project in loadedProjects.Where(p => string.Equals(p.FilePath, model.ProjectPath, StringComparison.OrdinalIgnoreCase)))
            foreach (var file in model.Files.Where(f => f.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
            {
                var filePath = Path.GetFullPath(file.Path);
                if (!_ownership.Owners.TryGetValue(filePath, out var owners)) _ownership.Owners[filePath] = owners = [];
                owners.Add(project.Id);
            }
        }
        await RefreshPageProjectionSourcesAsync(null, cancellationToken).ConfigureAwait(false);
        // Every semantic request waits for the projection itself, so a cached start returns without it.
        if (acquired.Cached) Task.Run(WarmPageProjectionAsync, CancellationToken.None).Forget();
        else await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        timings.Mark("XAML pages");
        issues.Enqueue(new WorkspaceIssue(acquired.OutOfDate
            ? $"Language services started in {timings} from a saved project model that is out of date, because project files changed since it was saved. Errors can be wrong until MSBuild finishes refreshing it in the background."
            : acquired.Cached
                ? $"Language services started from the saved project model in {timings}; MSBuild is refreshing it in the background."
                : $"Language services loaded in {timings}.", "Info"));
        if (configurations.Count == 0) { configurations.Add("Debug"); configurations.Add("Release"); }
        if (!acquired.Cached && acquired.Solution is { } fresh)
        {
            // Saved off the request path; a failure only means the next start loads from MSBuild again.
            var evaluated = new Dictionary<string, ProjectDiscovery.EvaluatedProject?>(StringComparer.OrdinalIgnoreCase);
            foreach (var projectPath in paths) evaluated[projectPath] = await Evaluate(projectPath).ConfigureAwait(false);
            // MSBuild also loads references the solution file does not list, and a cached start needs their evaluations for the
            // same XAML checks. One that failed stays out instead of stopping the save, as it did before these were saved.
            foreach (var projectPath in loadedProjects.Select(project => project.FilePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(item => !evaluated.ContainsKey(item)))
                if (await Evaluate(projectPath).ConfigureAwait(false) is { } reference) evaluated[projectPath] = reference;
            var saved = models.ToArray();
            Task.Run(() => SolutionCache.Write(request, sdk, fresh, evaluated, saved), CancellationToken.None)
                .Forget(fault => Console.Error.WriteLine("Workspace cache: " + fault.Message));
        }
        return new WorkspaceSnapshot(path, sdk, models, issues.ToArray(), configurations.OrderBy(value => value == "Debug" ? 0 : value == "Release" ? 1 : 2).ThenBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(), acquired.Cached);
    }

    private async Task WarmPageProjectionAsync()
    {
        try { await EnsurePageProjectionAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { Console.Error.WriteLine("XAML page projection: " + exception.Message); }
    }

    public async Task<DocumentUpdateResult> UpdateDocumentAsync(UpdateDocumentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            return await UpdateXamlProjectionDocumentAsync(request, cancellationToken).ConfigureAwait(false);
        Document document;
        var path = Path.GetFullPath(request.Path);
        // Compiler output can remain visible in the project tree, but page
        // projection deliberately replaces it in the semantic solution. It is
        // never an authored replay buffer and must not mutate the input model.
        if (IsGenerated(path)) return new(false, request.Version, []);
        var sameDeclarations = await SolutionWithSameDeclarationsAsync(path, request, cancellationToken).ConfigureAwait(false);
        bool declarationsChanged = false;
        lock (_gate)
        {
            if (_documents.OpenVersion(path) is { } current && request.Version < current) return new DocumentUpdateResult(false, current, []);
            var solution = _pages.Authored ?? RequireSolution();
            var ids = solution.GetDocumentIdsWithFilePath(path);
            if (ids.IsEmpty) return new DocumentUpdateResult(false, request.Version, []);
            if (_documents.OpenVersion(path) == request.Version && _documents.Text(path) is { } existingText)
            {
                if (!string.Equals(existingText, request.Text, StringComparison.Ordinal)) throw new InvalidOperationException("A document version cannot identify different text. Increment its version before updating.");
            }
            else
            {
                var text = SourceText.From(request.Text);
                // The texts the comparison below the lock was made with, each derived from its document's previous text, so that the
                // compiler parses again only around the edit. They belong to the solution they were derived from.
                var derived = ReferenceEquals(solution, sameDeclarations?.Solution) ? sameDeclarations!.Value.Texts : null;
                SourceText TextFor(DocumentId id) => derived?.GetValueOrDefault(id) ?? text;
                var projected = ReferenceEquals(solution, sameDeclarations?.Solution) && !_pages.Pending && ids.All(id => CurrentSolution?.ContainsDocument(id) == true) ? CurrentSolution : null;
                foreach (var id in ids) solution = solution.WithDocumentText(id, TextFor(id), PreservationMode.PreserveIdentity);
                if (projected is null)
                {
                    SetAuthoredSolution(solution);
                    declarationsChanged = true;
                }
                else
                {
                    // Pages are projected from declarations, so the projection holds. Building it again would cost every keystroke its whole cost.
                    foreach (var id in ids) projected = projected.WithDocumentText(id, TextFor(id), PreservationMode.PreserveIdentity);
                    _pages.Authored = solution;
                    CurrentSolution = projected;
                    _projectDiagnostics.CancelRunning();
                }
                SemanticsChanged(projected is null ? null : sameDeclarations!.Value.ChangedNames);
                _disk.Settled(path);
                _documents.Set(path, request.Version, request.Text);
                _codeActions.Clear();
            }
            document = solution.GetDocument(ids[0])!;
        }
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_pages.Pending || _documents.Version(path) != request.Version
                || CurrentSolution?.GetDocument(document.Id) is not { } currentDocument)
                return new(false, _documents.Version(path), []);
            document = currentDocument;
        }
        if (!request.Analyze) return new DocumentUpdateResult(true, request.Version, [], declarationsChanged);
        var diagnostics = await GetDiagnosticsAsync(document, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return _documents.OpenVersion(path) == request.Version && !_pages.Pending && ReferenceEquals(document.Project.Solution, CurrentSolution) ? new DocumentUpdateResult(true, request.Version, diagnostics, declarationsChanged) : new DocumentUpdateResult(false, _documents.Version(path), []);
    }

    /// <summary>
    /// The solution this text can replace a document in without changing what anything outside the file sees: it differs only
    /// inside method bodies, in non-constant initializers, in comments or in whitespace. Null when it declares something different.
    /// </summary>
    private async Task<(Solution Solution, HashSet<string> ChangedNames, Dictionary<DocumentId, SourceText> Texts)?> SolutionWithSameDeclarationsAsync(string path, UpdateDocumentRequest request, CancellationToken cancellationToken)
    {
        Solution? authored;
        string? synchronized;
        lock (_gate)
        {
            // Most updates repeat the version the worker already has, and those change nothing to compare.
            if (_documents.OpenVersion(path) == request.Version) return null;
            authored = _pages.Pending ? null : _pages.Authored;
            synchronized = _documents.Text(path);
        }
        if (authored is null) return null;
        var ids = authored.GetDocumentIdsWithFilePath(path);
        if (ids.IsEmpty) return null;
        var whole = SourceText.From(request.Text);
        var changedNames = new HashSet<string>(StringComparer.Ordinal);
        var texts = new Dictionary<DocumentId, SourceText>();
        foreach (var id in ids)
        {
            // Each target framework has its own document: an edit inside #if can be a comment to one and a declaration to another.
            var document = authored.GetDocument(id);
            var before = document is null ? null : await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (before is null) return null;
            // A text with no relation to the previous one has the whole file parsed again: here, in the authored solution and in the
            // projected one, on every pause in typing. Stated as one change to the previous text, all three parse around the edit.
            var source = texts[id] = ChangedText(await document!.GetTextAsync(cancellationToken).ConfigureAwait(false), synchronized, request.Text) ?? whole;
            var after = before.WithChangedText(source);
            if (!before.IsEquivalentTo(after, topLevel: true)) return null;
            changedNames.UnionWith(ChangedNames(await before.GetRootAsync(cancellationToken).ConfigureAwait(false), await after.GetRootAsync(cancellationToken).ConfigureAwait(false)));
        }
        return (authored, changedNames, texts);
    }

    /// <summary>
    /// <paramref name="after"/> as one change to <paramref name="current"/>, or null when the worker's copy of the buffer,
    /// <paramref name="synchronized"/>, is not what the document holds: the change is found by comparing the two strings.
    /// </summary>
    private static SourceText? ChangedText(SourceText current, string? synchronized, string after)
    {
        if (synchronized is null || current.Length != synchronized.Length || !current.ContentEquals(SourceText.From(synchronized))) return null;
        int prefix = synchronized.AsSpan().CommonPrefixLength(after);
        int limit = Math.Min(synchronized.Length, after.Length) - prefix, suffix = 0;
        while (suffix < limit && synchronized[synchronized.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        return current.WithChanges(new TextChange(TextSpan.FromBounds(prefix, synchronized.Length - suffix), after[prefix..(after.Length - suffix)]));
    }

    /// <summary>The names that appear a different number of times after an edit.</summary>
    private static IEnumerable<string> ChangedNames(SyntaxNode before, SyntaxNode after)
    {
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(SyntaxNode root, int by)
        {
            foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
                if (token.IsKind(SyntaxKind.IdentifierToken)) uses[token.ValueText] = uses.GetValueOrDefault(token.ValueText) + by;
        }
        Count(before, 1);
        Count(after, -1);
        return uses.Where(pair => pair.Value != 0).Select(pair => pair.Key);
    }

    public async Task CloseDocumentAsync(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        { await CloseXamlProjectionDocumentAsync(path, cancellationToken).ConfigureAwait(false); return; }
        if (IsGenerated(path)) return;
        long? version;
        string? synchronizedText;
        lock (_gate)
        {
            version = _documents.OpenVersion(path);
            synchronizedText = _documents.Text(path);
        }
        var read = await ReadBoundedTextAsync(path, DiskDocumentCharacters, cancellationToken, codePageSource: true).ConfigureAwait(false);
        lock (_gate)
        {
            // A new edit/reopen can arrive while disk I/O is pending. It owns the buffer.
            if (_documents.OpenVersion(path) != version || _documents.Text(path) != synchronizedText) return;
            if (_pages.Authored is { } solution)
            {
                // A tab closed with the text it was saved with, which is most of them, leaves the solution as it is. Replacing the text
                // regardless made every close recompile the project and its dependents and rebuild the page projection of all projects.
                bool unchanged = synchronizedText is not null && string.Equals(read.Text, synchronizedText, StringComparison.Ordinal);
                if (!unchanged && (read.Text is not null || read.State == "Missing"))
                    foreach (var id in solution.GetDocumentIdsWithFilePath(path))
                        solution = solution.WithDocumentText(id, SourceText.From(read.Text ?? ""), PreservationMode.PreserveIdentity);
                if (_disk.Documents.ContainsKey(path))
                {
                    if (read.Text is null) _disk.Unavailable[path] = read.Status ?? "The model source file is unavailable.";
                    else _disk.Unavailable.Remove(path);
                    _disk.Pending.Remove(path);
                }
                if (!unchanged) SetAuthoredSolution(solution);
                SemanticRevision++;
                _batchCache.Clear();
            }
            _documents.Remove(path); _projectDiagnostics.Reported.Remove(path);
            _completions.Clear(); OpenCompletions = null; _codeActions.Clear();
        }
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompletionResult> GetCompletionsAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        Document document;
        long generation;
        lock (_gate)
        {
            document = GetDocument(request.Path, request.Version);
            generation = _completions.Begin(); // Keep only the active popup, bounded to 1,500 entries and one solution snapshot.
            OpenCompletions = null;
        }
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        ValidatePosition(request.Position, text.Length);
        var service = CompletionService.GetService(document);
        if (service is null) return new CompletionResult(request.Version, request.Position, 0, []);
        var list = await service.GetCompletionsAsync(document, request.Position, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (list is null) return new CompletionResult(request.Version, request.Position, 0, []);
        var prefix = text.ToString(TextSpan.FromBounds(list.Span.Start, Math.Clamp(request.Position, list.Span.Start, list.Span.End)));
        var origins = await CompletionOrigins.FindAsync(document, list.Span.Start, cancellationToken).ConfigureAwait(false);
        var items = new List<CompletionEntry>();
        var (matching, incomplete) = CompletionMatching.Select(list.ItemsList, prefix, CompletionEntries);
        lock (_gate)
        {
            if (!_completions.IsLatest(generation) || _pages.Pending || !ReferenceEquals(document.Project.Solution, CurrentSolution))
                return new CompletionResult(request.Version, request.Position, 0, []);
            var open = new OpenCompletionList(document, []);
            foreach (var item in matching)
            {
                var id = _completions.Add(document, request.Version, item);
                open.Items[id] = item;
                // A type that still needs a using names its namespace itself.
                string? origin = string.IsNullOrEmpty(item.InlineDescription) ? origins.Of(item.DisplayText) : item.InlineDescription;
                items.Add(new CompletionEntry(id, item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix, item.DisplayText, null, item.Tags, Origin: origin));
            }
            OpenCompletions = open;
        }
        return new CompletionResult(request.Version, list.Span.Start, list.Span.Length, items, incomplete);
    }

    // The popup filters what it was given as more is typed, so this bounds one reply, not what can be found.
    private const int CompletionEntries = 1500;

    public async Task<TextEdit?> GetCompletionEditAsync(CompletionEditRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        _ = GetDocument(request.Path, request.Version);
        if (!_completions.TryGet(request.ItemId, out var entry) || entry.Version != request.Version || !string.Equals(entry.Document.FilePath, Path.GetFullPath(request.Path), StringComparison.OrdinalIgnoreCase)) return null;
        var service = CompletionService.GetService(entry.Document)!;
        var change = await service.GetChangeAsync(entry.Document, entry.Item, cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return !_pages.Pending && ReferenceEquals(entry.Document.Project.Solution, CurrentSolution) && _completions.Contains(request.ItemId) ? ToEdit(change.TextChange) : null;
    }

    public async Task<SignatureHelpResult> GetSignatureHelpAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var document = GetDocument(request.Path, request.Version);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || root.FullSpan.IsEmpty) return new SignatureHelpResult(request.Version, 0, []);
        ValidatePosition(request.Position, root.FullSpan.Length);
        var node = root.FindToken(Math.Max(0, request.Position - 1)).Parent;
        // Inside the brackets: after the opening one, and before the closing one unless that is still to be typed.
        var argumentList = node?.AncestorsAndSelf().OfType<BaseArgumentListSyntax>().FirstOrDefault(a => a.SpanStart < request.Position
            && (request.Position < a.Span.End || a.GetLastToken(includeZeroWidth: true).IsMissing && request.Position <= a.Span.End));
        if (argumentList?.Parent is not { } expression) return new SignatureHelpResult(request.Version, 0, []);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null) return new SignatureHelpResult(request.Version, 0, []);
        var symbolInfo = model.GetSymbolInfo(expression, cancellationToken);
        var symbols = new List<ISymbol>();
        if (symbolInfo.Symbol is { } symbol) symbols.Add(symbol);
        symbols.AddRange(symbolInfo.CandidateSymbols);
        if (expression is InvocationExpressionSyntax invocation) symbols.AddRange(model.GetMemberGroup(invocation.Expression, cancellationToken));
        if (expression is ObjectCreationExpressionSyntax creation && model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol type) symbols.AddRange(type.InstanceConstructors);
        var methods = symbols.OfType<IMethodSymbol>().Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToArray();
        var signatures = methods.Select(method =>
        {
            var documentation = method.GetDocumentationCommentXml(cancellationToken: cancellationToken);
            return new SignatureEntry(
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), PlainDocumentation(documentation),
                method.Parameters.Select(p => new SignatureParameter(p.Name, p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    ParameterDocumentation(documentation, p.Name), p.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))).ToArray(),
                method.ToDisplayString(SignaturePrefixFormat));
        }).ToArray();
        var active = argumentList.Arguments.GetSeparators().Count(s => s.SpanStart < request.Position);
        // An argument that names its parameter goes to that one, wherever it stands among the others.
        var named = active < argumentList.Arguments.Count ? argumentList.Arguments[active].NameColon?.Name.Identifier.ValueText : null;
        // The overload the call binds to comes first. While the call is incomplete, the first that takes this many arguments,
        // and has the parameter being named, is the likeliest.
        int typed = argumentList.Arguments.Count;
        int fitting = symbolInfo.Symbol is not null ? 0 : Array.FindIndex(methods, method => Takes(method, Math.Max(typed, active + 1))
            && (named is null || method.Parameters.Any(parameter => parameter.Name == named)));
        return new SignatureHelpResult(request.Version, active, signatures, Math.Max(0, fitting), argumentList.GetFirstToken().Span.End, named);
    }

    private static readonly SymbolDisplayFormat SignaturePrefixFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeContainingType);

    private static bool Takes(IMethodSymbol method, int arguments) =>
        method.Parameters.Length >= arguments || method.Parameters.LastOrDefault() is { IsParams: true };

    private static string? ParameterDocumentation(string? xml, string parameter)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var text = System.Xml.Linq.XElement.Parse(xml).Elements("param").FirstOrDefault(element => (string?)element.Attribute("name") == parameter)?.Value;
            return string.IsNullOrWhiteSpace(text) ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Xml.XmlException) { return null; }
    }

    public async Task<IReadOnlyList<SourceLocation>> FindReferencesAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        return (await FindSymbolReferencesAsync(new(request.Path, request.Position, request.Version), cancellationToken).ConfigureAwait(false)).Locations;
    }

    public async Task<WorkspaceEditResult> FormatDocumentAsync(DocumentRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var document = GetDocument(request.Path, request.Version);
        var formatted = await Formatter.FormatAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
        var changes = await formatted.GetTextChangesAsync(document, cancellationToken).ConfigureAwait(false);
        var before = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceEditResult([new DocumentEdits(document.FilePath!, request.Version, changes.Select(ToEdit).ToArray(), TextHash(before.ToString()))], []);
    }

    public async Task<WorkspaceEditResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken)
    {
        return await RenameWithXamlAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceEditResult> RefactorAsync(RefactorRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var document = GetDocument(request.Path, request.Version);
        var changed = await CSharpRefactorings.ApplyAsync(document, request.Position, request.Action, cancellationToken).ConfigureAwait(false);
        var changes = await changed.GetTextChangesAsync(document, cancellationToken).ConfigureAwait(false);
        var before = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new([new(document.FilePath!, request.Version, changes.Select(ToEdit).ToArray(), TextHash(before.ToString()))], []);
    }

    private Document GetDocument(string path, long version)
    {
        path = Path.GetFullPath(path);
        if (IsGenerated(path))
            throw new InvalidOperationException("Language queries for generated compiler output are unavailable. Open the authored XAML or C# source instead.");
        lock (_gate)
        {
            if (_pages.Pending) throw new InvalidOperationException("Current XAML fields are being synchronized; retry with the current buffers.");
            if (_documents.Version(path) != version) throw new InvalidOperationException("Document version is stale; synchronize the buffer and retry.");
            var solution = RequireSolution();
            var id = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault() ?? throw new FileNotFoundException("This file is not part of the loaded C# workspace.", path);
            return solution.GetDocument(id)!;
        }
    }

    private Solution RequireSolution() => CurrentSolution ?? throw new InvalidOperationException("Language services are unavailable. Check workspace loading diagnostics.");

    private async Task<IReadOnlyList<WorkspaceDiagnostic>> GetDiagnosticsAsync(Document document, CancellationToken cancellationToken)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var semantic = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null || semantic is null) return [];
        var diagnostics = semantic.GetDiagnostics(cancellationToken: cancellationToken);
        var analyzers = document.Project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(document.Project.Language)).ToImmutableArray();
        if (!analyzers.IsEmpty)
        {
            var options = new CompilationWithAnalyzersOptions(document.Project.AnalyzerOptions, null, true, false, false);
            var analysis = compilation.WithAnalyzers(analyzers, options);
                diagnostics = diagnostics.AddRange(await analysis.GetAnalyzerSyntaxDiagnosticsAsync(semantic.SyntaxTree, cancellationToken).ConfigureAwait(false));
                diagnostics = diagnostics.AddRange(await analysis.GetAnalyzerSemanticDiagnosticsAsync(semantic, null, cancellationToken).ConfigureAwait(false));
            }
        return CompleteDiagnostics(document, compilation, await semantic.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false), diagnostics);
    }

    private static WorkspaceDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var line = diagnostic.Location.GetMappedLineSpan();
        var span = diagnostic.Location.SourceSpan;
        return new WorkspaceDiagnostic(diagnostic.Id, diagnostic.GetMessage(), diagnostic.Severity.ToString(), diagnostic.Location.IsInSource ? line.Path : null,
            diagnostic.Location.IsInSource ? line.StartLinePosition.Line + 1 : 0, diagnostic.Location.IsInSource ? line.StartLinePosition.Character + 1 : 0, span.Start, span.Length);
    }

    private static async Task<SourceLocation> ToLocationAsync(Location location, string? display, CancellationToken cancellationToken)
    {
        var line = location.GetMappedLineSpan();
        var path = line.Path;
        var start = location.SourceSpan.Start;
        var available = File.Exists(path);
        // The text the location is reported in, for the line of code a list of locations shows.
        SourceText? shown = null;
        if (available && !string.Equals(path, location.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            // #line maps generated WPF fields back to XAML. Their C# offsets are not XAML offsets.
            try
            {
                var text = SourceText.From(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                var sourceLine = text.Lines[Math.Clamp(line.StartLinePosition.Line, 0, text.Lines.Count - 1)];
                start = Math.Min(sourceLine.End, sourceLine.Start + line.StartLinePosition.Character);
                shown = text;
            }
            // Locked, unreadable or just deleted: the C# declaration below is still somewhere to go.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { available = false; }
        }
        if (!available && location.SourceTree is { } tree)
        {
            var text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            path = await CacheGeneratedAsync(tree.FilePath, text, cancellationToken).ConfigureAwait(false);
            line = location.GetLineSpan();
            shown = text;
        }
        if (shown is null && location.SourceTree is { } own) shown = await own.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new SourceLocation(path, start, location.SourceSpan.Length, line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1, display,
            LineText: shown is null ? null : LineOf(shown, line.StartLinePosition.Line));
    }
    private static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static async Task<string> CacheGeneratedAsync(string originalPath, SourceText text, CancellationToken cancellationToken)
    {
        var content = text.ToString();
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio", "GeneratedSources", TextHash(originalPath + "\n" + content));
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(originalPath);
        if (string.IsNullOrWhiteSpace(name)) name = "Generated.g.cs";
        var path = Path.Combine(directory, name);
        if (File.Exists(path)) return path;
        // Content-addressed, so a concurrent writer of the same name produces identical bytes. Written beside the target and moved into
        // place: a write cancelled part-way would otherwise leave a truncated file that every later call finds and takes for complete.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path)) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return path;
    }
    private static TextEdit ToEdit(TextChange change) => new(change.Span.Start, change.Span.Length, change.NewText ?? "");
    private static bool IsGenerated(string path) => path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    private static void ValidatePosition(int position, int length) { if (position < 0 || position > length) throw new ArgumentOutOfRangeException(nameof(position)); }
    private static string? PlainDocumentation(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return System.Xml.Linq.XElement.Parse(xml).Element("summary")?.Value.Trim(); }
        catch (System.Xml.XmlException) { return null; }
    }
    public void Dispose()
    {
        Microsoft.CodeAnalysis.Workspace? workspace;
        lock (_gate)
        {
            CurrentSolution = null;
            SemanticRevision++;
            ResetProjectAnalysis();
            _pages.Reset();
            workspace = _lifetime.ReplaceWorkspace(null);
        }
        workspace?.Dispose();
        _lifetime.ExchangePrepared(null)?.Workspace?.Dispose();
    }
}

/// <summary>Elapsed time per workspace load phase, reported in Output so slow loads can be attributed.</summary>
internal sealed class LoadTimings
{
    private readonly System.Diagnostics.Stopwatch _total = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<(string Phase, TimeSpan Elapsed)> _phases = [];
    private TimeSpan _last;

    public void Mark(string phase)
    {
        var now = _total.Elapsed;
        _phases.Add((phase, now - _last));
        _last = now;
    }

    public override string ToString() =>
        $"{_total.Elapsed.TotalSeconds:0.0} s ({string.Join(" · ", _phases.Select(item => $"{item.Phase} {item.Elapsed.TotalSeconds:0.0} s"))})";
}
