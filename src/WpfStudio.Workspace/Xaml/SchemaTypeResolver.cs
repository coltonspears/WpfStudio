using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

/// <summary>Reads the XAML type system from compiler symbols without loading project assemblies.</summary>
internal sealed class SchemaTypeResolver
{
    private readonly Compilation _compilation;
    private readonly CancellationToken _token;
    private readonly Metadata _metadata;
    private readonly ImmutableArray<IAssemblySymbol> _assemblies;
    private readonly ImmutableArray<Mapping> _mappings;
    private sealed record Mapping(string Uri, string Namespace, IAssemblySymbol Assembly);
    private sealed record Metadata(ImmutableArray<IAssemblySymbol> Assemblies, ImmutableArray<Mapping> Mappings,
        ImmutableDictionary<string, ImmutableArray<Mapping>> ByUri,
        ImmutableDictionary<string, ImmutableArray<IAssemblySymbol>> ByAssembly);
    // Compilation is immutable. A new unsaved/generated/reference snapshot is a new
    // key, and the ephemeron table does not retain otherwise unused compilations.
    // No request token, syntax element, result cache, or mutable lookup state is shared.
    private static readonly ConditionalWeakTable<Compilation, Metadata> MetadataCache = new();

    public SchemaTypeResolver(Compilation compilation, CancellationToken token = default)
    {
        _compilation = compilation; _token = token;
        token.ThrowIfCancellationRequested();
        // Failed/canceled factories are not installed. Concurrent factories may run,
        // but only a completely built immutable value can become visible to callers.
        _metadata = MetadataCache.GetValue(compilation, value => CreateMetadata(value, token));
        token.ThrowIfCancellationRequested();
        _assemblies = _metadata.Assemblies;
        _mappings = _metadata.Mappings;
    }

