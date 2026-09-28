using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.PreviewHost;

internal sealed partial class PreviewDocument
{
    private static readonly XNamespace Design = "http://schemas.microsoft.com/expression/blend/2008";
    private readonly Dictionary<string, HashSet<DependencyProperty>> _designProperties = new(StringComparer.Ordinal);
    private sealed record DesignMember(DependencyProperty Property, XName AttributeName, XName ElementName);
    private sealed record DesignValue(XObject Source, DesignMember Member, string? Literal, XElement? Element);

    public bool IsDesignTimeProperty(DependencyObject target, DependencyProperty property)
    {
        // Source identities survive templates. Follow only proven property inheritance;
        // a local/style value is a boundary even when an ancestor has design data.
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        for (DependencyObject? current = target; current is not null && visited.Count < 128 && visited.Add(current);)
        {
            if (PreviewSource.GetId(current) is { } id && _designProperties.TryGetValue(id, out var properties) && properties.Contains(property)) return true;
            if (DependencyPropertyHelper.GetValueSource(current, property).BaseValueSource != BaseValueSource.Inherited) return false;
            current = current is FrameworkElement element ? element.Parent ?? (current is Visual ? VisualTreeHelper.GetParent(current) : null)
                : current is FrameworkContentElement content ? content.Parent : null;
        }
        return false;
    }

    private void ApplyDesignTimeValues(XElement root, PreviewRequest request, Assembly? assembly, XNamespace sourceNamespace)
    {
        int count = 0;
        bool budgetReported = false;
        foreach (var element in root.DescendantsAndSelf().ToArray())
        {
            if (element != root && element.Document is null) continue;
            if (element.Name.Namespace == Design)
            {
                if (element.Parent?.Name.Namespace == Design) continue;
                if (request.UseDesignTimeValues) DesignDiagnostic(element, "This design-only element is unsupported and was omitted from the preview.");
                element.Remove();
                continue;
            }
            var attributes = element.Attributes().Where(attribute => attribute.Name.Namespace == Design).ToArray();
            var propertyElements = element.Elements().Where(child => child.Name.Namespace == Design).ToArray();
            if (attributes.Length == 0 && propertyElements.Length == 0) continue;
            if (!request.UseDesignTimeValues)
            {
                foreach (var attribute in attributes) attribute.Remove();
                foreach (var child in propertyElements) child.Remove();
                continue;
            }
            Type? type = ResolveType(element.Name, assembly);
            var values = new List<DesignValue>();
            foreach (var source in attributes.Cast<XObject>().Concat(propertyElements))
            {
                if (++count > 2048)
                {
                    if (!budgetReported) DesignDiagnostic(source, "The design-time value budget was reached; remaining design declarations were omitted.");
                    budgetReported = true;
                    continue;
                }
                string name = source is XAttribute attribute ? attribute.Name.LocalName : ((XElement)source).Name.LocalName;
                string? literal = (source as XAttribute)?.Value;
                bool sizeHint = name is "DesignWidth" or "DesignHeight";
                if (sizeHint)
                {
                    name = name == "DesignWidth" ? "Width" : "Height";
                    if (attributes.Any(attribute => attribute.Name.LocalName == name) || element.Attribute(name) is { } authored && authored.Value != "Auto"
                        || element.Elements().Any(child => child.Name.Namespace != Design && child.Name.LocalName.EndsWith("." + name, StringComparison.Ordinal)))
                    {
                        DesignDiagnostic(source, $"The {name} design hint is omitted because an explicit size is declared.", "Information");
                        continue;
                    }
                }
                if (type is null || ResolveDesignMember(element, type, name, assembly) is not { } member || member.Property.ReadOnly
                    || member.Property == FrameworkElement.NameProperty)
                {
                    DesignDiagnostic(source, $"Design-time member '{name}' is not a supported writable dependency property.");
                    continue;
                }
                if (literal is not null)
                {
                    if (literal.TrimStart().StartsWith('{') && !literal.StartsWith("{}", StringComparison.Ordinal))
                    {
                        DesignDiagnostic(source, $"Design-time markup for '{name}' is unsupported (including DesignInstance, CreateList, DesignData and binding expressions); the authored runtime value is retained.");
                        continue;
                    }
                    string scalar = literal.StartsWith("{}", StringComparison.Ordinal) ? literal[2..] : literal;
                    if (!ScalarPropertyValues.TryConvert(member.Property.PropertyType, scalar, false, out var value, out var error) || !member.Property.IsValidValue(value))
                    {
                        DesignDiagnostic(source, $"Design-time value for '{name}' was omitted: {error ?? "the dependency property rejects this value"}.");
                        continue;
                    }
                }
                else if (source is XElement property)
                {
                    if (!name.Contains('.') || property.Descendants().Take(1025).Count() > 1024
                        || property.Descendants().Any(child => child.Name.Namespace == Design || child.Ancestors().TakeWhile(parent => parent != property).Take(65).Count() > 64))
                    {
                        DesignDiagnostic(source, $"Design-time property element '{name}' is unsupported or exceeds the object/depth budget.");
                        continue;
                    }
                }
                values.Add(new(source, member, literal, source as XElement));
            }
            foreach (var group in values.GroupBy(value => value.Member.Property))
            {
                if (group.Count() != 1)
                {
                    foreach (var duplicate in group) DesignDiagnostic(duplicate.Source, $"Multiple design declarations target '{duplicate.Member.Property.Name}'; the authored runtime value is retained.");
                    continue;
                }
                var value = group.Single();
                RemoveRuntimeValue(element, type!, value.Member.Property, assembly);
                if (value.Element is { } property)
                {
                    property.Name = value.Member.ElementName;
                }
                else element.SetAttributeValue(value.Member.AttributeName, value.Literal);
                if (element.Attribute(sourceNamespace + "PreviewSource.Id") is { } identity)
                {
                    if (!_designProperties.TryGetValue(identity.Value, out var properties)) _designProperties[identity.Value] = properties = [];
                    properties.Add(value.Member.Property);
                }
                DesignDiagnostic(value.Source, $"Design-time '{value.Member.Property.Name}' overrides the authored runtime value in this preview; any replaced binding is not evaluated here.", "Information");
            }
            foreach (var attribute in attributes) attribute.Remove();
            foreach (var child in propertyElements.Where(child => child.Name.Namespace == Design)) child.Remove();
        }
    }

