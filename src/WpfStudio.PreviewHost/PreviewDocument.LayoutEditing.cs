using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

internal sealed partial class PreviewDocument
{
    internal sealed record LayoutSource(string Id, SourceLocation Element, SourceLocation? Parent, Type Type, string? Unavailable);
    private readonly Dictionary<string, LayoutSource> _layoutSources = new(StringComparer.Ordinal);
    internal string LayoutSourceHash { get; private set; } = "";

    private void InitializeLayoutSources(PreviewRequest request) =>
        LayoutSourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Text)));

    private void RememberLayoutSource(string id, XElement element, Type type, PreviewRequest request, Assembly? assembly)
    {
        XElement? parent = element.Parent;
        while (parent is not null && parent.Name.LocalName.Contains('.')) parent = parent.Parent;
        string? unavailable = null;
        int depth = 0;
        for (var current = element; current is not null; current = current.Parent)
        {
            if (++depth > 64) { unavailable = "The authored ancestry exceeds the layout editing limit."; break; }
            if (current.Name.NamespaceName == "http://schemas.microsoft.com/expression/blend/2008")
            { unavailable = "Design-only objects cannot establish runtime layout source edits."; break; }
            if (current.Attribute(Xaml + "Key") is not null || current.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal))
            { unavailable = "Resource declarations do not establish a direct authored layout child."; break; }
            var currentType = ReferenceEquals(current, element) ? type : ResolveType(current.Name, assembly);
            if (currentType is not null && (typeof(FrameworkTemplate).IsAssignableFrom(currentType)
                || typeof(ResourceDictionary).IsAssignableFrom(currentType) || typeof(Style).IsAssignableFrom(currentType)))
            { unavailable = "Template and resource declarations can create shared or deferred instances. Edit their XAML directly."; break; }
        }
        _layoutSources[id] = new(id, GetSource(request, element), parent is null ? null : GetSource(request, parent), type, unavailable);
    }

    internal LayoutSource? GetLayoutSource(DependencyObject target) =>
        PreviewSource.GetId(target) is { } id && _layoutSources.TryGetValue(id, out var source) ? source : null;

    internal static string? LayoutContentProperty(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            var attribute = current.CustomAttributes.FirstOrDefault(item => item.AttributeType == typeof(System.Windows.Markup.ContentPropertyAttribute));
            if (attribute is not null) return attribute.ConstructorArguments.FirstOrDefault().Value as string;
        }
        return null;
    }
}
