using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private readonly Dictionary<string, XamlNameProjectionModel> _nameProjections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameProjectionStatuses = new(StringComparer.OrdinalIgnoreCase);
    private long _nameProjectionLoadEpoch;

    private XamlNameProjectionModel? GetNameProjection(string path)
    { lock (_gate) return _nameProjections.GetValueOrDefault(Path.GetFullPath(path)); }

    private static bool SameProjectionPlan(XamlNameProjectionPlan a, XamlNameProjectionPlan b) =>
        a.XamlPath == b.XamlPath && a.BaselineSourceBytes == b.BaselineSourceBytes
        && a.Baselines.SequenceEqual(b.Baselines) && a.Steps.SequenceEqual(b.Steps)
        && a.ExpectedDocuments.SequenceEqual(b.ExpectedDocuments);

    public async Task<XamlNameProjectionResult> ApplyXamlNameProjectionAsync(XamlNameProjectionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Plan is not { } plan || plan.Baselines is not { Count: > 0 and <= 32 }
            || plan.Steps is not { Count: > 0 and <= 32 } || plan.ExpectedDocuments is not { Count: <= 512 }
            || plan.BaselineSourceBytes is not { Length: <= 5_600_000 } || request.Documents is not { Count: <= 2048 })
            return new(false, "The XAML name projection exceeds its replay budget.");
        string path = Path.GetFullPath(plan.XamlPath);
        var updates = new Dictionary<string, UpdateDocumentRequest>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var update in request.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string updatePath = Path.GetFullPath(update.Path);
            if (update.Version < 0 || update.Text is null || (characters += update.Text.Length) > 16_000_000
                || update.Text.Length > 2_000_000 || !updates.TryAdd(updatePath, update with { Path = updatePath }))
                return new(false, "The projection buffer snapshot is invalid, duplicated or exceeds its text budget.");
        }
        if (!request.Replay)
            foreach (var expected in plan.ExpectedDocuments)
                if (!updates.TryGetValue(Path.GetFullPath(expected.Path), out var update) || TextHash(update.Text) != expected.TextHash)
                    return new(false, "The accepted rename buffers differ from the projection's reviewed changes.");

        Solution original;
        long revision;
        HashSet<ProjectId> owningProjects;
        Dictionary<string, XamlNameProjectionModel> models;
        Dictionary<string, string> texts;
        lock (_gate)
        {
            original = RequireSolution(); revision = _semanticRevision;
            if (!_xamlProjects.ContainsKey(path)) return new(false, "The projection's XAML source is not in this workspace.");
            owningProjects = new(_xamlProjects[path]);
            if (owningProjects.Overlaps(UnavailableModelProjects(original)))
                return new(false, "A linked model context is unavailable for live name projection.");
            if (!_nameProjections.ContainsKey(path) && _nameProjections.Count >= 32)
                return new(false, "The workspace has reached its 32 projected XAML document limit. Build and reload to establish fresh metadata.");
            models = new(_nameProjections, StringComparer.OrdinalIgnoreCase);
            texts = new(_syncedTexts, StringComparer.OrdinalIgnoreCase);
            foreach (string unowned in updates.Keys.Where(updatePath => !_xamlProjects.ContainsKey(updatePath)
                && original.GetDocumentIdsWithFilePath(updatePath).IsEmpty).ToArray())
            {
                if (plan.ExpectedDocuments.Any(expected => Path.GetFullPath(expected.Path).Equals(unowned, StringComparison.OrdinalIgnoreCase)))
                    return new(false, "A reviewed projection target is no longer in this workspace.");
                updates.Remove(unowned); // Unrelated scratch tabs do not belong to this compilation.
            }
            foreach (var (updatePath, update) in updates)
            {
                if (IsGenerated(updatePath) || !_xamlProjects.ContainsKey(updatePath) && original.GetDocumentIdsWithFilePath(updatePath).IsEmpty)
                    return new(false, "A projection buffer is not an authored document in the current workspace.");
                if (_versions.TryGetValue(updatePath, out long version) && (version > update.Version
                    || version == update.Version && _syncedTexts.GetValueOrDefault(updatePath) != update.Text))
                    return new(false, "A projection buffer version is stale or identifies different text.");
                texts[updatePath] = update.Text;
            }
        }
        try
        {
            var model = models.TryGetValue(path, out var existing) && SameProjectionPlan(existing.Plan, plan)
                ? existing : await XamlNameProjection.BuildAsync(original, plan, cancellationToken).ConfigureAwait(false);
            if (!owningProjects.SetEquals(model.States[0].Mappings.Select(mapping => mapping.ProjectId)))
                return new(false, "The projection does not cover every owning XAML project context.");
            models[path] = model;
            if (models.Values.Sum(item => (long)item.Plan.BaselineSourceBytes.Length + item.States.Sum(state =>
                    (long)state.XamlText.Length + state.GeneratedTexts.Values.Sum(text => (long)text.Length))) > 64_000_000)
                return new(false, "Live generated-name history exceeds the workspace text budget. Build and reload to establish fresh metadata.");
            // A generated document must have one projection owner. Never let a
            // second page silently replace another page's semantic overlay.
            var owners = new HashSet<DocumentId>();
            foreach (var item in models.Values)
                foreach (var id in item.States[0].GeneratedTexts.Keys)
                    if (!owners.Add(id)) return new(false, "Two XAML projections require the same generated document; rebuild and reload.");
            var states = new Dictionary<string, XamlNameProjectionState>(StringComparer.OrdinalIgnoreCase);
            var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (modelPath, item) in models)
            {
                string? diskText = await VerifyProjectionBaselineAsync(item, cancellationToken).ConfigureAwait(false);
                string? currentText = texts.GetValueOrDefault(modelPath) ?? diskText;
                var state = item.States.LastOrDefault(state => state.XamlText == currentText);
                if (state is null)
                {
                    state = item.States[0];
                    statuses[modelPath] = "Live generated names are unavailable for these XAML edits. Undo to a verified rename state, or save, build and reload.";
                }
                states[modelPath] = state;
            }
            if (!request.Replay && (!texts.TryGetValue(path, out var acceptedText) || acceptedText != model.States[^1].XamlText))
                return new(false, "The reviewed XAML rename has not been applied to the current buffer.");
            var changed = original;
            foreach (var (updatePath, update) in updates)
                foreach (var id in changed.GetDocumentIdsWithFilePath(updatePath))
                    changed = changed.WithDocumentText(id, SourceText.From(update.Text), PreservationMode.PreserveIdentity);
            foreach (var state in states.Values)
                foreach (var (id, generatedText) in state.GeneratedTexts)
                {
                    if (changed.GetDocument(id) is null) return new(false, "A generated document changed during projection.");
                    changed = changed.WithDocumentText(id, generatedText, PreservationMode.PreserveIdentity);
                }
            foreach (var (modelPath, state) in states)
                if (!statuses.ContainsKey(modelPath) && !await ValidateProjectedStateAsync(changed, state, cancellationToken).ConfigureAwait(false))
                    return new(false, "The current authored C# changes no longer establish the projected XAML names.");
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!ReferenceEquals(_solution, original) || _semanticRevision != revision)
                    return new(false, "Project sources changed while reconstructing live name metadata. Synchronize and retry.");
                foreach (var (updatePath, update) in updates)
                {
                    _versions[updatePath] = update.Version; _syncedTexts[updatePath] = update.Text;
                    _diskUnavailable.Remove(updatePath); _diskPending.Remove(updatePath);
                }
                _nameProjections.Clear(); foreach (var pair in models) _nameProjections.Add(pair.Key, pair.Value);
                _nameProjectionStatuses.Clear(); foreach (var pair in statuses) _nameProjectionStatuses.Add(pair.Key, pair.Value);
                _solution = changed; _semanticRevision++; _completions.Clear(); _xamlBatchCache.Clear();
                return new(true, statuses.GetValueOrDefault(path));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or FormatException)
        {
            // MSBuild can legitimately regenerate markup while reopening the
            // workspace. Adopt it only when it independently proves the current
            // saved source. Never put an old overlay over a newer compiler result.
            if (request.Replay && GetNameProjection(path) is null &&
                await TryAdoptCompiledNamesAsync(path, updates, original, revision, owningProjects, cancellationToken).ConfigureAwait(false))
                return new(true, "Fresh compiler output now supplies these saved XAML names. The previous in-memory rename history has been retired.", BaselineRefreshed: true);
            return new(false, "Live XAML name metadata could not be reconstructed: " + exception.Message);
        }
    }

    private async Task<bool> ValidateProjectedStateAsync(Solution solution, XamlNameProjectionState state, CancellationToken token)
    {
        foreach (var context in state.Mappings.GroupBy(mapping => mapping.ProjectId))
        {
            var project = solution.GetProject(context.Key);
            if (project is null || await project.GetCompilationAsync(token).ConfigureAwait(false) is not { } compilation) return false;
            var names = _xamlNames.GetNameOccurrences(state.XamlText, compilation, token);
            if (!names.IsComplete || names.CoverageLimited) return false;
            foreach (var mapping in context)
            {
                var matches = names.Declarations.Where(declaration => declaration.IsRootScope && declaration.RootClass == mapping.RootClass
                    && declaration.Name == mapping.Name && declaration.Start == mapping.Start && declaration.Length == mapping.Length).Take(2).ToArray();
                if (matches is not [var name] || name.ElementType is not { } type || XamlNameProjection.TypeIdentity(type) != mapping.TypeIdentity
                    || compilation.GetTypeByMetadataName(mapping.RootClass)?.GetMembers(mapping.Name) is not [IFieldSymbol field]
                    || XamlNameProjection.TypeIdentity(field.Type) != mapping.TypeIdentity || field.DeclaringSyntaxReferences is not [var syntax]
                    || solution.GetDocument(syntax.SyntaxTree) is not { } document || !state.GeneratedTexts.ContainsKey(document.Id)) return false;
            }
        }
        return true;
    }

    private async Task<bool> TryAdoptCompiledNamesAsync(string path, Dictionary<string, UpdateDocumentRequest> updates,
        Solution original, long revision, HashSet<ProjectId> owners, CancellationToken token)
    {
        var read = await ReadBoundedTextAsync(path, 1_000_000, token).ConfigureAwait(false);
        if (read.Text is not { } savedText) return false;
        lock (_gate)
        {
            if (!ReferenceEquals(_solution, original) || _semanticRevision != revision) return false;
            string current = updates.GetValueOrDefault(path)?.Text ?? _syncedTexts.GetValueOrDefault(path) ?? savedText;
            if (current != savedText) return false;
        }
        var candidate = original;
        foreach (var (updatePath, update) in updates)
            foreach (var id in candidate.GetDocumentIdsWithFilePath(updatePath))
                candidate = candidate.WithDocumentText(id, SourceText.From(update.Text), PreservationMode.PreserveIdentity);
        var checkedDocuments = new HashSet<DocumentId>();
        long proofCharacters = 0;
        foreach (var owner in owners)
        {
            var project = candidate.GetProject(owner);
            if (project is null || await project.GetCompilationAsync(token).ConfigureAwait(false) is not { } compilation) return false;
            var names = _xamlNames.GetNameOccurrences(savedText, compilation, token);
            proofCharacters += savedText.Length;
            if (!names.IsComplete || names.CoverageLimited) return false;
            int fields = 0;
            foreach (var declaration in names.Declarations.Where(item => item.IsRootScope && item.RootClass is not null))
            {
                if (declaration.RootType?.GetMembers(declaration.Name) is not [IFieldSymbol candidateField]
                    || candidateField.DeclaringSyntaxReferences is not [var candidateSyntax]) return false;
                proofCharacters += 2L * savedText.Length + (await candidateSyntax.SyntaxTree.GetTextAsync(token).ConfigureAwait(false)).Length;
                if (proofCharacters > 32_000_000) return false;
                var proof = await XamlGeneratedNameBridge.FromDeclarationAsync(project, compilation, new(path, savedText), declaration.Start, token).ConfigureAwait(false);
                if (proof.State != XamlGeneratedNameBridgeState.Verified || proof.Field?.DeclaringSyntaxReferences is not [var syntax]
                    || candidate.GetDocument(syntax.SyntaxTree) is not { } generated || generated.FilePath is not { } generatedPath) return false;
                if (checkedDocuments.Add(generated.Id))
                {
                    var disk = await ReadBoundedTextAsync(generatedPath, 2_000_000, token).ConfigureAwait(false);
                    if (disk.Text is null || (await generated.GetTextAsync(token).ConfigureAwait(false)).ToString() != disk.Text) return false;
                }
                fields++;
            }
            if (fields == 0) return false;
        }
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!ReferenceEquals(_solution, original) || _semanticRevision != revision) return false;
            foreach (var (updatePath, update) in updates)
            {
                _versions[updatePath] = update.Version; _syncedTexts[updatePath] = update.Text;
                _diskUnavailable.Remove(updatePath); _diskPending.Remove(updatePath);
            }
            _solution = candidate; _nameProjections.Remove(path); _nameProjectionStatuses.Remove(path);
            _semanticRevision++; _completions.Clear(); _xamlBatchCache.Clear();
            return true;
        }
    }

    private static async Task<string?> VerifyProjectionBaselineAsync(XamlNameProjectionModel model, CancellationToken token)
    {
        foreach (var baseline in model.Plan.Baselines)
        {
            var read = await ReadBoundedTextAsync(baseline.Path, 2_000_000, token).ConfigureAwait(false);
            if (read.Text is null || TextHash(read.Text) != baseline.TextHash)
                throw new InvalidOperationException("The generated compiler baseline changed. Reload the workspace before using live name metadata.");
        }
        var source = await ReadBoundedTextAsync(model.Plan.XamlPath, 1_000_000, token).ConfigureAwait(false);
        if (source.Text is null || !model.States.Any(state => state.XamlText == source.Text))
            throw new InvalidOperationException("The saved XAML differs from every verified name state. Build and reload to establish a new baseline.");
        return source.Text;
    }

    private async Task<DocumentUpdateResult> UpdateXamlProjectionDocumentAsync(UpdateDocumentRequest request, CancellationToken token)
    {
        string path = Path.GetFullPath(request.Path);
        XamlNameProjectionModel? model;
        lock (_gate)
        {
            if (!_xamlProjects.ContainsKey(path)) return new(false, request.Version, []);
            if (_versions.TryGetValue(path, out long version))
            {
                if (version > request.Version) return new(false, version, []);
                if (version == request.Version && _syncedTexts.GetValueOrDefault(path) != request.Text)
                    throw new InvalidOperationException("A document version cannot identify different text. Increment its version before updating.");
                if (version == request.Version) return new(true, version, []);
            }
            model = _nameProjections.GetValueOrDefault(path);
            if (model is null)
            {
                _versions[path] = request.Version; _syncedTexts[path] = request.Text;
                _semanticRevision++; _completions.Clear(); _xamlBatchCache.Clear();
                return new(true, request.Version, []);
            }
        }
        var result = await ApplyXamlNameProjectionAsync(new(model.Plan, [request with { Analyze = false }], Replay: true), token).ConfigureAwait(false);
        if (!result.Accepted) throw new InvalidOperationException(result.Status);
        return new(true, request.Version, []);
    }

    private async Task CloseXamlProjectionDocumentAsync(string path, CancellationToken token)
    {
        long? version; string? synchronizedText; long epoch; XamlNameProjectionModel? model;
        lock (_gate)
        {
            version = _versions.TryGetValue(path, out var current) ? current : null;
            synchronizedText = _syncedTexts.GetValueOrDefault(path); epoch = _nameProjectionLoadEpoch;
            model = _nameProjections.GetValueOrDefault(path);
        }
        var read = await ReadBoundedTextAsync(path, 1_000_000, token).ConfigureAwait(false);
        XamlNameProjectionState? state = model?.States.LastOrDefault(item => item.XamlText == read.Text);
        string? baselineError = null;
        if (model is not null)
        {
            try { await VerifyProjectionBaselineAsync(model, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            { baselineError = exception.Message; }
        }
        lock (_gate)
        {
            if (epoch != _nameProjectionLoadEpoch || (_versions.TryGetValue(path, out var current) ? (long?)current : null) != version
                || _syncedTexts.GetValueOrDefault(path) != synchronizedText || _nameProjections.GetValueOrDefault(path) != model) return;
            if (model is not null && _solution is { } solution)
            {
                if (baselineError is null)
                    foreach (var (id, text) in (state ?? model.States[0]).GeneratedTexts)
                        if (solution.GetDocument(id) is not null) solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                _solution = solution;
                if (baselineError is not null) _nameProjectionStatuses[path] = baselineError;
                else if (state is null) _nameProjectionStatuses[path] = "The saved XAML is outside the verified name states. Build and reload to refresh generated names.";
                else _nameProjectionStatuses.Remove(path);
            }
            _versions.Remove(path); _syncedTexts.Remove(path);
            _semanticRevision++; _completions.Clear(); _xamlBatchCache.Clear();
        }
    }

    private async Task<XamlGeneratedNameBridgeResult?> TryResolveProjectedNameAsync(Project project, Compilation compilation,
        XamlGeneratedNameSource source, int? start, IFieldSymbol? field, CancellationToken token)
    {
        string path = Path.GetFullPath(source.Path);
        var model = GetNameProjection(path);
        if (model is null) return null;
        bool relevant = field is null || field.DeclaringSyntaxReferences is [var declaration]
            && project.Solution.GetDocument(declaration.SyntaxTree) is { } fieldDocument
            && model.States[0].GeneratedTexts.ContainsKey(fieldDocument.Id);
        if (!relevant) return null;
        XamlGeneratedNameBridgeResult Missing(string reason) => new(XamlGeneratedNameBridgeState.Unavailable, Status: reason);
        var state = model.States.LastOrDefault(state => state.XamlText == source.Text);
        if (state is null) return Missing("The current XAML buffer is outside the verified generated-name states. Undo, or build and reload.");
        try { await VerifyProjectionBaselineAsync(model, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return Missing(exception.Message); }
        foreach (var (id, expected) in state.GeneratedTexts)
        {
            var generated = project.Solution.GetDocument(id);
            if (generated is null || !(await generated.GetTextAsync(token).ConfigureAwait(false)).ContentEquals(expected))
                return Missing("The generated name projection is not synchronized with this XAML snapshot.");
        }
        var matches = state.Mappings.Where(mapping => mapping.ProjectId == project.Id
            && (start is null || mapping.Start == start) && (field is null || mapping.Name == field.Name
                && mapping.RootClass == field.ContainingType.ToDisplayString())).Take(2).ToArray();
        if (matches.Length == 0 && field is null) return null; // Template/local identities still use their ordinary resolver.
        if (matches.Length != 1) return Missing("The projected name does not have one verified authored declaration.");
        var match = matches[0];
        var owner = compilation.GetTypeByMetadataName(match.RootClass);
        if (owner?.GetMembers(match.Name) is not [IFieldSymbol current]
            || XamlNameProjection.TypeIdentity(current.Type) != match.TypeIdentity
            || field is not null && !SymbolEqualityComparer.Default.Equals(field, current))
            return Missing("The projected field's current type or declaration no longer matches its authored name.");
        var names = _xamlNames.GetNameOccurrences(source.Text, compilation, token);
        if (!names.IsComplete || !names.Declarations.Any(declaration => declaration.IsRootScope
            && declaration.RootClass == match.RootClass && declaration.Name == match.Name && declaration.Start == match.Start
            && declaration.Length == match.Length && declaration.ElementType is { } elementType
            && XamlNameProjection.TypeIdentity(elementType) == match.TypeIdentity))
            return Missing("The current XAML metadata no longer establishes the projected name declaration.");
        lock (_gate)
            if (!ReferenceEquals(_solution, project.Solution) || !ReferenceEquals(_nameProjections.GetValueOrDefault(path), model))
                return Missing("The project changed while resolving the projected name.");
        return new(XamlGeneratedNameBridgeState.Verified, current, path, match.Start, match.Length);
    }

    private async Task<IReadOnlyList<SourceLocation>?> GetProjectedNameDefinitionAsync(Project origin, IFieldSymbol field, CancellationToken token)
    {
        var project = field.Locations.Where(location => location.SourceTree is not null)
            .Select(location => origin.Solution.GetDocument(location.SourceTree!)?.Project).FirstOrDefault(item => item is not null);
        if (project is null) return null;
        XamlNameProjectionModel[] models; Dictionary<string, string> synced;
        lock (_gate) { models = _nameProjections.Values.ToArray(); synced = new(_syncedTexts, StringComparer.OrdinalIgnoreCase); }
        if (await project.GetCompilationAsync(token).ConfigureAwait(false) is not { } compilation) return null;
        foreach (var model in models)
        {
            string path = model.Plan.XamlPath;
            string? text = synced.GetValueOrDefault(path) ?? (await ReadBoundedTextAsync(path, 1_000_000, token).ConfigureAwait(false)).Text;
            if (text is null) continue;
            var result = await TryResolveProjectedNameAsync(project, compilation, new(path, text), null, field, token).ConfigureAwait(false);
            if (result is null) continue;
            if (result.State != XamlGeneratedNameBridgeState.Verified) return [];
            var position = SourceText.From(text).Lines.GetLinePosition(result.Start);
            return [new(path, result.Start, result.Length, position.Line + 1, position.Character + 1, field.Name,
                TextHash(text), project.FilePath, project.Name)];
        }
        return null;
    }
}