    private static DesignMember? ResolveDesignMember(XElement element, Type type, string name, Assembly? assembly, XNamespace? declaredNamespace = null)
    {
        int dot = name.LastIndexOf('.');
        string propertyName = dot < 0 ? name : name[(dot + 1)..];
        Type owner = type;
        XNamespace ownerNamespace = element.Name.Namespace;
        string ownerName = element.Name.LocalName;
        if (dot >= 0)
        {
            ownerName = name[..dot];
            ownerNamespace = declaredNamespace ?? element.GetDefaultNamespace();
            var resolved = ResolveType(ownerNamespace + ownerName, assembly);
            if (resolved is null && declaredNamespace is null)
            {
                // A custom control can use d:Control.Property while its CLR mapping
                // is on the element's prefix instead of the default namespace.
                resolved = ResolveType(element.Name.Namespace + ownerName, assembly);
                ownerNamespace = element.Name.Namespace;
            }
            if (resolved is null) return null;
            owner = resolved;
        }
        DependencyProperty? property = null;
        var wrapper = owner.IsAssignableFrom(type) ? owner.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) : null;
        if (wrapper?.SetMethod?.IsPublic == true && wrapper.GetIndexParameters().Length == 0
            && wrapper.DeclaringType?.GetField(propertyName + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetValue(null) is DependencyProperty instance
            && instance.PropertyType == wrapper.PropertyType) property = instance;
        if (property is null && dot >= 0
            && owner.GetField(propertyName + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) is DependencyProperty attached)
        {
            var setters = owner.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name == "Set" + propertyName && !method.IsGenericMethod
                && method.ReturnType == typeof(void) && method.GetParameters() is [{ ParameterType: var target }, { ParameterType: var value }]
                && target.IsAssignableFrom(type) && value == attached.PropertyType).Take(2).ToArray();
            if (setters.Length == 1) property = attached;
        }
        XName attributeName = dot >= 0 && ownerNamespace != element.GetDefaultNamespace() ? ownerNamespace + name : XName.Get(name);
        return property is null ? null : new(property, attributeName, ownerNamespace + (ownerName + "." + propertyName));
    }

    private static void RemoveRuntimeValue(XElement element, Type type, DependencyProperty property, Assembly? assembly)
    {
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration && attribute.Name.Namespace != Design).ToArray())
        {
            if (attribute.Name.Namespace != XNamespace.None && !attribute.Name.LocalName.Contains('.')) continue;
            if (ResolveDesignMember(element, type, attribute.Name.LocalName, assembly,
                attribute.Name.Namespace == XNamespace.None ? null : attribute.Name.Namespace)?.Property == property) attribute.Remove();
        }
        foreach (var child in element.Elements().Where(child => child.Name.Namespace != Design && child.Name.LocalName.Contains('.')).ToArray())
            if (ResolveDesignMember(element, type, child.Name.LocalName, assembly, child.Name.Namespace)?.Property == property) child.Remove();

        string? content = ContentPropertyName(type);
        bool textInlines = typeof(TextBlock).IsAssignableFrom(type) && property == TextBlock.TextProperty;
        bool itemsSource = typeof(ItemsControl).IsAssignableFrom(type) && property == ItemsControl.ItemsSourceProperty;
        if (content != property.Name && !textInlines && !itemsSource) return;
        if (textInlines)
            foreach (var child in element.Elements().Where(child => child.Name.Namespace != Design && child.Name.LocalName.EndsWith(".Inlines", StringComparison.Ordinal)).ToArray())
            {
                var owner = ResolveType(child.Name.Namespace + child.Name.LocalName[..^8], assembly);
                if (owner?.IsAssignableFrom(type) == true && owner.GetProperty("Inlines")?.DeclaringType == typeof(TextBlock)) child.Remove();
            }
        if (itemsSource)
            foreach (var child in element.Elements().Where(child => child.Name.Namespace != Design && child.Name.LocalName.EndsWith(".Items", StringComparison.Ordinal)).ToArray())
            {
                var owner = ResolveType(child.Name.Namespace + child.Name.LocalName[..^6], assembly);
                if (owner?.IsAssignableFrom(type) == true && owner.GetProperty("Items")?.DeclaringType == typeof(ItemsControl)) child.Remove();
            }
        if (content == property.Name || textInlines && content == "Inlines" || itemsSource && content == "Items")
            foreach (var node in element.Nodes().Where(node => node is XText || node is XElement child && child.Name.Namespace != Design && !child.Name.LocalName.Contains('.')).ToArray()) node.Remove();
    }

    private static string? ContentPropertyName(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            var attribute = current.CustomAttributes.FirstOrDefault(attribute => attribute.AttributeType.FullName == "System.Windows.Markup.ContentPropertyAttribute");
            if (attribute is not null) return attribute.ConstructorArguments.FirstOrDefault().Value as string;
        }
        return null;
    }

    private void DesignDiagnostic(XObject source, string message, string severity = "Warning")
    {
        if (Diagnostics.Count > 256) return;
        if (Diagnostics.Count == 256)
        {
            Diagnostics.Add(new PreviewDiagnostic("Additional design-time diagnostics were omitted after the diagnostic budget was reached.", "Warning"));
            return;
        }
        var line = (IXmlLineInfo)source;
        Diagnostics.Add(new PreviewDiagnostic(message, severity, line.HasLineInfo() ? line.LineNumber : null,
            line.HasLineInfo() ? line.LinePosition : null));
    }
}
