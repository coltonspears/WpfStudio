using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Xaml;

internal sealed record XamlNameProjectionMapping(ProjectId ProjectId, string RootClass, string Name,
    string TypeIdentity, int Start, int Length);
internal sealed record XamlNameProjectionState(string XamlText, IReadOnlyDictionary<DocumentId, SourceText> GeneratedTexts,
    IReadOnlyList<XamlNameProjectionMapping> Mappings);
internal sealed record XamlNameProjectionModel(XamlNameProjectionPlan Plan, IReadOnlyList<XamlNameProjectionState> States);

/// <summary>
/// Re-derives compiler-field projections from authenticated source bytes and
/// actual generated documents. A plan contains rename intent, never executable
/// replacement code; all resulting generated text stays inside Roslyn.
/// </summary>
internal static class XamlNameProjection
{
    internal const int MaximumSteps = 32;
    private const int MaximumDocuments = 32;
    private const int MaximumGeneratedCharacters = 2_000_000;
    private const int MaximumBaselineCharacters = 8_000_000;
    private const int MaximumStateCharacters = 32_000_000;
    private const long MaximumBaselineProofCharacters = 32_000_000;
    private const int MaximumMappings = 4096;
    private static readonly XamlNameService Names = new();

    internal static string TypeIdentity(ITypeSymbol type)
        => type.ContainingAssembly?.Identity + "|" + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    internal static async Task<XamlNameProjectionModel> BuildAsync(Solution current, XamlNameProjectionPlan plan,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidatePlan(plan);
        string path = Path.GetFullPath(plan.XamlPath);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(plan.BaselineSourceBytes); }
        catch (FormatException exception) { throw new InvalidOperationException("The compiler source bytes are malformed.", exception); }
        if (bytes.Length > XamlGeneratedNameBridge.MaximumSourceBytes)
            throw new InvalidOperationException("The compiler source byte budget was exceeded.");
        string baseline = XamlGeneratedNameBridge.DecodeSourceBytes(bytes);
        if (baseline.Length > XamlNameScopeIndex.MaximumCharacters)
            throw new InvalidOperationException("The compiler source text budget was exceeded.");

