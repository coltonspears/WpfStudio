using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private const int CodeLensDeclarations = 300;
    private static readonly TimeSpan CodeLensBudget = TimeSpan.FromSeconds(12);

    public async Task<CodeLensResult> GetCodeLensAsync(DocumentRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        _ = GetDocument(request.Path, request.Version);
        // One XAML scan serves every declaration in the file.
        var snapshot = await CaptureSymbolSnapshotAsync(new SymbolReferenceRequest(request.Path, 0, request.Version), cancellationToken).ConfigureAwait(false);
        var solution = snapshot.Solution;
        var document = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(request.Path)).Select(solution.GetDocument).FirstOrDefault()
            ?? throw new FileNotFoundException("This file is not part of the loaded C# workspace.", request.Path);
        var identities = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default);
        var xaml = new Dictionary<string, HashSet<(string Path, int Start)>>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        foreach (var occurrence in file.Occurrences)
            if (await IdentityAsync(occurrence.Symbol, solution, identities, cancellationToken).ConfigureAwait(false) is { } key)
                (xaml.TryGetValue(key, out var uses) ? uses : xaml[key] = []).Add((file.Path.ToUpperInvariant(), occurrence.Start)); // Linked contexts count once.

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return new(request.Version, [], false, "The document could not be analyzed.");
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var declarations = root.DescendantNodes(node => node is not (BlockSyntax or ArrowExpressionClauseSyntax or AccessorListSyntax))
            .Select(node => Declared(model, node, cancellationToken)).Where(found => found is not null).Select(found => found!.Value)
            .Take(CodeLensDeclarations + 1).ToArray();
        var complete = declarations.Length <= CodeLensDeclarations && snapshot.Complete;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CodeLensBudget);
        var entries = new List<CodeLensEntry>();
        foreach (var (symbol, identifier) in declarations.Take(CodeLensDeclarations))
        {
            if (budget.IsCancellationRequested) { complete = false; break; }
            try
            {
                int references = 0, xamlReferences = 0;
                string? xamlName = null;
                foreach (var related in Related(symbol))
                {
                    var found = (await SymbolFinder.FindReferencesAsync(related, solution, budget.Token).ConfigureAwait(false)).ToArray();
                    references += found.SelectMany(reference => reference.Locations)
                        .Count(location => location.Location.IsInSource && location.Document.FilePath is { } path && !IsGenerated(path) && !location.IsImplicit);
                    var family = await SymbolFamilyAsync(related, found, solution, budget.Token).ConfigureAwait(false);
                    var uses = family.Keys.SelectMany(key => xaml.GetValueOrDefault(key) ?? []).Distinct().Count();
                    xamlReferences += uses;
                    if (uses > 0 && !SymbolEqualityComparer.Default.Equals(related, symbol)) xamlName = related.Name;
                }
                var line = text.Lines.GetLinePosition(identifier.SpanStart).Line + 1;
                entries.Add(new(line, identifier.SpanStart, symbol.Name, references, xamlReferences, xamlName));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { complete = false; break; }
        }
        EnsureSymbolSnapshotCurrent(snapshot);
        return new(request.Version, entries, complete,
            complete ? null : "Some counts are missing: " + string.Join(" ", BoundedWarnings(snapshot.Warnings).DefaultIfEmpty("the analysis budget was reached.")));
    }

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
    private static IEnumerable<ISymbol> Related(ISymbol symbol)
    {
        yield return symbol;
        if (symbol.ContainingType is not { } type) yield break;
        bool Has(string attribute) => symbol.GetAttributes().Any(a => a.AttributeClass?.Name == attribute + "Attribute" || a.AttributeClass?.Name == attribute);
        string? generated = symbol switch
        {
            IFieldSymbol field when Has("ObservableProperty") => PropertyName(field.Name),
            IMethodSymbol method when Has("RelayCommand") => (method.Name.EndsWith("Async", StringComparison.Ordinal) && method.Name.Length > 5 ? method.Name[..^5] : method.Name) + "Command",
            _ => null
        };
        if (generated is null) yield break;
        foreach (var member in type.GetMembers(generated).OfType<IPropertySymbol>()) yield return member;
    }

    // The Toolkit's field-to-property naming: _name, m_name and name become Name.
    private static string PropertyName(string field)
    {
        var name = field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_');
        return name.Length == 0 ? field : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
