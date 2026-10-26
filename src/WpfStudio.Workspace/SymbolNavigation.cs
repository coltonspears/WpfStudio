using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace WpfStudio.Workspace;

/// <summary>Solution-wide symbol search and implementation lookup over one Roslyn snapshot.</summary>
public static class SymbolNavigation
{
    public static async Task<IReadOnlyList<(ISymbol Symbol, Location Location)>> SearchAsync(Solution solution, string query, int limit, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0 || limit <= 0) return [];
        // "Customer.Save" searches members named Save whose container mentions Customer.
        var split = query.LastIndexOf('.');
        var container = split > 0 ? query[..split] : null;
        var name = split > 0 ? query[(split + 1)..] : query;
        if (name.Length == 0) return [];
        var found = new Dictionary<(string, int), (ISymbol Symbol, Location Location)>();
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var symbol in await SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, name, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false))
            {
                if (symbol.IsImplicitlyDeclared || symbol is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion) }) continue;
                if (container is not null && symbol.ContainingSymbol?.ToDisplayString().Contains(container, StringComparison.OrdinalIgnoreCase) != true) continue;
                // Multi-targeted projects declare the same source symbol once per target framework.
                foreach (var location in symbol.Locations.Where(l => l.IsInSource && l.SourceTree?.FilePath is not null))
                    found.TryAdd((location.SourceTree!.FilePath, location.SourceSpan.Start), (symbol, location));
            }
        }
        return found.Values.OrderBy(item => Rank(item.Symbol.Name, name)).ThenBy(item => item.Symbol is INamedTypeSymbol ? 0 : 1)
            .ThenBy(item => item.Symbol.Name.Length).ThenBy(item => item.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Location.SourceTree!.FilePath, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
    }

    private static int Rank(string candidate, string query) =>
        candidate == query ? 0 : candidate.Equals(query, StringComparison.OrdinalIgnoreCase) ? 1 :
        candidate.StartsWith(query, StringComparison.Ordinal) ? 2 : candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 3 :
        candidate.Contains(query, StringComparison.OrdinalIgnoreCase) ? 4 : 5;

    public static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true } => "Record",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "Interface",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "Struct",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "Enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "Delegate",
        INamedTypeSymbol => "Class",
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "Constructor",
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IEventSymbol => "Event",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "Enum member",
        IFieldSymbol { IsConst: true } => "Constant",
        IFieldSymbol => "Field",
        _ => symbol.Kind.ToString()
    };

    /// <summary>
    /// Mirrors Go to Implementation: interface and abstract members list their implementations, virtual members
    /// their overrides, and types their implementing or derived types. Anything else navigates to its own definition.
    /// </summary>
    public static async Task<IReadOnlyList<ISymbol>> FindImplementationsAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is null) return [];
        var solution = document.Project.Solution;
        symbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, cancellationToken).ConfigureAwait(false) ?? symbol;
        symbol = symbol.OriginalDefinition;
        var results = new List<ISymbol>();
        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface } type:
                results.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false));
                results.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
            case INamedTypeSymbol { TypeKind: TypeKind.Class } type:
                results.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false));
                if (results.Count == 0 && !type.IsAbstract) results.Add(type);
                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol:
                var pending = new Queue<ISymbol>([symbol]);
                if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
                    foreach (var implementation in await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false))
                    { results.Add(implementation); pending.Enqueue(implementation); }
                // An abstract class can implement an interface member that its subclasses then override.
                while (pending.TryDequeue(out var member))
                    if (member.IsAbstract || member.IsVirtual || member.IsOverride)
                        results.AddRange(await SymbolFinder.FindOverridesAsync(member, solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                if (results.Any(r => !r.IsAbstract)) results.RemoveAll(r => r.IsAbstract);
                if (results.Count == 0 && !symbol.IsAbstract) results.Add(symbol);
                break;
            default:
                results.Add(symbol);
                break;
        }
        return results.Distinct<ISymbol>(SymbolEqualityComparer.Default).Where(s => s.Locations.Any(l => l.IsInSource)).ToArray();
    }
}
