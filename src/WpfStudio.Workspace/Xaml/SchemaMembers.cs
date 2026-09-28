using Microsoft.CodeAnalysis;

namespace WpfStudio.Workspace.Xaml;

internal sealed record SchemaMember(string Name, ISymbol Symbol, ITypeSymbol? ValueType, bool CanWrite, bool IsEvent = false, bool IsAttached = false);

internal static class SchemaMembers
{
    public static IEnumerable<SchemaMember> Instance(INamedTypeSymbol type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.TypeKind != TypeKind.Error; current = current.BaseType)
        foreach (var member in current.GetMembers())
        {
            if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) continue;
            if (member is IPropertySymbol { IsIndexer: false } property && names.Add(property.Name))
                yield return new(property.Name, property, property.Type, property.SetMethod?.DeclaredAccessibility == Accessibility.Public);
            else if (member is IEventSymbol evt && names.Add(evt.Name))
                yield return new(evt.Name, evt, evt.Type, true, IsEvent: true);
        }
    }

    public static IEnumerable<SchemaMember> Attached(INamedTypeSymbol type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.TypeKind != TypeKind.Error; current = current.BaseType)
        foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
        {
            if (!method.IsStatic || method.DeclaredAccessibility != Accessibility.Public || method.IsGenericMethod) continue;
            if (method.Name.StartsWith("Set", StringComparison.Ordinal) && method.Name.Length > 3 && method.Parameters.Length == 2 && method.ReturnsVoid)
            {
                var name = method.Name[3..];
                if (names.Add(name)) yield return new(name, method, method.Parameters[1].Type, true, IsAttached: true);
            }
        }
        // Collection-valued attached members (for example behavior collections)
        // can expose only a getter and still support property-element syntax.
        for (var current = type; current is not null && current.TypeKind != TypeKind.Error; current = current.BaseType)
        foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.IsStatic && method.DeclaredAccessibility == Accessibility.Public && !method.IsGenericMethod &&
                method.Name.StartsWith("Get", StringComparison.Ordinal) && method.Name.Length > 3 && method.Parameters.Length == 1 && !method.ReturnsVoid && names.Add(method.Name[3..]))
                yield return new(method.Name[3..], method, method.ReturnType, false, IsAttached: true);
        }
        // WPF also recognizes true attached events through Add{Name}Handler.
        foreach (var group in EventAdders(type).GroupBy(method => method.Name, StringComparer.Ordinal))
        {
            // Reflection-based WPF event lookup cannot safely choose an overloaded adder.
            var overloads = group.Take(2).ToArray();
            if (overloads.Length != 1) continue;
            var method = overloads[0];
            bool dependencyObject = false;
            for (var target = method.Parameters[0].Type as INamedTypeSymbol; target is not null; target = target.BaseType)
                if (target.ToDisplayString() == "System.Windows.DependencyObject") { dependencyObject = true; break; }
            var name = method.Name[3..^7];
            bool routedField = method.ContainingType.GetMembers(name + "Event").OfType<IFieldSymbol>()
                .Any(field => field.IsStatic && field.DeclaredAccessibility == Accessibility.Public
                    && field.Type.ToDisplayString() == "System.Windows.RoutedEvent");
            if (dependencyObject && routedField && names.Add(name))
                yield return new(name, method, method.Parameters[1].Type, true, IsEvent: true, IsAttached: true);
        }
    }

    private static IEnumerable<IMethodSymbol> EventAdders(INamedTypeSymbol type)
    {
        for (var current = type; current is not null && current.TypeKind != TypeKind.Error; current = current.BaseType)
        foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            if (method.IsStatic && method.DeclaredAccessibility == Accessibility.Public && !method.IsGenericMethod && method.ReturnsVoid
                && method.Parameters.Length == 2 && method.Parameters.All(parameter => parameter.RefKind == RefKind.None)
                && method.Name.StartsWith("Add", StringComparison.Ordinal) && method.Name.EndsWith("Handler", StringComparison.Ordinal)
                && method.Name.Length > 10 && method.Parameters[1].Type.TypeKind == TypeKind.Delegate) yield return method;
    }

    public static SchemaMember? Find(INamedTypeSymbol type, string name, bool attached = false) =>
        attached ? Attached(type).FirstOrDefault(member => member.Name == name) ?? Instance(type).FirstOrDefault(member => member.Name == name)
            : Instance(type).FirstOrDefault(member => member.Name == name);

    public static bool IsComplete(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.TypeKind == TypeKind.Error) return false;
        return true;
    }

    public static bool DerivesFrom(INamedTypeSymbol? type, INamedTypeSymbol owner)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, owner)) return true;
        return false;
    }

    public static string Describe(ISymbol symbol)
    {
        var text = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var documentation = symbol.GetDocumentationCommentXml();
        if (!string.IsNullOrWhiteSpace(documentation))
        {
            try
            {
                var summary = System.Xml.Linq.XElement.Parse(documentation).Element("summary")?.Value;
                if (!string.IsNullOrWhiteSpace(summary)) text += "\n\n" + string.Join(" ", summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
            catch (System.Xml.XmlException) { }
        }
        return text;
    }
}
