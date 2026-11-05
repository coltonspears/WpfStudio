using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private const int CodeLensDeclarations = 300;
    private static readonly TimeSpan CodeLensBudget = TimeSpan.FromSeconds(12);
    // Counts are asked for after every pause in typing, and the watcher and its periodic check already report what changes on disk.
    private static readonly TimeSpan DiskVerificationHolds = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Reference counts for a file's declarations. Finding references searches the whole solution, and this is asked for after
    /// every pause in typing, so a declaration is only counted again when an edit has touched its name or something bigger changed.
    /// </summary>
    public async Task<CodeLensResult> GetCodeLensAsync(DocumentRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var path = Path.GetFullPath(request.Path);
        var document = GetDocument(request.Path, request.Version);
        var declarations = await DeclarationsAsync(document, cancellationToken).ConfigureAwait(false);
        if (declarations is null) return new(request.Version, [], false, "The document could not be analyzed.");

        CodeLensCounts.File known;
        Dictionary<string, CountedReferences> counts;
        HashSet<string> changedNames;
        long edits;
        lock (_gate)
        {
            known = _codeLensCounts.For(path);
            counts = new(known.Counts, StringComparer.Ordinal);
            changedNames = new(known.ChangedNames, StringComparer.Ordinal);
            edits = known.Edits;
            if (declarations.Take(CodeLensDeclarations).All(found => IsCounted(found.Symbol, counts, changedNames)) && counts.Count > 0)
                return new(request.Version, Entries(declarations, counts, document), known.Complete, known.Status);
        }

        // One XAML scan serves every declaration in the file.
        var snapshot = await CaptureSymbolSnapshotAsync(new SymbolReferenceRequest(request.Path, 0, request.Version), acceptRecentVerification: true, cancellationToken).ConfigureAwait(false);
        var solution = snapshot.Solution;
        document = solution.GetDocumentIdsWithFilePath(path).Select(solution.GetDocument).FirstOrDefault()
            ?? throw new FileNotFoundException("This file is not part of the loaded C# workspace.", request.Path);
        declarations = await DeclarationsAsync(document, cancellationToken).ConfigureAwait(false);
        if (declarations is null) return new(request.Version, [], false, "The document could not be analyzed.");
        var identities = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
        var xaml = new Dictionary<string, HashSet<(string Path, int Start)>>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        foreach (var occurrence in file.Occurrences)
            if (await IdentityAsync(occurrence.Symbol, solution, identities, cancellationToken).ConfigureAwait(false) is { } key)
                (xaml.TryGetValue(key, out var uses) ? uses : xaml[key] = []).Add((file.Path.ToUpperInvariant(), occurrence.Start)); // Linked contexts count once.

        var complete = declarations.Length <= CodeLensDeclarations && snapshot.Complete;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CodeLensBudget);
        int counted = 0;
        foreach (var (symbol, _) in declarations.Take(CodeLensDeclarations))
        {
            if (IsCounted(symbol, counts, changedNames)) continue;
            if (budget.IsCancellationRequested) { complete = false; break; }
            try
            {
                int references = 0, xamlReferences = 0;
                string? xamlName = null;
                foreach (var related in Related(symbol))
                {
                    var found = (await SymbolFinder.FindReferencesAsync(related, solution, budget.Token).ConfigureAwait(false)).ToArray();
                    references += found.SelectMany(reference => reference.Locations)
                        .Count(location => location.Location.IsInSource && location.Document.FilePath is { } locationPath && !IsGenerated(locationPath) && !location.IsImplicit);
                    var family = await SymbolFamilyAsync(related, found, solution, budget.Token).ConfigureAwait(false);
                    var uses = family.Keys.SelectMany(key => xaml.GetValueOrDefault(key) ?? []).Distinct().Count();
                    xamlReferences += uses;
                    if (uses > 0 && !SymbolEqualityComparer.Default.Equals(related, symbol)) xamlName = related.Name;
                }
                counts[CountKey(symbol)] = new(references, xamlReferences, xamlName);
                counted++;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { complete = false; break; }
        }
        EnsureSymbolSnapshotCurrent(snapshot);
        var status = complete ? null : "Some counts are missing: " + string.Join(" ", BoundedWarnings(snapshot.Warnings).DefaultIfEmpty("the analysis budget was reached."));
        lock (_gate)
        {
            if (_codeLensCounts.IsCurrent(path, known))
            {
                foreach (var (key, count) in counts) known.Counts[key] = count;
                // Names touched while this counted may have been missed, so they stay for next time.
                if (known.Edits == edits && complete) known.ChangedNames.Clear();
                known.Complete = complete;
                known.Status = status;
            }
        }
        return new(request.Version, Entries(declarations, counts, document), complete, status, counted);
    }

    private static async Task<(ISymbol Symbol, SyntaxToken Identifier)[]?> DeclarationsAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return null;
        return root.DescendantNodes(node => node is not (BlockSyntax or ArrowExpressionClauseSyntax or AccessorListSyntax))
            .Select(node => Declared(model, node, cancellationToken)).Where(found => found is not null).Select(found => found!.Value)
            .Take(CodeLensDeclarations + 1).ToArray();
    }

    private static IReadOnlyList<CodeLensEntry> Entries((ISymbol Symbol, SyntaxToken Identifier)[] declarations, Dictionary<string, CountedReferences> counts, Document document)
    {
        if (!document.TryGetText(out var text)) return [];
        var entries = new List<CodeLensEntry>();
        foreach (var (symbol, identifier) in declarations.Take(CodeLensDeclarations))
        {
            if (!counts.TryGetValue(CountKey(symbol), out var count)) continue;
            // The editor shows the lens above the declaration's first line and Git history for all of its lines.
            var declaration = identifier.Parent?.AncestorsAndSelf().FirstOrDefault(node => node is MemberDeclarationSyntax) ?? identifier.Parent;
            var span = declaration?.Span ?? identifier.Span;
            entries.Add(new(text.Lines.GetLinePosition(identifier.SpanStart).Line + 1, identifier.SpanStart, symbol.Name, count.References, count.XamlReferences, count.XamlName,
                text.Lines.GetLinePosition(span.Start).Line + 1, text.Lines.GetLinePosition(span.End).Line + 1));
        }
        return entries;
    }

    private static string CountKey(ISymbol symbol) => symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString();

    private static bool IsCounted(ISymbol symbol, Dictionary<string, CountedReferences> counts, HashSet<string> changedNames) =>
        counts.ContainsKey(CountKey(symbol)) && !Related(symbol).Any(related => Touched(related, changedNames));

    // A use names the symbol itself, except an attribute used without its suffix and a constructor, which is named after its type.
    private static bool Touched(ISymbol symbol, HashSet<string> changedNames) =>
        changedNames.Contains(symbol.Name)
        || symbol is INamedTypeSymbol && symbol.Name.EndsWith("Attribute", StringComparison.Ordinal) && changedNames.Contains(symbol.Name[..^"Attribute".Length])
        || symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } type } && changedNames.Contains(type.Name);

    // Types and members get lenses. Fields only when the Toolkit turns them into properties XAML binds to.
    private static (ISymbol Symbol, SyntaxToken Identifier)? Declared(SemanticModel model, SyntaxNode node, CancellationToken token)
    {
        (SyntaxNode Declaration, SyntaxToken Identifier)? target = node switch
        {
            BaseTypeDeclarationSyntax type => (type, type.Identifier),
            DelegateDeclarationSyntax @delegate => (@delegate, @delegate.Identifier),
            MethodDeclarationSyntax method => (method, method.Identifier),
            ConstructorDeclarationSyntax constructor => (constructor, constructor.Identifier),
            PropertyDeclarationSyntax property => (property, property.Identifier),
            EventDeclarationSyntax @event => (@event, @event.Identifier),
            FieldDeclarationSyntax { Declaration.Variables: [var variable] } field when HasAttribute(field.AttributeLists, "ObservableProperty") => (variable, variable.Identifier),
            _ => null
        };
        return target is { } found && model.GetDeclaredSymbol(found.Declaration, token) is { } symbol ? (symbol, found.Identifier) : null;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) => lists.SelectMany(list => list.Attributes)
        .Any(attribute => attribute.Name.ToString().Split('.')[^1] is var simple && (simple == name || simple == name + "Attribute"));

    /// <summary>The symbol and the members CommunityToolkit.Mvvm generates from it, which XAML binds to instead.</summary>
    private static IEnumerable<ISymbol> Related(ISymbol symbol) => ToolkitMembers.Related(symbol);
}
