using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    public async Task<IReadOnlyList<SourceLocation>> FindImplementationsAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var document = GetDocument(request.Path, request.Version);
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
