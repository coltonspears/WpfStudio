using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    public async Task<XamlNameProjectionResult> ApplyXamlNameProjectionAsync(XamlNameProjectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Plan is not { CurrentSource: true } plan)
            return new(false, "This worker derives fields from current XAML. Reopen the rename review to replace its old compiler-baseline plan.");
        if (plan.ExpectedDocuments is not { Count: > 0 and <= 512 } || request.Documents is not { Count: <= 2048 }
            || plan.Baselines.Count != 0 || plan.BaselineSourceBytes.Length != 0 || plan.Steps.Count != 0)
            return new(false, "The current-source rename snapshot is invalid or exceeds its budget.");
        var updates = new Dictionary<string, UpdateDocumentRequest>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var update in request.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(update.Path);
            if (update.Version < 0 || update.Text is null || update.Text.Length > 2_000_000
                || (characters += update.Text.Length) > 16_000_000 || !updates.TryAdd(path, update with { Path = path }))
                return new(false, "The rename buffers are duplicated, invalid or exceed the text budget.");
        }
        if (!request.Replay)
            foreach (var expected in plan.ExpectedDocuments)
                if (!updates.TryGetValue(Path.GetFullPath(expected.Path), out var update) || TextHash(update.Text) != expected.TextHash)
                    return new(false, "The current buffers differ from the reviewed rename.");
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        Solution authored, original;
        long epoch;
        XamlPageProjectionInput[] inputs;
        lock (_gate)
        {
            authored = _authoredSolution ?? RequireSolution();
            original = RequireSolution();
            epoch = _pageProjectionEpoch;
            string pagePath = Path.GetFullPath(plan.XamlPath);
            var inventory = PageProjectionInventory();
            if (!inventory.Any(item => item.Path.Equals(pagePath, StringComparison.OrdinalIgnoreCase)))
                return new(false, "The reviewed page is no longer an evaluated WPF Page.");
            foreach (string unowned in updates.Keys.Where(path => !_xamlProjects.ContainsKey(path)
                && authored.GetDocumentIdsWithFilePath(path).IsEmpty).ToArray())
            {
                if (plan.ExpectedDocuments.Any(expected => Path.GetFullPath(expected.Path).Equals(unowned, StringComparison.OrdinalIgnoreCase)))
                    return new(false, "A reviewed source is no longer in the workspace.");
                updates.Remove(unowned);
            }
            foreach (var (path, update) in updates)
            {
                if (IsGenerated(path)) return new(false, "Generated output cannot be supplied as an authored buffer.");
                if (_versions.TryGetValue(path, out long version) && (version > update.Version
                    || version == update.Version && _syncedTexts.GetValueOrDefault(path) != update.Text))
                    return new(false, "A rename buffer version is stale or identifies different text.");
            }
            inputs = inventory.Select(item => new XamlPageProjectionInput(item.Project, item.Path,
                updates.GetValueOrDefault(item.Path)?.Text ?? _syncedTexts.GetValueOrDefault(item.Path)
                    ?? _pageProjectionTexts.GetValueOrDefault(item.Path) ?? "")).ToArray();
        }
        var candidate = authored;
        foreach (var (path, update) in updates)
            foreach (var id in candidate.GetDocumentIdsWithFilePath(path))
                candidate = candidate.WithDocumentText(id, SourceText.From(update.Text), PreservationMode.PreserveIdentity);
        var result = await XamlPageSemanticProjection.BuildAsync(candidate, inputs, cancellationToken).ConfigureAwait(false);
        try { await CheckRenameCompilerErrorsAsync(original, result.Solution, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException exception) { return new(false, exception.Message); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!ReferenceEquals(authored, _authoredSolution) || !ReferenceEquals(original, _solution) || epoch != _pageProjectionEpoch)
                return new(false, "Sources changed while synchronizing the reviewed rename. Retry with current buffers.");
            foreach (var (path, update) in updates)
            {
                _versions[path] = update.Version;
                _syncedTexts[path] = update.Text;
                _diskUnavailable.Remove(path);
                _diskPending.Remove(path);
                _pageProjectionReadStatuses.Remove(path);
            }
            _authoredSolution = candidate;
            _solution = result.Solution;
            _pageProjection = result;
            _pageProjectionPending = false;
            _pageProjectionEpoch++;
            _semanticRevision++;
            _nameProjectionStatuses.Clear();
            foreach (var page in result.Pages)
                if (page.Status is not null) _nameProjectionStatuses[page.Input.Path] = page.Status;
            _completions.Clear();
            _xamlBatchCache.Clear();
        }
        // No historical plan is needed: restart and undo replay authored buffers.
        return new(true, BaselineRefreshed: true);
    }

    private Task<XamlGeneratedNameBridgeResult?> TryResolveProjectedNameAsync(Project project, Compilation compilation,
        XamlGeneratedNameSource source, int? start, IFieldSymbol? field, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        XamlGeneratedNameBridgeResult? result = null;
        lock (_gate)
        {
            var page = _pageProjection?.Pages.SingleOrDefault(item => item.Input.ProjectId == project.Id
                && string.Equals(item.Input.Path, Path.GetFullPath(source.Path), StringComparison.OrdinalIgnoreCase));
            if (page is null || field is not null && (field.DeclaringSyntaxReferences is not [var syntax]
                || project.Solution.GetDocument(syntax.SyntaxTree)?.Id != page.ProjectionDocumentId))
                return Task.FromResult(result);
            XamlGeneratedNameBridgeResult Missing(string status) => new(XamlGeneratedNameBridgeState.Unavailable, Status: status);
            if (!ReferenceEquals(project.Solution, _solution) || _pageProjectionPending || page.Input.Text != source.Text)
                result = Missing("Current XAML fields are not synchronized with this buffer snapshot.");
            else
            {
                var matches = page.Fields.Where(item => (start is null || item.Start == start)
                    && (field is null || item.Name == field.Name && item.RootClass == field.ContainingType.ToDisplayString())).Take(2).ToArray();
                if (matches.Length == 0 && field is null) return Task.FromResult(result);
                if (matches.Length != 1) result = Missing("The current field has no unique authored XAML declaration.");
                else
                {
                    var match = matches[0];
                    var members = compilation.GetTypeByMetadataName(match.RootClass)?.GetMembers(match.Name);
                    if (members is not [IFieldSymbol current] || XamlPageSemanticProjection.TypeIdentity(current.Type) != match.TypeIdentity
                        || current.DeclaringSyntaxReferences is not [var reference]
                        || project.Solution.GetDocument(reference.SyntaxTree)?.Id != match.DocumentId
                        || field is not null && !SymbolEqualityComparer.Default.Equals(field, current))
                        result = Missing("Authored C# and current XAML do not establish one matching field.");
                    else result = new(XamlGeneratedNameBridgeState.Verified, current, source.Path, match.Start, match.Length);
                }
            }
        }
        return Task.FromResult<XamlGeneratedNameBridgeResult?>(result);
    }

    private async Task<IReadOnlyList<SourceLocation>?> GetProjectedNameDefinitionAsync(Project origin, IFieldSymbol field, CancellationToken token)
    {
        var project = field.Locations.Where(location => location.SourceTree is not null)
            .Select(location => origin.Solution.GetDocument(location.SourceTree!)?.Project).FirstOrDefault(item => item is not null);
        if (project is null || await project.GetCompilationAsync(token).ConfigureAwait(false) is not { } compilation) return null;
        XamlPageProjectionPage[] pages;
        lock (_gate) pages = _pageProjection?.Pages.Where(page => page.Input.ProjectId == project.Id).ToArray() ?? [];
        foreach (var page in pages)
        {
            var result = await TryResolveProjectedNameAsync(project, compilation, new(page.Input.Path, page.Input.Text), null, field, token).ConfigureAwait(false);
            if (result is null) continue;
            if (result.State != XamlGeneratedNameBridgeState.Verified) return [];
            var position = SourceText.From(page.Input.Text).Lines.GetLinePosition(result.Start);
            return [new(page.Input.Path, result.Start, result.Length, position.Line + 1, position.Character + 1, field.Name,
                page.TextHash, project.FilePath, project.Name)];
        }
        return null;
    }
}
