using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private const int DiskDocumentCharacters = 2_000_000;
    private const int DiskRefreshCharacters = 16_000_000;
    private long _semanticRevision;
    private readonly Dictionary<ProjectId, string[]> _xamlInventory = [];
    private readonly HashSet<ProjectId> _xamlInventoryUnavailable = [];
    private readonly Dictionary<string, DocumentId[]> _diskDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _diskUnavailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _diskPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _diskRefreshGate = new(1, 1);
    private string? _diskCursor;
    private readonly Dictionary<BatchCacheKey, BatchAnalysis> _xamlBatchCache = [];

    private sealed record BatchCacheKey(ProjectId Project, string Path, string Hash, string Resources);
    private sealed record BatchAnalysis(WorkspaceDiagnostic[] Diagnostics, bool Complete, string? Status);
    private sealed record DiskText(string? Text, string State, string? Status = null);

    // Called with _gate held. Neither replayed open buffers nor fallback directory listings
    // define project ownership: these inventories come from the evaluated workspace only.
    private void ResetProjectAnalysis()
    {
        _xamlInventory.Clear();
        _xamlResourceInventory.Clear();
        _xamlInventoryUnavailable.Clear();
        _diskDocuments.Clear();
        _diskUnavailable.Clear();
        _diskPending.Clear();
        _diskCursor = null;
        _xamlBatchCache.Clear();
    }

    private void CaptureDiskDocuments(IEnumerable<Project> projects)
    {
        foreach (var group in projects.SelectMany(project => project.Documents)
                     .Where(document => document.FilePath is { } path && !IsGenerated(path)
                         && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(document => Path.GetFullPath(document.FilePath!), StringComparer.OrdinalIgnoreCase))
            _diskDocuments[group.Key] = group.Select(document => document.Id).ToArray();
    }

    /// <summary>
    /// Reconciles known closed C# documents without registering replay buffers. Missing files
    /// retain their document identity with empty text so recreation can recover without reload.
    /// A bounded full scan continues its pending sweep on the next call.
    /// </summary>
    public async Task<RefreshDiskDocumentsResult> RefreshDiskDocumentsAsync(RefreshDiskDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int maximum = Math.Clamp(request.MaximumFiles, 1, 2048);
        if (request.Paths is { Count: > 16384 }) return new(false, false, 0, true, "Too many model paths were requested for one refresh.");
        var requested = request.Paths?.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await _diskRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Solution solution;
            long revision;
            HashSet<string> pending;
            Dictionary<string, DocumentId[]> documents;
            string[] selected;
            lock (_gate)
            {
                if (_solution is null) return new(false, false, 0, false, "The language workspace is unavailable.");
                solution = _solution;
                revision = _semanticRevision;
                documents = new(_diskDocuments, StringComparer.OrdinalIgnoreCase);
                pending = new(_diskPending.Where(path => !_syncedTexts.ContainsKey(path)), StringComparer.OrdinalIgnoreCase);
                if (requested is not null)
                    pending.UnionWith(requested.Where(path => documents.ContainsKey(path) && !_syncedTexts.ContainsKey(path)));
                else if (pending.Count == 0)
                    pending.UnionWith(documents.Keys.Where(path => !_syncedTexts.ContainsKey(path)));
                // Rotate even targeted requests, so a repeated over-budget request cannot
                // permanently starve later paths in a large project.
                var priority = requested?.ToHashSet(StringComparer.OrdinalIgnoreCase);
                selected = pending.OrderBy(path => priority?.Contains(path) == true ? 0 : 1)
                    .ThenBy(path => _diskCursor is null || StringComparer.OrdinalIgnoreCase.Compare(path, _diskCursor) > 0 ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).Take(maximum).ToArray();
            }

            var reads = new Dictionary<string, DiskText>(StringComparer.OrdinalIgnoreCase);
            var updated = solution;
            int total = 0;
            foreach (var path in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Defer to the next sweep chunk instead of falsely classifying a valid file as
                // oversized merely because earlier files consumed this call's aggregate budget.
                if (DiskRefreshCharacters - total < DiskDocumentCharacters) break;
                int available = DiskDocumentCharacters;
                var read = await ReadBoundedTextAsync(path, available, cancellationToken).ConfigureAwait(false);
                reads.Add(path, read);
                pending.Remove(path);
                if (read.State == "TooLarge") total += available;
                if (read.Text is { } text)
                {
                    total += text.Length;
                    var replacement = SourceText.From(text);
                    foreach (var id in documents[path])
                        if (updated.GetDocument(id) is { } document
                            && (!document.TryGetText(out var currentText) || !currentText.ContentEquals(replacement)))
                            updated = updated.WithDocumentText(id, replacement, PreservationMode.PreserveIdentity);
                }
                else if (read.State == "Missing")
                {
                    foreach (var id in documents[path])
                        if (updated.GetDocument(id) is { } document
                            && (!document.TryGetText(out var currentText) || currentText.Length != 0))
                            updated = updated.WithDocumentText(id, SourceText.From(""), PreservationMode.PreserveIdentity);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!ReferenceEquals(solution, _solution) || revision != _semanticRevision)
                    return new(false, false, 0, false, "Project types changed during disk refresh; retry with the current buffers.");
                bool changed = !ReferenceEquals(solution, updated) || !_diskPending.SetEquals(pending);
                foreach (var (path, read) in reads)
                {
                    if (read.Text is not null) changed |= _diskUnavailable.Remove(path);
                    else
                    {
                        string reason = read.Status ?? "A model source file is unavailable.";
                        changed |= !_diskUnavailable.TryGetValue(path, out var old) || old != reason;
                        _diskUnavailable[path] = reason;
                    }
                }
                _diskPending.Clear();
                _diskPending.UnionWith(pending);
                if (reads.Count > 0) _diskCursor = reads.Keys.Last();
                if (changed)
                {
                    _solution = updated;
                    _semanticRevision++;
                    _xamlBatchCache.Clear();
                    _completions.Clear();
                }
                bool incomplete = pending.Count > 0 || _diskUnavailable.Count > 0;
                return new(true, changed, reads.Count, incomplete, pending.Count > 0
                    ? $"Model refresh is incomplete; {pending.Count} known files remain in this sweep. A subsequent refresh continues it."
                    : _diskUnavailable.Count > 0 ? $"{_diskUnavailable.Count} model source files are missing, unreadable, or exceed the read budget; affected XAML contexts are unavailable." : null,
                    PendingFiles: pending.Count);
            }
        }
        finally { _diskRefreshGate.Release(); }
    }

    public async Task<XamlProjectAnalysisResult> AnalyzeXamlProjectAsync(XamlProjectAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int maximumFiles = Math.Clamp(request.MaximumFiles, 1, 512);
        int maximumFileCharacters = Math.Clamp(request.MaximumFileCharacters, 1, 1_000_000);
        int maximumTotalCharacters = Math.Clamp(request.MaximumTotalCharacters, 1, 8_000_000);
        int maximumDiagnostics = Math.Clamp(request.MaximumDiagnostics, 1, 2000);
        string? projectPath = request.ProjectPath is null ? null : Path.GetFullPath(request.ProjectPath);
        Solution solution;
        long revision;
        (Project Project, string Path)[] inventory;
        HashSet<ProjectId> unavailable;
        HashSet<string> ambiguous;
        lock (_gate)
        {
            revision = _semanticRevision;
            if (_solution is null) return new(request.Generation, revision, false, [], 0, Status: "The language workspace is unavailable.");
            solution = _solution;
            var projects = solution.Projects.Where(project => project.FilePath is not null
                && (projectPath is null || string.Equals(project.FilePath, projectPath, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (projects.Length == 0) return new(request.Generation, revision, false, [], 0, Status: "The requested project context is unavailable.");
            if (projects.Any(project => _xamlInventoryUnavailable.Contains(project.Id) || !_xamlInventory.ContainsKey(project.Id)))
                return new(request.Generation, revision, false, [], 0, Status: "Evaluated XAML ownership is unavailable; reload the workspace before project analysis.");
            unavailable = UnavailableModelProjects(solution);
            ambiguous = projects.GroupBy(project => project.FilePath!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            inventory = projects.SelectMany(project => _xamlInventory[project.Id].Select(path => (project, path)))
                .OrderBy(item => item.project.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.path, StringComparer.OrdinalIgnoreCase)
                .GroupBy(item => item.project.FilePath!, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.GroupBy(item => item.path, StringComparer.OrdinalIgnoreCase).Select(files => files.First()))
                .Select(item => (item.project, item.path)).ToArray();
        }

        var overlays = new Dictionary<string, XamlDocumentOverlay>(StringComparer.OrdinalIgnoreCase);
        long overlayCharacters = 0;
        if (request.Overlays is null || request.Overlays.Count > 2048)
            return new(request.Generation, revision, false, [], inventory.Length, true, "The open XAML overlay snapshot exceeds the request budget.");
        var ownedPaths = inventory.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var overlay in request.Overlays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(overlay.Path);
            // All-open snapshots may include documents from a different project scope.
            if (!ownedPaths.Contains(path)) continue;
            overlayCharacters += overlay.Text.Length;
            if (overlayCharacters > 16_000_000)
                return new(request.Generation, revision, false, [], inventory.Length, true, "The open XAML overlay snapshot exceeds the request budget.");
            if (overlays.TryGetValue(path, out var old) && (old.Version != overlay.Version || old.Text != overlay.Text))
                return new(request.Generation, revision, false, [], inventory.Length, Status: "Conflicting open XAML snapshots were supplied for one path.");
            overlays[path] = overlay;
        }

        ResourceSnapshot resources;
        try { resources = await CaptureXamlResourcesAsync(solution, inventory.Select(item => item.Project.Id).Distinct(),
            request.Overlays, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException exception) { return new(request.Generation, revision, false, [], inventory.Length, Status: exception.Message); }
        var files = new List<XamlFileAnalysisResult>();
        var compilations = new Dictionary<ProjectId, Compilation?>();
        int totalCharacters = 0, diagnosticCount = 0;
        bool truncated = inventory.Length > maximumFiles;
        bool incomplete = !resources.Complete;
        foreach (var (project, path) in inventory.Take(maximumFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            overlays.TryGetValue(path, out var overlay);
            long? version = overlay?.Version;
            DiskText read;
            int remaining = maximumTotalCharacters - totalCharacters;
            if (remaining <= 0) read = new(null, "TooLarge", "The project XAML text budget was exhausted before this file could be analyzed.");
            else if (overlay is not null)
                read = overlay.Text.Length <= Math.Min(maximumFileCharacters, remaining) ? new(overlay.Text, "Read")
                    : new(null, "TooLarge", "The open XAML snapshot exceeds the file or remaining project text budget.");
            else if (resources.Texts.TryGetValue(path, out var content))
                read = content.Read.Text is { } captured && captured.Length > Math.Min(maximumFileCharacters, remaining)
                    ? new(null, "TooLarge", "The XAML snapshot exceeds the file or remaining project text budget.") : content.Read;
            else read = new(null, "TooLarge", "The resource snapshot file budget was exhausted before this file could be analyzed.");
            if (read.Text is null)
            {
                truncated |= read.State == "TooLarge";
                if (read.State == "TooLarge") totalCharacters += Math.Max(0, Math.Min(maximumFileCharacters, remaining));
                incomplete = true;
                files.Add(new(path, project.FilePath!, project.Name, version, null, read.State, [], read.Status));
                continue;
            }
            totalCharacters += read.Text.Length;
            string hash = TextHash(read.Text);
            if (unavailable.Contains(project.Id) || ambiguous.Contains(project.FilePath!))
            {
                incomplete = true;
                files.Add(new(path, project.FilePath!, project.Name, version, hash, "Unavailable", [], ambiguous.Contains(project.FilePath!)
                    ? "This project path has multiple target-framework contexts; select an unambiguous workspace configuration."
                    : "Model source refresh is incomplete for this project or a referenced project; type diagnostics are withheld until it recovers."));
                continue;
            }
            var key = new BatchCacheKey(project.Id, path, hash, resources.Fingerprint(project));
            BatchAnalysis? analysis;
            lock (_gate) _xamlBatchCache.TryGetValue(key, out analysis);
            try
            {
                if (analysis is null)
                {
                    if (!compilations.TryGetValue(project.Id, out var compilation))
                        compilations[project.Id] = compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                    if (compilation is null)
                    {
                        incomplete = true;
                        files.Add(new(path, project.FilePath!, project.Name, version, hash, "Unavailable", [], "The project's type information is unavailable."));
                        continue;
                    }
                    // One extra diagnostic proves truncation; never cache an unbounded response.
                    var events = _xamlEvents.AnalyzeDetailed(path, read.Text, version ?? 0, compilation, 2001, cancellationToken);
                    var names = _xamlNames.AnalyzeDetailed(path, read.Text, version ?? 0, compilation, 2001, cancellationToken);
                    var diagnostics = _xamlSchema.AnalyzeBounded(path, read.Text, version ?? 0, compilation, 2001, cancellationToken)
                        .Concat(_xamlLanguage.AnalyzeBounded(path, read.Text, version ?? 0, compilation, 2001, cancellationToken, resources.Context(project, path)))
                        .Concat(events.Diagnostics).Concat(names.Diagnostics).Distinct().Take(2001)
                        .Select(diagnostic => diagnostic with { ProjectPath = project.FilePath, ProjectName = project.Name,
                            Message = diagnostic.Message.Length <= 2048 ? diagnostic.Message : diagnostic.Message[..2048] + "…" }).ToArray();
                    analysis = new(diagnostics, events.IsComplete && names.IsComplete,
                        JoinResourceStatus(JoinResourceStatus(events.Status, names.Status), resources.Status));
                    lock (_gate)
                        if (ReferenceEquals(solution, _solution) && revision == _semanticRevision)
                        {
                            while (_xamlBatchCache.Count > 0 && (_xamlBatchCache.Count >= 128
                                || _xamlBatchCache.Values.Sum(value => value.Diagnostics.Length) + diagnostics.Length > 8192))
                                _xamlBatchCache.Remove(_xamlBatchCache.Keys.First());
                            _xamlBatchCache[key] = analysis;
                        }
                }
                int room = maximumDiagnostics - diagnosticCount;
                var included = analysis.Diagnostics.Take(room).ToArray();
                bool diagnosticsOmitted = included.Length < analysis.Diagnostics.Length;
                truncated |= diagnosticsOmitted || !analysis.Complete;
                diagnosticCount += included.Length;
                files.Add(new(path, project.FilePath!, project.Name, version, hash, "Analyzed", included,
                    diagnosticsOmitted ? "Additional diagnostics were omitted because the project diagnostic budget was reached. " + analysis.Status : analysis.Status));
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                incomplete = true;
                files.Add(new(path, project.FilePath!, project.Name, version, hash, "Unavailable", [], $"XAML analysis could not complete ({exception.GetType().Name})."));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!ReferenceEquals(solution, _solution) || revision != _semanticRevision)
                return new(request.Generation, _semanticRevision, false, [], inventory.Length, Status: "Project types changed during XAML analysis; retry with the current buffers.");
            return new(request.Generation, revision, true, files, inventory.Length, truncated, truncated
                ? "Project XAML analysis has incomplete coverage; see each file's status for analysis limits or unsupported contexts."
                : incomplete ? "Some XAML files or project type contexts are unavailable; see each file's status." : null);
        }
    }

    // Call with _gate held. An unread model also invalidates projects that consume its symbols.
    private HashSet<ProjectId> UnavailableModelProjects(Solution solution)
    {
        var result = new HashSet<ProjectId>();
        var graph = solution.GetProjectDependencyGraph();
        foreach (var path in _diskUnavailable.Keys.Concat(_diskPending).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_syncedTexts.ContainsKey(path) || !_diskDocuments.TryGetValue(path, out var documents)) continue;
            foreach (var projectId in documents.Select(document => document.ProjectId).Distinct())
            {
                if (solution.GetProject(projectId) is null) continue;
                result.Add(projectId);
                result.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(projectId));
            }
        }
        return result;
    }

    private static async Task<DiskText> ReadBoundedTextAsync(string path, int maximumCharacters, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            // UTF-32 needs four bytes per character; reject before allocating for much larger files.
            if (stream.Length > (long)maximumCharacters * 4 + 4) return new(null, "TooLarge", "The source file exceeds the text read budget.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
            var text = new StringBuilder(Math.Min(maximumCharacters, 8192));
            var buffer = new char[8192];
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumCharacters - text.Length + 1)), cancellationToken).ConfigureAwait(false);
                if (read == 0) return new(text.ToString(), "Read");
                if (text.Length + read > maximumCharacters) return new(null, "TooLarge", "The source file exceeds the text read budget.");
                text.Append(buffer, 0, read);
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return new(null, "Missing", "The evaluated source file is missing from disk."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return new(null, "Unavailable", $"The source file could not be read ({exception.GetType().Name})."); }
    }
}
