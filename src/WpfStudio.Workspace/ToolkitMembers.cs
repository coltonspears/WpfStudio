using Microsoft.CodeAnalysis;

namespace WpfStudio.Workspace;

/// <summary>
/// The members CommunityToolkit.Mvvm generates from <c>[ObservableProperty]</c> fields and <c>[RelayCommand]</c> methods,
/// and the way back from a generated member to the code its author wrote.
/// </summary>
internal static class ToolkitMembers
{
    /// <summary>The symbol and the members the Toolkit generates from it, which XAML binds to instead.</summary>
    public static IEnumerable<ISymbol> Related(ISymbol symbol)
    {
        yield return symbol;
        if (symbol.ContainingType is not { } type) yield break;
        bool Has(string attribute) => symbol.GetAttributes().Any(a => a.AttributeClass?.Name == attribute + "Attribute" || a.AttributeClass?.Name == attribute);
        string[] generated = symbol switch
        {
            IFieldSymbol field when Has("ObservableProperty") => [PropertyName(field.Name)],
            IMethodSymbol method when Has("RelayCommand") => CommandNames(method.Name),
            _ => []
        };
        foreach (var name in generated)
        foreach (var member in type.GetMembers(name).OfType<IPropertySymbol>()) yield return member;
    }

    /// <summary>
    /// The field or method a generated property came from, when the property is declared in generated code only. Going to
    /// the definition of <c>Title</c> or <c>SaveCommand</c> should arrive at <c>_title</c> or <c>Save</c>, not at a generated file.
    /// </summary>
    public static ISymbol Origin(ISymbol symbol)
    {
        if (symbol is not IPropertySymbol || symbol.ContainingType is not { } type || symbol.Locations.Any(IsAuthored)) return symbol;
        foreach (var member in type.GetMembers())
        {
            if (member is not (IFieldSymbol or IMethodSymbol) || member.IsImplicitlyDeclared || !member.Locations.Any(IsAuthored)) continue;
            if (Related(member).Skip(1).Any(generated => SymbolEqualityComparer.Default.Equals(generated, symbol))) return member;
        }
        return symbol;
    }

    /// <summary>A location in a file somebody wrote, as opposed to a source generator's or the compiler's output.</summary>
    public static bool IsAuthored(Location location) => location.IsInSource && location.SourceTree?.FilePath is { Length: > 0 } path && !IsGeneratedPath(path);

    public static bool IsGeneratedPath(string path) => path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
        || GeneratedPaths.IsIntermediate(path);

    // The Toolkit's field-to-property naming: _name, m_name and name become Name.
    private static string PropertyName(string field)
    {
        var name = field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_');
        return name.Length == 0 ? field : char.ToUpperInvariant(name[0]) + name[1..];
    }

    // The Toolkit's method-to-command naming: SaveAsync and Save become SaveCommand, and so does OnSave.
    private static string[] CommandNames(string method)
    {
        var name = method.EndsWith("Async", StringComparison.Ordinal) && method.Length > 5 ? method[..^5] : method;
        return name.Length > 2 && name.StartsWith("On", StringComparison.Ordinal) && !char.IsLower(name[2])
            ? [name[2..] + "Command", name + "Command"] : [name + "Command"];
    }
}
