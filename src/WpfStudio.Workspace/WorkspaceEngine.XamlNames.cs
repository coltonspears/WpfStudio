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
    private sealed record NameSelection(SymbolFile? File, XamlNameDeclaration? Declaration,
        IFieldSymbol? Field, string? Error = null);

    private async Task<NameSelection?> ResolveRequestedNameAsync(SymbolSnapshot snapshot, SymbolReferenceRequest request,
        CancellationToken token)
    {
        string path = Path.GetFullPath(request.Path);
        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            string? projectPath = request.ProjectPath is null ? null : Path.GetFullPath(request.ProjectPath);
            var contexts = snapshot.Files.Where(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                (projectPath is null || string.Equals(projectPath, file.Project.FilePath, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (contexts.Length != 1)
                throw new InvalidOperationException("Select an unambiguous, available XAML project context before finding or renaming this symbol.");
            var file = contexts[0];
            ValidatePosition(request.Position, file.Text.Length);
            if (file.Names.GetTarget(request.Position) is not { } occurrence) return null;
            return await ResolveNameDeclarationAsync(file, occurrence.Declaration, token).ConfigureAwait(false);
        }

        // A generated field is a XAML name even when its checksum proof fails.
        // Never silently fall through to a C#-only rename of that field.
        if (await ResolveRequestedSymbolAsync(snapshot, request, token).ConfigureAwait(false) is not IFieldSymbol field) return null;
        field = (await SymbolFinder.FindSourceDefinitionAsync(field, snapshot.Solution, token).ConfigureAwait(false) as IFieldSymbol) ?? field;
        var project = field.Locations.Where(location => location.SourceTree is not null)
            .Select(location => snapshot.Solution.GetDocument(location.SourceTree!)?.Project).FirstOrDefault(item => item is not null);
        if (project is null)
            return field.Locations.Any(location => location.SourceTree is { } tree && IsGenerated(tree.FilePath))
                ? new(null, null, field, "The generated name field has no verified current project document. Rebuild and reload the workspace.") : null;
        var compilation = await project.GetCompilationAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Project types are unavailable for name references.");
        var sources = snapshot.Files.Where(file => file.Project.Id == project.Id)
            .Select(file => new XamlGeneratedNameSource(file.Path, file.Text)).ToArray();
        XamlGeneratedNameBridgeResult? bridge = null;
        foreach (var source in sources)
        {
            bridge = await TryResolveProjectedNameAsync(project, compilation, source, null, field, token).ConfigureAwait(false);
            if (bridge is not null) break;
        }
        bridge ??= await XamlGeneratedNameBridge.FromFieldAsync(project, compilation, field, sources, token).ConfigureAwait(false);
        if (bridge.State == XamlGeneratedNameBridgeState.NotGeneratedField) return null;
        if (bridge.State != XamlGeneratedNameBridgeState.Verified || bridge.Path is null)
            return new(null, null, field, bridge.Status ?? "The generated field cannot be verified against current XAML. Rebuild and reload the workspace.");
        var matches = snapshot.Files.Where(file => file.Project.Id == project.Id &&
            string.Equals(file.Path, bridge.Path, StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => file.Names.Declarations.Where(declaration => declaration.Start == bridge.Start && declaration.Length == bridge.Length)
                .Select(declaration => (File: file, Declaration: declaration))).ToArray();
        return matches.Length == 1 ? new(matches[0].File, matches[0].Declaration, bridge.Field)
            : new(null, null, field, "The generated field's authored name is unavailable or ambiguous in the current XAML snapshot.");
    }

    private async Task<NameSelection> ResolveNameDeclarationAsync(SymbolFile file, XamlNameDeclaration declaration,
        CancellationToken token)
    {
        var compilation = await file.Project.GetCompilationAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Project types are unavailable for name references.");
        var source = new XamlGeneratedNameSource(file.Path, file.Text);
        var projected = declaration.IsRootScope
            ? await TryResolveProjectedNameAsync(file.Project, compilation, source, declaration.Start, null, token).ConfigureAwait(false) : null;
        var bridge = projected ?? await XamlGeneratedNameBridge.FromDeclarationAsync(file.Project, compilation, source, declaration.Start, token).ConfigureAwait(false);
        return bridge.State switch
        {
            XamlGeneratedNameBridgeState.Verified => new(file, declaration, bridge.Field),
            XamlGeneratedNameBridgeState.NoGeneratedField => new(file, declaration, null),
            _ => new(file, declaration, null, bridge.Status ?? "Generated name metadata is unavailable. Rebuild and reload the workspace.")
        };
    }

    private async Task<SymbolReferenceResult> FindNameReferencesAsync(SymbolSnapshot snapshot, NameSelection selection,
        CancellationToken token)
    {
        var locations = new List<SourceLocation>();
        if (selection.File is { } file && selection.Declaration is { } declaration)
        {
            var text = SourceText.From(file.Text);
            string hash = TextHash(file.Text);
            foreach (var occurrence in file.Names.Occurrences.Where(occurrence => SameNameDeclaration(occurrence.Declaration, declaration)))
            {
                token.ThrowIfCancellationRequested();
                if (locations.Count >= SymbolScanOccurrences) break;
                var line = text.Lines.GetLinePosition(occurrence.Start);
                locations.Add(new(file.Path, occurrence.Start, occurrence.Length, line.Line + 1, line.Character + 1,
                    declaration.Name, hash, file.Project.FilePath, file.Project.Name));
            }
            snapshot.Warnings.InsertRange(0, file.Names.Warnings);
        }
        if (selection.Error is { } error) snapshot.Warnings.Insert(0, error);
        else if (selection.Field is { } field)
        {
            var contents = new Dictionary<DocumentId, (SourceText Text, string Hash)>();
            var references = await SymbolFinder.FindReferencesAsync(field, snapshot.Solution, token).ConfigureAwait(false);
            foreach (var reference in references.SelectMany(reference => reference.Locations))
            {
                token.ThrowIfCancellationRequested();
                if (!reference.Location.IsInSource || reference.Document.FilePath is not { } path || IsGenerated(path)) continue;
                if (locations.Count >= SymbolScanOccurrences) { snapshot.Warnings.Add("Name references reached the 16,384-location limit."); break; }
                if (!contents.TryGetValue(reference.Document.Id, out var content))
                {
                    var text = await reference.Document.GetTextAsync(token).ConfigureAwait(false);
                    contents[reference.Document.Id] = content = (text, TextHash(text.ToString()));
                }
                var span = reference.Location.SourceSpan;
                var line = content.Text.Lines.GetLinePosition(span.Start);
                locations.Add(new(path, span.Start, span.Length, line.Line + 1, line.Character + 1, field.Name,
                    content.Hash, reference.Document.Project.FilePath, reference.Document.Project.Name));
            }
        }
        snapshot.Warnings.Insert(0, "Name references include verified authored declarations, ElementName values and proven generated-field C# uses. Runtime registration, FindName/reflection strings and unsupported name consumers are not inferred.");
        EnsureSymbolSnapshotCurrent(snapshot);
        return new(locations.Distinct().ToArray(), BoundedWarnings(snapshot.Warnings));
    }

    private async Task<WorkspaceEditResult> RenameNameAsync(SymbolSnapshot snapshot, NameSelection selection, string newName,
        CancellationToken token)
    {
        if (selection.Error is { } error) throw new InvalidOperationException(error);
        if (selection.File is not { } selectedFile || selection.Declaration is not { } selected)
            throw new InvalidOperationException("The authored XAML name is unavailable.");
        if (!XamlNameScopeIndex.ValidName(newName)) throw new ArgumentException("The new name must be a valid XAML name.", nameof(newName));
        if (!snapshot.Complete)
            throw new InvalidOperationException("Name rename requires every evaluated XAML file and model context to be available within the scan budget. " + string.Join(" ", BoundedWarnings(snapshot.Warnings)));
        var contexts = snapshot.Files.Where(file => file.Path.Equals(selectedFile.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        var targets = new List<NameSelection>();
        List<TextEdit>? replacements = null;
        int nameOccurrences = 0;
        foreach (var context in contexts)
        {
            token.ThrowIfCancellationRequested();
            nameOccurrences += context.Names.Occurrences.Count;
            if (nameOccurrences > SymbolScanOccurrences)
                throw new InvalidOperationException("Linked name validation exceeds the 16,384-occurrence limit.");
            if (!context.Names.IsComplete || context.Names.CoverageLimited)
                throw new InvalidOperationException("Name rename requires complete, supported name consumers in every linked XAML context. " + string.Join(" ", BoundedWarnings(context.Names.Warnings)));
            var declarations = context.Names.Declarations.Where(declaration => SameNameDeclaration(declaration, selected)).ToArray();
            if (declarations.Length != 1 || !EquivalentNameDeclaration(declarations[0], selected))
                throw new InvalidOperationException("Linked XAML contexts disagree about this name declaration; rename was withheld.");
            var target = context == selectedFile ? selection
                : await ResolveNameDeclarationAsync(context, declarations[0], token).ConfigureAwait(false);
            if (target.Error is not null) throw new InvalidOperationException(target.Error);
            if ((target.Field is null) != (selection.Field is null))
                throw new InvalidOperationException("Linked XAML contexts disagree about the generated name field; rename was withheld.");
            var edits = context.Names.Occurrences.Where(occurrence => SameNameDeclaration(occurrence.Declaration, declarations[0]))
                .Select(occurrence => new TextEdit(occurrence.Start, occurrence.Length, newName)).Distinct().OrderBy(edit => edit.Start).ToList();
            if (edits.Count == 0 || replacements is not null && !replacements.SequenceEqual(edits))
                throw new InvalidOperationException("Linked XAML contexts disagree about name references; rename was withheld.");
            replacements ??= edits;
            targets.Add(target);
        }
        if (replacements is null) throw new InvalidOperationException("No verified name declaration was captured.");
        RequireWritableSymbolFile(selectedFile.Path);
        var changed = snapshot.Solution;
        if (targets.Any(target => target.Field is not null))
        {
            if (!SyntaxFacts.IsValidIdentifier(newName) || newName.StartsWith('@'))
                throw new ArgumentException("A generated name field requires an unescaped C# identifier as well as a valid XAML name.", nameof(newName));
            foreach (var target in targets)
            {
                if (target.Field is not { } field || target.File is not { } file) continue;
                var compilation = await changed.GetProject(file.Project.Id)!.GetCompilationAsync(token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Project types are unavailable for generated-field rename.");
                string id = DocumentationCommentId.CreateDeclarationId(field)
                    ?? throw new InvalidOperationException("The generated field has no stable declaration identity.");
                if (DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) is not IFieldSymbol current)
                    throw new InvalidOperationException("The generated field changed during linked-context rename.");
                // Keep generated edits in this temporary solution so Roslyn checks
                // declarations and consumers together. They are never emitted.
                changed = await Renamer.RenameSymbolAsync(changed, current, new SymbolRenameOptions(), newName, token).ConfigureAwait(false);
            }
        }
        await CheckRenameCompilerErrorsAsync(snapshot.Solution, changed, token).ConfigureAwait(false);
        var documents = await AuthoredNameCodeEditsAsync(snapshot, changed, token).ConfigureAwait(false);
        string after = ApplySymbolEdits(selectedFile.Text, replacements);
        var changedTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [selectedFile.Path] = after };
        var oldIdentities = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
        var newIdentities = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
        int[] ends = replacements.Select(edit => edit.Start + edit.Length).ToArray();
        int[] deltas = new int[replacements.Count];
        for (int i = 0; i < replacements.Count; i++)
            deltas[i] = (i == 0 ? 0 : deltas[i - 1]) + replacements[i].NewText.Length - replacements[i].Length;
        int Map(int position)
        {
            int index = Array.BinarySearch(ends, position);
            if (index < 0) index = ~index - 1;
            return position + (index < 0 ? 0 : deltas[index]);
        }
        foreach (var target in targets)
        {
            var file = target.File!;
            var compilation = await changed.GetProject(file.Project.Id)!.GetCompilationAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Renamed project types are unavailable.");
            var rebound = _xamlNames.GetNameOccurrences(after, compilation, token);
            var validation = _xamlNames.ValidateRename(file.Names, rebound, target.Declaration!, newName, replacements, token);
            if (!validation.Success) throw new InvalidOperationException(validation.Error ?? "Renaming would change an unrelated name reference.");
            // ElementName also controls property-path source inference. Preserve
            // all already-resolved binding/event symbols, including untouched ones.
            var symbols = ReadSymbolOccurrences(after, compilation, token, snapshot.Resources.Context(file.Project, file.Path, changedTexts));
            if (symbols.CoverageLimited) throw new InvalidOperationException("Renamed XAML exceeded the symbol validation budget.");
            var map = symbols.Occurrences.ToLookup(occurrence => (occurrence.Start, occurrence.Length, occurrence.Kind));
            foreach (var occurrence in file.Occurrences)
            {
                int position = Map(occurrence.Start);
                var matches = map[(position, occurrence.Length, occurrence.Kind)].ToArray();
                if (matches.Length != 1 || await IdentityAsync(matches[0].Symbol, changed, newIdentities, token).ConfigureAwait(false)
                    != await IdentityAsync(occurrence.Symbol, snapshot.Solution, oldIdentities, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Renaming would change or lose an existing XAML binding or event symbol.");
            }
        }
        foreach (var group in snapshot.Files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            var file = group.First();
            // Include every scanned file as an exact prerequisite. An external
            // edit during review must not introduce an unobserved name consumer.
            documents[file.Path] = new(file.Path, file.Version,
                file.Path.Equals(selectedFile.Path, StringComparison.OrdinalIgnoreCase) ? replacements : [], TextHash(file.Text));
        }
        XamlNameProjectionPlan? projection = null;
        if (selection.Field is not null && selected.Name != newName)
        {
            var expected = new List<XamlNameProjectionExpectedDocument>();
            foreach (var document in documents.Values.Where(document => document.Edits.Count > 0))
            {
                string content;
                if (document.Path.Equals(selectedFile.Path, StringComparison.OrdinalIgnoreCase)) content = selectedFile.Text;
                else
                {
                    var id = snapshot.Solution.GetDocumentIdsWithFilePath(document.Path).FirstOrDefault()
                        ?? throw new InvalidOperationException("An authored rename document is no longer in the current solution.");
                    content = (await snapshot.Solution.GetDocument(id)!.GetTextAsync(token).ConfigureAwait(false)).ToString();
                }
                expected.Add(new(document.Path, TextHash(ApplySymbolEdits(content, document.Edits))));
            }
            projection = new(selectedFile.Path, "", [], [], expected, CurrentSource: true);
        }
        snapshot.Warnings.AddRange(selectedFile.Names.Warnings);
        snapshot.Warnings.Insert(0, "Only verified name declarations, ElementName values and generated-field C# references are renamed. Review runtime RegisterName/FindName, reflection and other string-based consumers.");
        if (projection is not null)
            snapshot.Warnings.Insert(0, "Current XAML fields update in memory when this rename is applied. Generated files are not edited; a real build still compiles the saved XAML.");
        EnsureSymbolSnapshotCurrent(snapshot);
        return new(documents.Values.ToArray(), BoundedWarnings(snapshot.Warnings), projection);
    }

    private static async Task<Dictionary<string, DocumentEdits>> AuthoredNameCodeEditsAsync(SymbolSnapshot snapshot, Solution changed,
        CancellationToken token)
    {
        var result = new Dictionary<string, DocumentEdits>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in changed.GetChanges(snapshot.Solution).GetProjectChanges())
        foreach (var id in project.GetChangedDocuments())
        {
            var before = snapshot.Solution.GetDocument(id)!;
            if (before.FilePath is not { } path || IsGenerated(path)) continue;
            RequireWritableSymbolFile(path);
            var changes = await changed.GetDocument(id)!.GetTextChangesAsync(before, token).ConfigureAwait(false);
            string text = (await before.GetTextAsync(token).ConfigureAwait(false)).ToString();
            var edit = new DocumentEdits(path, snapshot.Versions.GetValueOrDefault(path), changes.Select(ToEdit).ToArray(), TextHash(text));
            if (result.TryGetValue(path, out var previous) &&
                (previous.ExpectedTextHash != edit.ExpectedTextHash || !previous.Edits.SequenceEqual(edit.Edits)))
                throw new InvalidOperationException("Linked C# contexts produce conflicting generated-name edits.");
            result[path] = edit;
        }
        return result;
    }

    private static bool SameNameDeclaration(XamlNameDeclaration left, XamlNameDeclaration right)
        => left.Start == right.Start && left.Length == right.Length && left.ScopeStart == right.ScopeStart;

    private static bool EquivalentNameDeclaration(XamlNameDeclaration left, XamlNameDeclaration right)
        => left.Name == right.Name && left.ScopeKind == right.ScopeKind && left.IsRootScope == right.IsRootScope &&
           left.RootClass == right.RootClass && left.ElementType.ToDisplayString() == right.ElementType.ToDisplayString() &&
           Equals(left.ElementType.ContainingAssembly.Identity, right.ElementType.ContainingAssembly.Identity);

}
