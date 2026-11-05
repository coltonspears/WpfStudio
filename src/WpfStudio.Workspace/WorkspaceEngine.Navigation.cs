using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private const int DefinitionCandidates = 40;

    public async Task<IReadOnlyList<SourceLocation>> GetDefinitionAsync(DocumentPositionRequest request, CancellationToken cancellationToken) =>
        (await FindDefinitionAsync(request, cancellationToken).ConfigureAwait(false)).Locations;

    /// <summary>
    /// Where the symbol at a position is declared, preferring code somebody wrote: a member the Toolkit generated leads to the
    /// field or method it came from, and generated parts of a partial type stay out when an authored part exists. A name that
    /// does not resolve, or resolves into an assembly, falls back to source declarations of the same name. When nothing is
    /// found, <see cref="DefinitionResult.Status"/> says why.
    /// </summary>
    public async Task<DefinitionResult> FindDefinitionAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetNavigationDocumentAsync(request.Path, request.Version, cancellationToken).ConfigureAwait(false);
        var solution = document.Project.Solution;
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, request.Position, cancellationToken).ConfigureAwait(false);
        if (symbol is null or IErrorTypeSymbol)
        {
            if (await IdentifierAtAsync(document, request.Position, cancellationToken).ConfigureAwait(false) is not { } name) return new([], "Place the caret on a name to go to its definition.");
            var named = await DeclarationsNamedAsync(solution, name, null, cancellationToken).ConfigureAwait(false);
            return named.Count == 0
                ? new([], $"'{name}' does not resolve to a symbol, and no loaded project declares that name.", Resolved: false)
                : new(await ToLocationsAsync(solution, named, cancellationToken).ConfigureAwait(false), $"'{name}' does not resolve here. Showing the declarations with that name.", Resolved: false);
        }
        var source = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, cancellationToken).ConfigureAwait(false) ?? symbol;
        if (source is IFieldSymbol field && await GetProjectedNameDefinitionAsync(document.Project, field, cancellationToken).ConfigureAwait(false) is { } projected)
            return new(projected);
        var locations = await ToLocationsAsync(solution, [.. Parts(ToolkitMembers.Origin(source))], cancellationToken).ConfigureAwait(false);
        if (locations.Count > 0) return new(locations);
        // Declared in an assembly. The same member may still be in source here: a project referenced through its built output under another assembly name.
        if (source.Kind is not (SymbolKind.Namespace or SymbolKind.Local or SymbolKind.Parameter or SymbolKind.RangeVariable or SymbolKind.Label))
        {
            var named = await DeclarationsNamedAsync(solution, source.Name, source, cancellationToken).ConfigureAwait(false);
            if (named.Count > 0) return new(await ToLocationsAsync(solution, named, cancellationToken).ConfigureAwait(false));
        }
        var display = source.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
        return new([], source.ContainingAssembly is { } assembly
            ? $"{display} is declared in {assembly.Name}, which has no source in this workspace."
            : $"{display} has no source definition.");
    }

    /// <summary>A partial method or property is declared twice; both parts are places its author may mean.</summary>
    private static IEnumerable<ISymbol> Parts(ISymbol symbol)
    {
        yield return symbol;
        ISymbol? other = symbol switch
        {
            IMethodSymbol method => method.PartialImplementationPart ?? method.PartialDefinitionPart,
            IPropertySymbol property => property.PartialImplementationPart ?? property.PartialDefinitionPart,
            _ => null
        };
        if (other is not null) yield return other;
    }

    /// <summary>Source locations of the symbols, authored code first. Generated code is left out whenever authored code declares the symbol too.</summary>
    private static async Task<IReadOnlyList<SourceLocation>> ToLocationsAsync(Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var found = symbols.SelectMany(symbol => symbol.Locations.Where(location => location.IsInSource).Select(location => (Symbol: symbol, Location: location))).ToArray();
        bool Authored(Location location) => ToolkitMembers.IsAuthored(location) && solution.GetDocument(location.SourceTree) is not (null or SourceGeneratedDocument);
        if (found.Any(item => Authored(item.Location))) found = found.Where(item => Authored(item.Location)).ToArray();
        var locations = new List<SourceLocation>();
        foreach (var (symbol, location) in found.Take(DefinitionCandidates))
            locations.Add(await ToLocationAsync(location, symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat), cancellationToken).ConfigureAwait(false));
        return locations.DistinctBy(location => (location.Path.ToUpperInvariant(), location.Start)).ToArray();
    }

    /// <summary>
    /// Source declarations with a name. With <paramref name="like"/>, only those of the same kind in a container of the same
    /// name, which is how a member read from an assembly is matched to its source.
    /// </summary>
    private static async Task<IReadOnlyList<ISymbol>> DeclarationsNamedAsync(Solution solution, string name, ISymbol? like, CancellationToken cancellationToken)
    {
        if (name.Length == 0) return [];
        var declarations = await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: false, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false);
        static string Container(ISymbol symbol) => symbol.ContainingSymbol?.ToDisplayString() ?? "";
        return declarations.Where(symbol => !symbol.IsImplicitlyDeclared && symbol.Locations.Any(location => location.IsInSource)
                && (like is null || symbol.Kind == like.Kind && Container(symbol) == Container(like)))
            .Distinct<ISymbol>(SymbolEqualityComparer.Default).Take(DefinitionCandidates).ToArray();
    }

    /// <summary>The identifier at a position, or the one ending there: the caret rests after a name as often as inside it.</summary>
    private static async Task<string?> IdentifierAtAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { FullSpan.IsEmpty: false } root) return null;
        foreach (var offset in new[] { position, position - 1 })
        {
            if (offset < 0 || offset >= root.FullSpan.End) continue;
            var token = root.FindToken(offset);
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.Span.Start <= position && position <= token.Span.End) return token.ValueText;
        }
        return null;
    }

    /// <summary>
    /// The document a navigation request is about: a synchronized buffer, or the generator output a read-only snapshot
    /// was written from, so that following definitions does not end at the first generated file.
    /// </summary>
    private async Task<Document> GetNavigationDocumentAsync(string path, long version, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (!IsGeneratedSnapshot(path)) return GetDocument(path, version);
        Solution solution;
        lock (_gate)
        {
            if (_pages.Pending) throw new InvalidOperationException("Current XAML fields are being synchronized; retry with the current buffers.");
            solution = RequireSolution();
        }
        // The snapshot's folder is named after the hash of the generated path and text, so the generator's current output either matches it or has moved on.
        var name = Path.GetFileName(path);
        var hash = Path.GetFileName(Path.GetDirectoryName(path));
        foreach (var project in solution.Projects)
        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (generated.FilePath is not { } original || !string.Equals(Path.GetFileName(original), name, StringComparison.OrdinalIgnoreCase)) continue;
            var text = await generated.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(TextHash(original + "\n" + text), hash, StringComparison.OrdinalIgnoreCase)) return generated;
        }
        throw new InvalidOperationException("This generated source is from an earlier state of the project. Close it and navigate to it again.");
    }

    private static bool IsGeneratedSnapshot(string path) =>
        path.StartsWith(Path.Combine(Path.GetTempPath(), "WpfStudio", "GeneratedSources") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SourceLocation>> FindImplementationsAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetNavigationDocumentAsync(request.Path, request.Version, cancellationToken).ConfigureAwait(false);
        var symbols = await SymbolNavigation.FindImplementationsAsync(document, request.Position, cancellationToken).ConfigureAwait(false);
        var locations = new List<SourceLocation>();
        foreach (var symbol in symbols)
        foreach (var location in symbol.Locations.Where(l => l.IsInSource))
            locations.Add(await ToLocationAsync(location, Describe(symbol), cancellationToken).ConfigureAwait(false));
        return locations.DistinctBy(l => (l.Path.ToUpperInvariant(), l.Start)).ToArray();
    }

    public async Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(SymbolSearchRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        Solution solution;
        lock (_gate) solution = RequireSolution();
        var matches = await SymbolNavigation.SearchAsync(solution, request.Query, Math.Clamp(request.Limit, 1, 500), cancellationToken).ConfigureAwait(false);
        var results = new List<SymbolSearchResult>(matches.Count);
        foreach (var (symbol, location) in matches)
            results.Add(new(symbol.Name, SymbolNavigation.KindOf(symbol), symbol.ContainingSymbol is INamespaceSymbol { IsGlobalNamespace: true } ? null : symbol.ContainingSymbol?.ToDisplayString(),
                await ToLocationAsync(location, Describe(symbol), cancellationToken).ConfigureAwait(false)));
        return results;
    }

    private static string Describe(ISymbol symbol) => SymbolNavigation.KindOf(symbol).ToLowerInvariant() + " " + symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
}