        var baselineTexts = new Dictionary<DocumentId, SourceText>();
        var projects = new Dictionary<ProjectId, Project>();
        var temporary = current;
        int baselineCharacters = 0;
        foreach (var entry in plan.Baselines)
        {
            token.ThrowIfCancellationRequested();
            string projectPath = Path.GetFullPath(entry.ProjectPath), generatedPath = Path.GetFullPath(entry.Path);
            var owners = current.Projects.Where(project => SamePath(project.FilePath, projectPath)).Take(2).ToArray();
            if (owners.Length != 1)
                throw new InvalidOperationException("The generated baseline project is unavailable or ambiguous; reload the workspace.");
            var owner = owners[0];
            var documents = owner.Documents.Where(document => SamePath(document.FilePath, generatedPath)).Take(2).ToArray();
            if (documents.Length != 1 || !Generated(generatedPath))
                throw new InvalidOperationException("A projection baseline must identify one current generated project document.");
            var document = documents[0];
            if (baselineTexts.ContainsKey(document.Id)) throw new InvalidOperationException("The projection repeats a generated baseline document.");
            // Re-read the compiler output itself. Neither a supplied replacement
            // string nor a previous in-memory projection can stand in for it.
            byte[] generatedBytes = await XamlGeneratedNameBridge.ReadSourceBytesAsync(generatedPath, token).ConfigureAwait(false);
            string generatedText = XamlGeneratedNameBridge.DecodeSourceBytes(generatedBytes);
            baselineCharacters += generatedText.Length;
            if (generatedText.Length > MaximumGeneratedCharacters || baselineCharacters > MaximumBaselineCharacters)
                throw new InvalidOperationException("The generated baseline text budget was exceeded.");
            if (!string.Equals(Hash(generatedText), entry.TextHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The compiler-generated baseline changed. Rebuild or reload before restoring the name projection.");
            var text = SourceText.From(generatedText, Encoding.UTF8);
            baselineTexts.Add(document.Id, text);
            projects[owner.Id] = owner;
            temporary = temporary.WithDocumentText(document.Id, text, PreservationMode.PreserveIdentity);
        }

        var usedBaselines = new HashSet<DocumentId>();
        var mappings = new List<XamlNameProjectionMapping>();
        long proofCharacters = 0;
        void ReserveProof(long characters)
        {
            if (characters > MaximumBaselineProofCharacters - proofCharacters)
                throw new InvalidOperationException("Compiler-field baseline verification exceeds its 32-million-character work budget. Live name projection is unavailable for this page.");
            proofCharacters += characters;
        }
        foreach (var projectId in projects.Keys)
        {
            var project = temporary.GetProject(projectId)!;
            var compilation = await CompilationAsync(project, token).ConfigureAwait(false);
            ReserveProof(baseline.Length); // Initial namescope capture in this project.
            var names = CompleteNames(baseline, compilation, token);
            foreach (var declaration in names.Declarations.Where(declaration => declaration.IsRootScope && declaration.RootClass is not null))
            {
                // Each bridge proof parses XML and the lexical name tree again,
                // then walks the generated document's directives. Bound that
                // repeated work independently of retained text and field counts.
                if (declaration.RootType?.GetMembers(declaration.Name) is not [IFieldSymbol candidate]
                    || FieldDocument(project, candidate) is not { } candidateDocument
                    || !baselineTexts.TryGetValue(candidateDocument.Id, out var candidateText))
                    throw new InvalidOperationException("A page field's generated document is missing from the baseline set.");
                ReserveProof(2L * baseline.Length + candidateText.Length);
                var proof = await XamlGeneratedNameBridge.FromDeclarationAsync(project, compilation,
                    new(path, baseline), declaration.Start, token, bytes).ConfigureAwait(false);
                if (proof.State != XamlGeneratedNameBridgeState.Verified || proof.Field is not { } field)
                    throw new InvalidOperationException(proof.Status ?? "A page field lacks compiler provenance.");
                var document = FieldDocument(project, field);
                if (document is null || !baselineTexts.ContainsKey(document.Id))
                    throw new InvalidOperationException("A page field's generated document is missing from the baseline set.");
                usedBaselines.Add(document.Id);
                mappings.Add(new(projectId, declaration.RootClass!, declaration.Name, TypeIdentity(field.Type), declaration.Start, declaration.Length));
                if (mappings.Count > MaximumMappings) throw new InvalidOperationException("The generated-name mapping budget was exceeded.");
            }
        }
        if (mappings.Count == 0 || usedBaselines.Count != baselineTexts.Count)
            throw new InvalidOperationException("The baseline includes an unrelated generated document or has no verified page fields.");

        var states = new List<XamlNameProjectionState>
        {
            State(baseline, baselineTexts, mappings)
        };
        long stateCharacters = baseline.Length + baselineCharacters;
        string xaml = baseline;
        foreach (var step in plan.Steps)
        {
            token.ThrowIfCancellationRequested();
            if (!XamlNameScopeIndex.ValidName(step.NewName) || !SyntaxFacts.IsValidIdentifier(step.NewName) || step.NewName.StartsWith('@'))
                throw new InvalidOperationException("A projected page name must be a valid unescaped C# and XAML identifier.");
            var beforeNames = new Dictionary<ProjectId, XamlNameOccurrenceResult>();
            var selected = new Dictionary<ProjectId, XamlNameDeclaration>();
            TextEdit[]? edits = null;
            foreach (var projectId in projects.Keys)
            {
                var compilation = await CompilationAsync(temporary.GetProject(projectId)!, token).ConfigureAwait(false);
                var names = CompleteNames(xaml, compilation, token);
                var candidates = names.Declarations.Where(declaration => declaration.Start == step.DeclarationStart && declaration.IsRootScope).Take(2).ToArray();
                if (candidates.Length != 1 || candidates[0].RootClass is null)
                    throw new InvalidOperationException("A projection step does not identify one verified page-name declaration.");
                var declaration = candidates[0];
                if (!states[^1].Mappings.Any(mapping => mapping.ProjectId == projectId && mapping.Start == declaration.Start &&
                    mapping.Length == declaration.Length && mapping.Name == declaration.Name && mapping.RootClass == declaration.RootClass))
                    throw new InvalidOperationException("A projection step lost its previously verified field mapping.");
                var changes = names.Occurrences.Where(occurrence => occurrence.Declaration == declaration)
                    .Select(occurrence => new TextEdit(occurrence.Start, occurrence.Length, step.NewName)).Distinct().OrderBy(edit => edit.Start).ToArray();
                if (edits is not null && !edits.SequenceEqual(changes))
                    throw new InvalidOperationException("Linked contexts disagree about a projected name rename.");
                edits ??= changes;
                beforeNames.Add(projectId, names); selected.Add(projectId, declaration);
            }
            if (edits is not { Length: > 0 }) throw new InvalidOperationException("A projection step has no verified authored edits.");
            string afterXaml = Apply(xaml, edits);
            foreach (var projectId in projects.Keys)
            {
                var project = temporary.GetProject(projectId)!;
                var compilation = await CompilationAsync(project, token).ConfigureAwait(false);
                var declaration = selected[projectId];
                var root = compilation.GetTypeByMetadataName(declaration.RootClass!)
                    ?? throw new InvalidOperationException("The projected page class is unavailable.");
                if (root.GetMembers(declaration.Name) is not [IFieldSymbol field] || FieldDocument(project, field) is not { } fieldDocument
                    || !baselineTexts.ContainsKey(fieldDocument.Id))
                    throw new InvalidOperationException("The projected name no longer identifies one compiler field.");
                if (step.NewName != declaration.Name && root.GetMembers(step.NewName).Length != 0)
                    throw new InvalidOperationException("The projected name conflicts with an existing page member.");
                var renamed = await Renamer.RenameSymbolAsync(temporary, field, new SymbolRenameOptions(), step.NewName, token).ConfigureAwait(false);
                foreach (var projectChange in renamed.GetChanges(temporary).GetProjectChanges())
                foreach (var id in projectChange.GetChangedDocuments())
                {
                    var document = temporary.GetDocument(id)!;
                    if (document.FilePath is not { } generatedPath || !Generated(generatedPath)) continue;
                    if (!baselineTexts.ContainsKey(id))
                        throw new InvalidOperationException("Name replay changed a generated document outside its verified baseline set.");
                    var text = await renamed.GetDocument(id)!.GetTextAsync(token).ConfigureAwait(false);
                    if (text.Length > MaximumGeneratedCharacters) throw new InvalidOperationException("The projected generated text budget was exceeded.");
                    temporary = temporary.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                }
                // Authored C# edits from this temporary rename are deliberately
                // discarded. The editor's current buffers remain authoritative.
            }

            mappings = [];
            foreach (var projectId in projects.Keys)
            {
                var project = temporary.GetProject(projectId)!;
                var compilation = await CompilationAsync(project, token).ConfigureAwait(false);
                var names = CompleteNames(afterXaml, compilation, token);
                var validation = Names.ValidateRename(beforeNames[projectId], names, selected[projectId], step.NewName, edits, token);
                if (!validation.Success) throw new InvalidOperationException(validation.Error ?? "Projected name references could not be revalidated.");
                foreach (var declaration in names.Declarations.Where(declaration => declaration.IsRootScope && declaration.RootClass is not null))
                {
                    if (declaration.RootType?.GetMembers(declaration.Name) is not [IFieldSymbol field]
                        || !SymbolEqualityComparer.Default.Equals(field.Type, declaration.ElementType)
                        || FieldDocument(project, field) is not { } document || !baselineTexts.ContainsKey(document.Id)
                        || field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
                        throw new InvalidOperationException("A projected declaration lost its exact compiler-field identity.");
                    mappings.Add(new(projectId, declaration.RootClass!, declaration.Name, TypeIdentity(field.Type), declaration.Start, declaration.Length));
                }
            }
            var generated = new Dictionary<DocumentId, SourceText>();
            foreach (var id in baselineTexts.Keys)
                generated[id] = await temporary.GetDocument(id)!.GetTextAsync(token).ConfigureAwait(false);
            stateCharacters += afterXaml.Length + generated.Values.Sum(text => (long)text.Length);
            if (stateCharacters > MaximumStateCharacters) throw new InvalidOperationException("The retained name-projection state budget was exceeded.");
            states.Add(State(afterXaml, generated, mappings));
            xaml = afterXaml;
        }
        var expectedXaml = plan.ExpectedDocuments.SingleOrDefault(document => SamePath(document.Path, path));
        if (expectedXaml is null || expectedXaml.TextHash != Hash(xaml))
            throw new InvalidOperationException("The projection does not produce the expected authored XAML state.");
        // Copy request collections before retaining them across later requests.
        var saved = plan with
        {
            XamlPath = path,
            Baselines = Array.AsReadOnly(plan.Baselines.ToArray()),
            Steps = Array.AsReadOnly(plan.Steps.ToArray()),
            ExpectedDocuments = Array.AsReadOnly(plan.ExpectedDocuments.ToArray())
        };
        return new(saved, states.AsReadOnly());
    }

    internal static async Task<XamlNameProjectionModel> CreateAsync(Solution before, Solution after,
        XamlNameProjectionModel? existing, string xamlPath, string beforeText, int declarationStart,
        string newName, string afterText, IReadOnlyList<XamlNameProjectionExpectedDocument> expectedDocuments,
        CancellationToken token = default)
    {
        xamlPath = Path.GetFullPath(xamlPath);
        XamlNameProjectionPlan plan;
        if (existing is not null)
        {
            if (!SamePath(existing.Plan.XamlPath, xamlPath)) throw new InvalidOperationException("The projection belongs to a different source file.");
            int state = -1;
            for (int i = existing.States.Count - 1; i >= 0; i--)
                if (existing.States[i].XamlText == beforeText && await MatchesGeneratedAsync(before, existing.States[i], token).ConfigureAwait(false))
                { state = i; break; }
            if (state < 0) throw new InvalidOperationException("Current source and compiler fields do not match a recognized projection state.");
            if (state >= MaximumSteps) throw new InvalidOperationException("The name-projection history reached its 32-step limit. Save, rebuild and reload the workspace.");
            plan = existing.Plan with
            {
                Steps = existing.Plan.Steps.Take(state).Append(new XamlNameProjectionStep(declarationStart, newName)).ToArray(),
                ExpectedDocuments = expectedDocuments.ToArray()
            };
        }
        else
        {
            byte[] sourceBytes = await XamlGeneratedNameBridge.ReadSourceBytesAsync(xamlPath, token).ConfigureAwait(false);
            if (XamlGeneratedNameBridge.DecodeSourceBytes(sourceBytes) != beforeText)
                throw new InvalidOperationException("The authored XAML changed before its compiler projection could be captured.");
            var baselines = new List<XamlNameProjectionBaseline>();
            foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
            foreach (var id in projectChange.GetChangedDocuments())
            {
                var document = before.GetDocument(id)!;
                if (document.FilePath is not { } path || !Generated(path)) continue;
                if (document.Project.FilePath is not { } projectPath)
                    throw new InvalidOperationException("The generated project has no stable path for replay.");
                var text = await document.GetTextAsync(token).ConfigureAwait(false);
                baselines.Add(new(projectPath, path, Hash(text.ToString())));
            }
            plan = new(xamlPath, Convert.ToBase64String(sourceBytes), baselines,
                [new(declarationStart, newName)], expectedDocuments.ToArray());
        }
        var model = await BuildAsync(before, plan, token).ConfigureAwait(false);
        if (model.States[^1].XamlText != afterText || !await MatchesGeneratedAsync(after, model.States[^1], token).ConfigureAwait(false))
            throw new InvalidOperationException("Replayed compiler fields differ from the reviewed rename result.");
        var allowed = model.States[^1].GeneratedTexts.Keys.ToHashSet();
        foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
        foreach (var id in projectChange.GetChangedDocuments())
            if (after.GetDocument(id)?.FilePath is { } path && Generated(path) && !allowed.Contains(id))
                throw new InvalidOperationException("The reviewed rename changes a generated document outside its projection.");
        return model;
    }

    private static XamlNameProjectionState State(string text, Dictionary<DocumentId, SourceText> generated,
        List<XamlNameProjectionMapping> mappings) => new(text,
            new ReadOnlyDictionary<DocumentId, SourceText>(new Dictionary<DocumentId, SourceText>(generated)), Array.AsReadOnly(mappings.ToArray()));

    private static XamlNameOccurrenceResult CompleteNames(string text, Compilation compilation, CancellationToken token)
    {
        var result = Names.GetNameOccurrences(text, compilation, token);
        if (!result.IsComplete || result.CoverageLimited)
            throw new InvalidOperationException("Name projection requires complete supported authored name coverage. " + string.Join(" ", result.Warnings.Take(8)));
        return result;
    }

    private static Document? FieldDocument(Project project, IFieldSymbol field)
    {
        if (field.DeclaringSyntaxReferences is not [var reference]) return null;
        var document = project.Solution.GetDocument(reference.SyntaxTree);
        return document?.Project.Id == project.Id ? document : null;
    }

    private static async Task<bool> MatchesGeneratedAsync(Solution solution, XamlNameProjectionState state, CancellationToken token)
    {
        foreach (var pair in state.GeneratedTexts)
        {
            if (solution.GetDocument(pair.Key) is not { } document) return false;
            if (!(await document.GetTextAsync(token).ConfigureAwait(false)).ContentEquals(pair.Value)) return false;
        }
        return true;
    }

    private static async Task<Compilation> CompilationAsync(Project project, CancellationToken token)
        => await project.GetCompilationAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Compiler types are unavailable for the generated-name projection.");

    private static void ValidatePlan(XamlNameProjectionPlan plan)
    {
        if (plan.BaselineSourceBytes is null || plan.BaselineSourceBytes.Length > ((XamlGeneratedNameBridge.MaximumSourceBytes + 2) / 3) * 4
            || plan.Baselines is not { Count: >= 1 and <= MaximumDocuments } || plan.Steps is not { Count: >= 1 and <= MaximumSteps }
            || plan.ExpectedDocuments is not { Count: >= 1 and <= 512 })
            throw new InvalidOperationException("The name-projection request exceeds its bounded source, document, or history limits.");
        if (!Path.IsPathFullyQualified(plan.XamlPath) || !plan.XamlPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A projection requires an absolute authored XAML path.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in plan.ExpectedDocuments)
            if (document is null || string.IsNullOrEmpty(document.Path) || !Path.IsPathFullyQualified(document.Path) || !ValidHash(document.TextHash) || !expected.Add(Path.GetFullPath(document.Path)))
                throw new InvalidOperationException("The projection's authored hash prerequisites are invalid or repeated.");
        foreach (var document in plan.Baselines)
            if (document is null || string.IsNullOrEmpty(document.Path) || string.IsNullOrEmpty(document.ProjectPath)
                || !Path.IsPathFullyQualified(document.Path) || !Path.IsPathFullyQualified(document.ProjectPath) || !ValidHash(document.TextHash))
                throw new InvalidOperationException("The generated baseline identity is invalid.");
        foreach (var step in plan.Steps)
            if (step is null || step.DeclarationStart < 0 || string.IsNullOrEmpty(step.NewName))
                throw new InvalidOperationException("The name-projection rename step is invalid.");
    }

    private static bool ValidHash(string? text) => text is { Length: 64 } && text.All(character => char.IsAsciiHexDigit(character));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool SamePath(string? left, string? right) => left is not null && right is not null &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static bool Generated(string path) => path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) || path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    private static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        return text;
    }
}
