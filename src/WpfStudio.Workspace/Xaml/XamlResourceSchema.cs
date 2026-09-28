using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

/// <summary>Shared passive schema evidence for import discovery and resource lookup.</summary>
internal sealed class XamlResourceSchema(Compilation compilation, CancellationToken token)
{
    private readonly SchemaTypeResolver _types = new(compilation, token);
    private readonly IAssemblySymbol[] _frameworkAssemblies = compilation.SourceModule.ReferencedAssemblySymbols
        .Where(assembly => assembly.Identity.Name == "PresentationFramework").Take(2).ToArray();

    public bool IsElement(Element element, string name, string? assembly, bool allowReduced = false)
    {
        if (element.LocalName != name) return false;
        if (allowReduced) return element.Namespace is null || element.Namespace == Presentation;
        return IsFrameworkType(_types.Resolve(element, element.Name, assembly), "System.Windows." + name);
    }

    public bool IsMergeProperty(Element element, string? assembly, bool allowReduced = false)
    {
        if (element.LocalName != "ResourceDictionary.MergedDictionaries") return false;
        if (allowReduced) return element.Namespace == Presentation;
        return IsFrameworkType(_types.Resolve(element, element.Name[..element.Name.LastIndexOf('.')], assembly), "System.Windows.ResourceDictionary");
    }

    public bool IsFrameworkType(INamedTypeSymbol? type, string metadataName)
    {
        if (type is null || _frameworkAssemblies.Length > 1) return false;
        return _frameworkAssemblies.Length == 1
            ? SymbolEqualityComparer.Default.Equals(type, _frameworkAssemblies[0].GetTypeByMetadataName(metadataName))
            : type.ToDisplayString() == metadataName;
    }

    public bool HasBaseOverride(Element element)
    {
        int depth = 0;
        for (Element? current = element; current is not null; current = current.Parent)
        {
            token.ThrowIfCancellationRequested();
            if (++depth > 256 || current.Attributes.Any(attribute => attribute.Name == "xml:base")) return true;
        }
        return false;
    }
}