    private static Metadata CreateMetadata(Compilation compilation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var assemblies = new[] { compilation.Assembly }.Concat(compilation.SourceModule.ReferencedAssemblySymbols).ToImmutableArray();
        var assemblyNames = new Dictionary<string, List<IAssemblySymbol>>(StringComparer.OrdinalIgnoreCase);
        void AddName(string name, IAssemblySymbol assembly)
        {
            if (!assemblyNames.TryGetValue(name, out var values)) assemblyNames[name] = values = [];
            values.Add(assembly);
        }
        foreach (var assembly in assemblies)
        {
            token.ThrowIfCancellationRequested();
            AddName(assembly.Identity.Name, assembly);
            string fullName = assembly.Identity.GetDisplayName();
            if (!StringComparer.OrdinalIgnoreCase.Equals(assembly.Identity.Name, fullName)) AddName(fullName, assembly);
        }
        var mappings = ImmutableArray.CreateBuilder<Mapping>();
        foreach (var assembly in assemblies)
        {
            token.ThrowIfCancellationRequested();
            foreach (var attribute in assembly.GetAttributes())
            {
                token.ThrowIfCancellationRequested();
                if (attribute.AttributeClass?.ToDisplayString() != "System.Windows.Markup.XmlnsDefinitionAttribute"
                    || attribute.ConstructorArguments.Length < 2
                    || attribute.ConstructorArguments[0].Value is not string uri
                    || attribute.ConstructorArguments[1].Value is not string clrNamespace) continue;
                var name = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "AssemblyName").Value.Value as string;
                var owner = string.IsNullOrEmpty(name) ? assembly : assemblyNames.TryGetValue(name, out var candidates) && candidates.Count == 1 ? candidates[0] : null;
                if (owner is not null) mappings.Add(new(uri, clrNamespace, owner));
            }
        }
        token.ThrowIfCancellationRequested();
        var result = new Metadata(assemblies, mappings.ToImmutable(),
            mappings.GroupBy(mapping => mapping.Uri, StringComparer.Ordinal).ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal),
            assemblyNames.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.OrdinalIgnoreCase));
        token.ThrowIfCancellationRequested();
        return result;
    }

    public INamedTypeSymbol? Resolve(Element element, string qualifiedName, string? declaringAssembly = null)
    {
        var name = SplitName(qualifiedName.Trim());
        var uri = element.LookupNamespace(name.Prefix);
        if (uri is null) return null;
        return Resolve(WithDeclaringAssembly(uri, declaringAssembly), name.Local);
    }

    public INamedTypeSymbol? Resolve(string uri, string name)
    {
        _token.ThrowIfCancellationRequested();
        if (uri == Language)
        {
            var metadata = name switch
            {
                "String" => "System.String", "Boolean" => "System.Boolean", "Byte" => "System.Byte", "SByte" => "System.SByte",
                "Int16" => "System.Int16", "Int32" => "System.Int32", "Int64" => "System.Int64", "UInt16" => "System.UInt16",
                "UInt32" => "System.UInt32", "UInt64" => "System.UInt64", "Single" => "System.Single", "Double" => "System.Double",
                "Decimal" => "System.Decimal", "Char" => "System.Char", "Object" => "System.Object", "TimeSpan" => "System.TimeSpan",
                "Uri" => "System.Uri", "Type" => "System.Windows.Markup.TypeExtension", "Static" => "System.Windows.Markup.StaticExtension",
                "Null" => "System.Windows.Markup.NullExtension", "Array" => "System.Windows.Markup.ArrayExtension", _ => null
            };
            return metadata is null ? null : _compilation.GetTypeByMetadataName(metadata);
        }
        var candidates = Mappings(uri).Select(mapping => mapping.Assembly.GetTypeByMetadataName(Qualify(mapping.Namespace, name)))
            .OfType<INamedTypeSymbol>().Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public INamedTypeSymbol? ResolveTypeValue(Element element, string value, string? declaringAssembly = null)
    {
        value = value.Trim();
        if (!value.StartsWith('{')) return Resolve(element, value, declaringAssembly);
        var extension = ParseExtension(value);
        if (extension is null || !extension.IsComplete || !IsExtension(element, extension, Language, "Type")) return null;
        var name = extension.Argument("TypeName") ?? extension.Positional;
        return name is null ? null : Resolve(element, name.Value, declaringAssembly);
    }

    internal INamedTypeSymbol? ResolveMetadataName(string name, string? declaringAssembly) =>
        (declaringAssembly is null ? _compilation.Assembly : FindAssembly(declaringAssembly))?.GetTypeByMetadataName(name);

    internal static string WithDeclaringAssembly(string uri, string? assembly) => assembly is not null &&
        uri.StartsWith("clr-namespace:", StringComparison.Ordinal) && !uri.Split(';').Skip(1).Any(part => part.TrimStart().StartsWith("assembly=", StringComparison.Ordinal))
            ? uri + ";assembly=" + assembly : uri;

    public bool IsKnownNamespace(string uri) => uri == Language || Mappings(uri).Any();

    public IEnumerable<INamedTypeSymbol> Types(string uri)
    {
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var mapping in Mappings(uri))
        {
            _token.ThrowIfCancellationRequested();
            var ns = Namespace(mapping.Assembly.GlobalNamespace, mapping.Namespace);
            if (ns is null) continue;
            foreach (var type in ns.GetTypeMembers())
                if (type.Arity == 0 && type.DeclaredAccessibility == Accessibility.Public && seen.Add(type)) yield return type;
        }
    }

    public IEnumerable<string> NamespaceUris()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in new[] { Presentation, Language, Design, "http://schemas.openxmlformats.org/markup-compatibility/2006" }.Concat(_mappings.Select(m => m.Uri)))
            if (seen.Add(uri)) yield return uri;
        foreach (var assembly in _assemblies)
        {
            var suffix = SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly) ? "" : ";assembly=" + assembly.Identity.Name;
            foreach (var ns in PublicNamespaces(assembly.GlobalNamespace))
            {
                var uri = "clr-namespace:" + (ns.IsGlobalNamespace ? "" : ns.ToDisplayString()) + suffix;
                if (seen.Add(uri)) yield return uri;
            }
        }
    }

    private IEnumerable<Mapping> Mappings(string uri)
    {
        if (uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var parts = uri["clr-namespace:".Length..].Split(';');
            var name = parts.Skip(1).FirstOrDefault(p => p.TrimStart().StartsWith("assembly=", StringComparison.Ordinal))?.Trim()["assembly=".Length..];
            var assembly = string.IsNullOrEmpty(name) ? _compilation.Assembly : FindAssembly(name);
            if (assembly is not null && Namespace(assembly.GlobalNamespace, parts[0]) is not null) yield return new(uri, parts[0], assembly);
            yield break;
        }
        if (_metadata.ByUri.TryGetValue(uri, out var mappings))
            foreach (var mapping in mappings) yield return mapping;
    }

    private IAssemblySymbol? FindAssembly(string name)
    {
        _token.ThrowIfCancellationRequested();
        return _metadata.ByAssembly.TryGetValue(name, out var matches) && matches.Length == 1 ? matches[0] : null;
    }

    private IEnumerable<INamespaceSymbol> PublicNamespaces(INamespaceSymbol root)
    {
        _token.ThrowIfCancellationRequested();
        if (root.GetTypeMembers().Any(type => type.Arity == 0 && type.DeclaredAccessibility == Accessibility.Public)) yield return root;
        foreach (var child in root.GetNamespaceMembers())
        foreach (var ns in PublicNamespaces(child)) yield return ns;
    }

    private static INamespaceSymbol? Namespace(INamespaceSymbol root, string name)
    {
        if (name.Length == 0) return root;
        INamespaceSymbol? current = root;
        foreach (var segment in name.Split('.'))
        {
            current = current?.GetNamespaceMembers().FirstOrDefault(ns => ns.Name == segment);
            if (current is null) return null;
        }
        return current;
    }

    private static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
}
