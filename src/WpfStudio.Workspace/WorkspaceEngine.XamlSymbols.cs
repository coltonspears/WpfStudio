using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    public event EventHandler? SemanticStateChanged;

    private const int SymbolScanFiles = 512;
    private const int SymbolScanCharacters = 8_000_000;
    private const int SymbolScanOccurrences = 16_384;
    private sealed record SymbolFile(Project Project, string Path, string Text, long Version,
        IReadOnlyList<XamlSymbolOccurrence> Occurrences, Lazy<XamlNameOccurrenceResult> NameAnalysis)
    {
        public XamlNameOccurrenceResult Names => NameAnalysis.Value;
    }
    private sealed record SymbolSnapshot(Solution Solution, long Revision, Dictionary<string, long> Versions,
        IReadOnlyList<SymbolFile> Files, List<string> Warnings, bool Complete, ResourceSnapshot Resources);

    public async Task<SymbolReferenceResult> FindSymbolReferencesAsync(SymbolReferenceRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await CaptureSymbolSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
        var name = await ResolveRequestedNameAsync(snapshot, request, cancellationToken).ConfigureAwait(false);
        if (name is not null) return await FindNameReferencesAsync(snapshot, name, cancellationToken).ConfigureAwait(false);
        var symbol = await ResolveRequestedSymbolAsync(snapshot, request, cancellationToken).ConfigureAwait(false);
        if (symbol is null) { EnsureSymbolSnapshotCurrent(snapshot); return new([], BoundedWarnings(snapshot.Warnings), false); }
        var references = (await SymbolFinder.FindReferencesAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false)).ToArray();
        var family = await SymbolFamilyAsync(symbol, references, snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var locations = new List<SourceLocation>();
        var csharpTexts = new Dictionary<DocumentId, (SourceText Text, string Hash)>();
        foreach (var reference in references.SelectMany(item => item.Locations))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reference.Location.IsInSource || reference.Document.FilePath is not { } path || IsGenerated(path)) continue;
            if (locations.Count >= SymbolScanOccurrences) { snapshot.Warnings.Add("Reference results reached the 16,384-location limit."); break; }
            if (!csharpTexts.TryGetValue(reference.Document.Id, out var content))
            {
                var text = await reference.Document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                csharpTexts[reference.Document.Id] = content = (text, TextHash(text.ToString()));
            }
            var span = reference.Location.SourceSpan;
            var line = content.Text.Lines.GetLinePosition(span.Start);
            locations.Add(new(path, span.Start, span.Length, line.Line + 1, line.Character + 1, symbol.Name,
                content.Hash, reference.Document.Project.FilePath, reference.Document.Project.Name));
        }
        var identities = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
        var xamlTexts = new Dictionary<string, (SourceText Text, string Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
        if (!xamlTexts.TryGetValue(file.Path, out var content))
            xamlTexts[file.Path] = content = (SourceText.From(file.Text), TextHash(file.Text));
        foreach (var occurrence in file.Occurrences)
        {
            if (!family.ContainsKey(await IdentityAsync(occurrence.Symbol, snapshot.Solution, identities, cancellationToken).ConfigureAwait(false) ?? "")) continue;
            if (locations.Count >= SymbolScanOccurrences) { snapshot.Warnings.Add("Reference results reached the 16,384-location limit."); break; }
            var line = content.Text.Lines.GetLinePosition(occurrence.Start);
            locations.Add(new(file.Path, occurrence.Start, occurrence.Length, line.Line + 1, line.Character + 1,
                symbol.Name, content.Hash, file.Project.FilePath, file.Project.Name));
        }
        }
        EnsureSymbolSnapshotCurrent(snapshot);
        return new(locations.Distinct().ToArray(), BoundedWarnings(snapshot.Warnings));
    }

    private async Task<WorkspaceEditResult> RenameWithXamlAsync(RenameRequest request, CancellationToken token)
    {
        var origin = new SymbolReferenceRequest(request.Path, request.Position, request.Version, request.Text, request.ProjectPath, request.XamlOverlays);
        var snapshot = await CaptureSymbolSnapshotAsync(origin, token).ConfigureAwait(false);
        var name = await ResolveRequestedNameAsync(snapshot, origin, token).ConfigureAwait(false);
        if (name is not null) return await RenameNameAsync(snapshot, name, request.NewName, token).ConfigureAwait(false);
        if (!SyntaxFacts.IsValidIdentifier(request.NewName) || request.NewName.StartsWith('@'))
            throw new ArgumentException("The new name must be an unescaped C# identifier.", nameof(request));
        var symbol = await ResolveRequestedSymbolAsync(snapshot, origin, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No verified symbol at the caret.");
        symbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, snapshot.Solution, token).ConfigureAwait(false) ?? symbol;
        if (symbol.Locations.All(location => !location.IsInSource)) throw new InvalidOperationException("External symbols cannot be renamed.");
        if (symbol.Locations.Any(location => location.SourceTree is { } tree &&
            (IsGenerated(tree.FilePath) || snapshot.Solution.GetDocument(tree) is null)))
            throw new InvalidOperationException("Rename the authored source declaration rather than a generated member.");
        bool supportsXaml = symbol is IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary };
        if (supportsXaml && !snapshot.Complete)
            throw new InvalidOperationException("Rename requires every evaluated XAML file and model context to be available within the scan budget. " + string.Join(" ", BoundedWarnings(snapshot.Warnings)));
        var references = (await SymbolFinder.FindReferencesAsync(symbol, snapshot.Solution, token).ConfigureAwait(false)).ToArray();
        var family = await SymbolFamilyAsync(symbol, references, snapshot.Solution, token).ConfigureAwait(false);
        var changed = await Renamer.RenameSymbolAsync(snapshot.Solution, symbol, new SymbolRenameOptions(), request.NewName, token).ConfigureAwait(false);
        var edits = new Dictionary<string, DocumentEdits>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectChange in changed.GetChanges(snapshot.Solution).GetProjectChanges())
        foreach (var id in projectChange.GetChangedDocuments())
        {
            var before = snapshot.Solution.GetDocument(id)!;
            if (before.FilePath is not { } path) continue;
            if (IsGenerated(path)) { snapshot.Warnings.Add($"Generated document excluded: {path}"); continue; }
            RequireWritableSymbolFile(path);
            var textChanges = await changed.GetDocument(id)!.GetTextChangesAsync(before, token).ConfigureAwait(false);
            string text = (await before.GetTextAsync(token).ConfigureAwait(false)).ToString();
            var edit = new DocumentEdits(path, snapshot.Versions.GetValueOrDefault(path), textChanges.Select(ToEdit).ToArray(), TextHash(text));
            if (edits.TryGetValue(path, out var previous) && (previous.ExpectedTextHash != edit.ExpectedTextHash || !previous.Edits.SequenceEqual(edit.Edits)))
                throw new InvalidOperationException($"Linked C# contexts produce conflicting edits for {path}.");
            edits[path] = edit;
        }
        await CheckRenameCompilerErrorsAsync(snapshot.Solution, changed, token).ConfigureAwait(false);
        if (supportsXaml)
        {
            var identities = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
            var renamedIdentities = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
            var renamedFamily = await RenamedFamilyAsync(family.Values, snapshot.Solution, changed, request.NewName, token).ConfigureAwait(false);
            var compilations = new Dictionary<ProjectId, Compilation>();
            var groups = snapshot.Files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var replacementSets = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var contexts = group.ToArray();
                var occurrenceMaps = contexts.ToDictionary(context => context,
                    context => context.Occurrences.ToLookup(occurrence => (occurrence.Start, occurrence.Length)));
                var spans = new HashSet<(int Start, int Length)>();
                foreach (var context in contexts)
                foreach (var occurrence in context.Occurrences)
                    if (family.ContainsKey(await IdentityAsync(occurrence.Symbol, snapshot.Solution, identities, token).ConfigureAwait(false) ?? ""))
                        spans.Add((occurrence.Start, occurrence.Length));
                var replacements = new List<TextEdit>();
                foreach (var span in spans.OrderBy(span => span.Start))
                {
                    foreach (var context in contexts)
                    {
                        var matching = occurrenceMaps[context][span].ToArray();
                        if (matching.Length != 1 || !family.ContainsKey(await IdentityAsync(matching[0].Symbol, snapshot.Solution, identities, token).ConfigureAwait(false) ?? ""))
                            throw new InvalidOperationException($"Linked XAML contexts disagree about the symbol at offset {span.Start} in {group.Key}; rename was withheld.");
                    }
                    replacements.Add(new(span.Start, span.Length, request.NewName));
                }
                replacementSets.Add(group.Key, replacements);
            }
            // Rebind every consumer against the complete proposed dictionary text,
            // including edits in dependencies processed later in inventory order.
            var afterTexts = groups.ToDictionary(group => group.Key,
                group => ApplySymbolEdits(group.First().Text, replacementSets[group.Key]), StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var contexts = group.ToArray();
                var first = contexts[0];
                var replacements = replacementSets[group.Key];
                var replacementMap = replacements.ToDictionary(edit => (edit.Start, edit.Length));
                if (replacements.Count > 0) RequireWritableSymbolFile(first.Path);
                string after = afterTexts[first.Path];
                foreach (var context in contexts)
                {
                    if (!compilations.TryGetValue(context.Project.Id, out var compilation))
                        compilations[context.Project.Id] = compilation = await changed.GetProject(context.Project.Id)!.GetCompilationAsync(token).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Renamed project types are unavailable.");
                    var rebound = ReadSymbolOccurrences(after, compilation, token, snapshot.Resources.Context(context.Project, context.Path, afterTexts));
                    if (rebound.CoverageLimited) throw new InvalidOperationException("Renamed XAML could not be checked within the symbol-analysis budget.");
                    var reboundMap = rebound.Occurrences.ToLookup(occurrence => (occurrence.Start, occurrence.Length, occurrence.Kind));
                    int replacementIndex = 0, delta = 0;
                    // A newly hidden base member can change an untouched binding,
                    // including one in a file with no proposed replacements.
                    foreach (var occurrence in context.Occurrences.OrderBy(occurrence => occurrence.Start))
                    {
                        while (replacementIndex < replacements.Count && replacements[replacementIndex].Start + replacements[replacementIndex].Length <= occurrence.Start)
                        {
                            var earlier = replacements[replacementIndex++];
                            delta += earlier.NewText.Length - earlier.Length;
                        }
                        replacementMap.TryGetValue((occurrence.Start, occurrence.Length), out var replacement);
                        int position = occurrence.Start + delta;
                        int length = replacement?.NewText.Length ?? occurrence.Length;
                        var matches = reboundMap[(position, length, occurrence.Kind)].ToArray();
                        string? identity = matches.Length == 1 ? await IdentityAsync(matches[0].Symbol, changed, renamedIdentities, token).ConfigureAwait(false) : null;
                        bool correct = identity is not null && (replacement is not null ? renamedFamily.Contains(identity)
                            : identity == await IdentityAsync(occurrence.Symbol, snapshot.Solution, identities, token).ConfigureAwait(false));
                        if (!correct) throw new InvalidOperationException($"Renaming would change or lose the XAML symbol binding in {context.Path}; choose another name.");
                    }
                }
                // Even a file without matches is a prerequisite: an external edit
                // during review must not introduce an unseen reference silently.
                edits[first.Path] = new(first.Path, first.Version, replacements, TextHash(first.Text));
            }
            snapshot.Warnings.Add("Only verified XAML binding property segments and event handler values are updated. Review dynamic bindings, resource keys, x:Class, generated markup and reflection/string references.");
        }
        else snapshot.Warnings.Add("This symbol uses C# rename only. XAML type names, x:Class, namescopes, resource keys and reflection/string references are not updated.");
        EnsureSymbolSnapshotCurrent(snapshot);
        return new(edits.Values.ToArray(), BoundedWarnings(snapshot.Warnings));
    }

    private async Task<SymbolSnapshot> CaptureSymbolSnapshotAsync(SymbolReferenceRequest request, CancellationToken token)
    {
        var refresh = await RefreshDiskDocumentsAsync(new(), token).ConfigureAwait(false);
        // Public refresh calls are reported by WorkspaceClient after their response.
        // This implicit refresh must notify independently, even if the rest of the
        // symbol operation fails or is cancelled. Both refresh locks are released.
        if (refresh.Changed) SemanticStateChanged?.Invoke(this, EventArgs.Empty);
        Solution solution;
        long revision;
        Dictionary<string, long> versions;
        XamlDocumentOverlay[] synchronizedXaml;
        (Project Project, string Path)[] inventory;
        HashSet<ProjectId> unavailable;
        var warnings = new List<string>();
        bool complete = refresh.Accepted && !refresh.Truncated;
        if (!complete) warnings.Add(refresh.Status ?? "Model source refresh is incomplete.");
        lock (_gate)
        {
            solution = RequireSolution(); revision = _semanticRevision;
            versions = new(_versions, StringComparer.OrdinalIgnoreCase);
            synchronizedXaml = _syncedTexts.Where(pair => pair.Key.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Select(pair => new XamlDocumentOverlay(pair.Key, pair.Value, _versions.GetValueOrDefault(pair.Key))).ToArray();
            unavailable = UnavailableModelProjects(solution);
            if (solution.Projects.Any(project => _xamlInventoryUnavailable.Contains(project.Id) || !_xamlInventory.ContainsKey(project.Id)))
            { complete = false; warnings.Add("Evaluated XAML inventory is unavailable; reload the workspace."); }
            inventory = solution.Projects.SelectMany(project => _xamlInventory.GetValueOrDefault(project.Id, []).Select(path => (project, path))).ToArray();
        }
        var overlays = new Dictionary<string, XamlDocumentOverlay>(StringComparer.OrdinalIgnoreCase);
        if (request.XamlOverlays is { Count: > 2048 }) throw new InvalidOperationException("The XAML overlay snapshot exceeds the 2,048-file limit.");
        long overlayCharacters = 0;
        foreach (var overlay in request.XamlOverlays ?? [])
        {
            token.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(overlay.Path);
            overlayCharacters += overlay.Text.Length;
            if (overlayCharacters > SymbolScanCharacters * 2L) throw new InvalidOperationException("The XAML overlay snapshot exceeds the text budget.");
            if (overlays.TryGetValue(path, out var previous) && (previous.Text != overlay.Text || previous.Version != overlay.Version))
                throw new InvalidOperationException("Conflicting XAML overlays identify the same file.");
            overlays[path] = overlay;
        }
        foreach (var overlay in synchronizedXaml)
            overlays.TryAdd(overlay.Path, overlay);
        string origin = Path.GetFullPath(request.Path);
        if (origin.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Text is { } text)
            {
                if (overlays.TryGetValue(origin, out var previous) && (previous.Text != text || previous.Version != request.Version))
                    throw new InvalidOperationException("The selected XAML text differs from its open-buffer snapshot.");
                overlays[origin] = new(origin, text, request.Version);
            }
            else if (overlays.TryGetValue(origin, out var overlay) && overlay.Version != request.Version)
                throw new InvalidOperationException("The selected XAML version differs from its open-buffer snapshot.");
            else if (!overlays.ContainsKey(origin) && request.Version != 0)
                throw new InvalidOperationException("An open XAML document requires its current text or overlay snapshot.");
        }
        if (inventory.Length > SymbolScanFiles) { complete = false; warnings.Add("XAML reference coverage exceeds the 512-file/context limit."); }
        var resources = await CaptureXamlResourcesAsync(solution, solution.ProjectIds, overlays.Values.ToArray(), token).ConfigureAwait(false);
        complete &= resources.Complete;
        if (resources.Status is not null) warnings.Add(resources.Status);
        var files = new List<SymbolFile>();
        var texts = new Dictionary<string, (string Text, long Version)>(StringComparer.OrdinalIgnoreCase);
        var compilations = new Dictionary<ProjectId, Compilation?>();
        int characters = 0, occurrences = 0;
        foreach (var (project, path) in inventory.Take(SymbolScanFiles))
        {
            token.ThrowIfCancellationRequested();
            if (unavailable.Contains(project.Id)) { complete = false; warnings.Add($"Model types are unavailable for {project.Name}."); continue; }
            if (!texts.TryGetValue(path, out var content))
            {
                overlays.TryGetValue(path, out var overlay);
                int budget = Math.Min(1_000_000, SymbolScanCharacters - characters);
                var read = resources.Texts.TryGetValue(path, out var captured) ? captured.Read
                    : new DiskText(null, "TooLarge", "The resource snapshot omitted this file.");
                if (read.Text is { } readText && readText.Length > budget) read = new(null, "TooLarge", "The XAML symbol text budget is exhausted.");
                if (read.Text is null) { complete = false; warnings.Add($"XAML reference coverage unavailable for {path}: {read.Status ?? read.State}."); continue; }
                characters += read.Text.Length;
                texts[path] = content = (read.Text, overlay?.Version ?? 0);
            }
            if (!compilations.TryGetValue(project.Id, out var compilation))
                compilations[project.Id] = compilation = await project.GetCompilationAsync(token).ConfigureAwait(false);
            if (compilation is null) { complete = false; warnings.Add($"Compilation unavailable for {project.Name}."); continue; }
            var result = ReadSymbolOccurrences(content.Text, compilation, token, resources.Context(project, path));
            warnings.AddRange(result.Warnings.Select(warning => $"{Path.GetFileName(path)} ({project.Name}): {warning}"));
            if (result.CoverageLimited) complete = false;
            occurrences += result.Occurrences.Count;
            if (occurrences > SymbolScanOccurrences) { complete = false; warnings.Add("XAML reference coverage exceeds the 16,384-occurrence limit."); break; }
            // Name coverage is independent: unsupported name consumers must not
            // disable an otherwise verified property/event refactoring.
            string nameText = content.Text;
            Compilation nameCompilation = compilation;
            files.Add(new(project, path, content.Text, content.Version, result.Occurrences,
                new(() => _xamlNames.GetNameOccurrences(nameText, nameCompilation, token))));
        }
        var snapshot = new SymbolSnapshot(solution, revision, versions, files, warnings, complete, resources);
        EnsureSymbolSnapshotCurrent(snapshot);
        return snapshot;
    }

    private XamlSymbolOccurrenceResult ReadSymbolOccurrences(string text, Compilation compilation, CancellationToken token, XamlResourceContext? resources = null)
    {
        var bindings = _xamlLanguage.GetSymbolOccurrences(text, compilation, token, resources);
        var events = _xamlEvents.GetSymbolOccurrences(text, compilation, token);
        return new(bindings.Occurrences.Concat(events.Occurrences).ToArray(), bindings.IsComplete && events.IsComplete,
            bindings.Warnings.Concat(events.Warnings).Distinct().ToArray(), bindings.CoverageLimited || events.CoverageLimited);
    }

    private async Task<ISymbol?> ResolveRequestedSymbolAsync(SymbolSnapshot snapshot, SymbolReferenceRequest request, CancellationToken token)
    {
        string path = Path.GetFullPath(request.Path);
        string? projectPath = request.ProjectPath is null ? null : Path.GetFullPath(request.ProjectPath);
        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            var contexts = snapshot.Files.Where(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                (projectPath is null || string.Equals(projectPath, file.Project.FilePath, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (contexts.Length != 1) throw new InvalidOperationException("Select an unambiguous, available XAML project context before finding or renaming this symbol.");
            ValidatePosition(request.Position, contexts[0].Text.Length);
            var matches = contexts[0].Occurrences.Where(item => request.Position >= item.Start && request.Position <= item.Start + item.Length).ToArray();
            return matches.Length == 1 ? matches[0].Symbol : null;
        }
        var documents = snapshot.Solution.GetDocumentIdsWithFilePath(path).Select(snapshot.Solution.GetDocument).OfType<Document>()
            .Where(document => projectPath is null || string.Equals(document.Project.FilePath, projectPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (documents.Length != 1) throw new InvalidOperationException("Select an unambiguous C# project context for this symbol.");
        if (snapshot.Versions.GetValueOrDefault(path) != request.Version) throw new InvalidOperationException("Document version is stale; synchronize the buffer and retry.");
        string text = (await documents[0].GetTextAsync(token).ConfigureAwait(false)).ToString();
        if (request.Text is not null && request.Text != text) throw new InvalidOperationException("The source buffer differs from the synchronized language-service snapshot.");
        ValidatePosition(request.Position, text.Length);
        return await SymbolFinder.FindSymbolAtPositionAsync(documents[0], request.Position, token).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, ISymbol>> SymbolFamilyAsync(ISymbol symbol, IEnumerable<ReferencedSymbol> references,
        Solution solution, CancellationToken token)
    {
        var family = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        var cache = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
        foreach (var member in references.Select(reference => reference.Definition).Prepend(symbol))
        {
            var source = await SymbolFinder.FindSourceDefinitionAsync(member, solution, token).ConfigureAwait(false) ?? member;
            if (await IdentityAsync(source, solution, cache, token).ConfigureAwait(false) is { } key) family[key] = source.OriginalDefinition;
        }
        return family;
    }

    private static async Task<string?> IdentityAsync(ISymbol symbol, Solution solution, Dictionary<ISymbol, string?> cache, CancellationToken token)
    {
        symbol = symbol.OriginalDefinition;
        if (cache.TryGetValue(symbol, out var key)) return key;
        var source = (await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, token).ConfigureAwait(false) ?? symbol).OriginalDefinition;
        string? id = DocumentationCommentId.CreateDeclarationId(source);
        // Declaration IDs distinguish member signatures and remain stable when a
        // different declaration earlier in the file changes length. Project and
        // physical source identity distinguish otherwise identical linked contexts.
        string declarations = string.Join(";", source.Locations.Where(location => location.IsInSource && location.SourceTree is not null)
            .Select(location => SnapshotDeclaration(location)).Order(StringComparer.Ordinal));
        string SnapshotDeclaration(Location location) => solution.GetDocument(location.SourceTree!)?.Project.Id + ":" +
            Path.GetFullPath(location.SourceTree!.FilePath).ToUpperInvariant() +
            (id is null ? ":" + location.SourceSpan.Start + ":" + location.SourceSpan.Length : "");
        key = id is null && declarations.Length == 0 ? null : source.ContainingAssembly?.Identity + "|" + id + "|" + declarations;
        cache[symbol] = key;
        return key;
    }

    private static async Task<HashSet<string>> RenamedFamilyAsync(IEnumerable<ISymbol> family, Solution before, Solution after, string name, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var cache = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
        foreach (var member in family)
        foreach (var location in member.Locations.Where(location => location.IsInSource && location.SourceTree is not null))
        {
            var original = before.GetDocument(location.SourceTree!);
            if (original is null || after.GetDocument(original.Id) is not { } changed) continue;
            var changes = (await changed.GetTextChangesAsync(original, token).ConfigureAwait(false)).OrderBy(change => change.Span.Start).ToArray();
            int position = location.SourceSpan.Start, delta = 0;
            foreach (var change in changes)
            {
                if (change.Span.Start > position) break;
                if (position < change.Span.End) { position = change.Span.Start; break; }
                delta += (change.NewText?.Length ?? 0) - change.Span.Length;
            }
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(changed, position + delta, token).ConfigureAwait(false);
            if (symbol?.Name == name && await IdentityAsync(symbol, after, cache, token).ConfigureAwait(false) is { } identity) result.Add(identity);
        }
        return result;
    }

    private static async Task CheckRenameCompilerErrorsAsync(Solution before, Solution after, CancellationToken token)
    {
        var affected = after.GetChanges(before).GetProjectChanges().Select(change => change.NewProject.Id).ToHashSet();
        var graph = after.GetProjectDependencyGraph();
        foreach (var project in affected.ToArray()) affected.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(project));
        foreach (var projectId in affected)
        {
            var original = await before.GetProject(projectId)!.GetCompilationAsync(token).ConfigureAwait(false);
            var changed = await after.GetProject(projectId)!.GetCompilationAsync(token).ConfigureAwait(false);
            if (original is null || changed is null) throw new InvalidOperationException("Compiler validation is unavailable for rename.");
            static string ErrorKey(Diagnostic diagnostic) => diagnostic.Id + "|" + diagnostic.GetMessage() + "|" + diagnostic.Location.SourceTree?.FilePath;
            var oldErrors = original.GetDiagnostics(token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).GroupBy(ErrorKey).ToDictionary(group => group.Key, group => group.Count());
            foreach (var group in changed.GetDiagnostics(token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).GroupBy(ErrorKey))
                if (group.Count() > oldErrors.GetValueOrDefault(group.Key)) throw new InvalidOperationException("Rename introduces a compiler conflict: " + group.First().GetMessage());
        }
    }

    private static void RequireWritableSymbolFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new InvalidOperationException($"The rename target is missing or read-only: {path}");
    }

    private void EnsureSymbolSnapshotCurrent(SymbolSnapshot snapshot)
    {
        lock (_gate)
            if (_pageProjectionPending || !ReferenceEquals(snapshot.Solution, _solution) || snapshot.Revision != _semanticRevision)
                throw new InvalidOperationException("Project types or buffers changed during the operation. Request it again.");
    }

    private static string ApplySymbolEdits(string text, IEnumerable<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        return text;
    }

    private static string[] BoundedWarnings(IEnumerable<string> warnings) => warnings.Distinct().Take(64)
        .Select(warning => warning.Length > 2048 ? warning[..2048] : warning).ToArray();
}
