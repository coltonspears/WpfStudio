using System.Text;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using Element = WpfStudio.Core.Wpf.XamlPropertySourceDocument.Element;
using Attribute = WpfStudio.Core.Wpf.XamlPropertySourceDocument.Attribute;
using Part = WpfStudio.Core.Wpf.XamlPropertySourceDocument.Part;
using PartKind = WpfStudio.Core.Wpf.XamlPropertySourceDocument.PartKind;

namespace WpfStudio.Core.Wpf;

public sealed record XamlPropertyEditRequest(string Path, string Text, long Version, SourceLocation Element,
    long ElementSourceVersion, string ElementSourceHash, string Property, string? LiteralValue,
    bool ClearLocalValue = false, bool ReplaceExistingValue = false, string? OwnerType = null,
    string? OwnerAssembly = null, bool IsAttached = false, string? SourceAssembly = null, string? ContentProperty = null,
    bool SetNull = false);

public sealed record XamlPropertyEditResult(DocumentEdits? Edit, string Explanation, string? Error = null,
    bool ReplacesExpression = false, bool ReplacesObjectValue = false, bool AffectsTemplate = false)
{
    public bool Success => Edit is not null && Error is null;
}

/// <summary>
/// Prepares reviewable source edits from a verified preview snapshot and canonical property
/// identity. It does not instantiate XAML, infer CLR inheritance, or change an editor buffer.
/// </summary>
public static class XamlPropertyEditService
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string Language = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Design = "http://schemas.microsoft.com/expression/blend/2008";
    private const string Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private enum Match { No, Yes, Ambiguous }
    private sealed record Identity(string Member, string Owner, string Namespace, string Assembly, string? RequestedPrefix);

    public static XamlPropertyEditResult CreateEdit(XamlPropertyEditRequest request)
    {
        if (request is null) return Fail("An edit request is required.");
        if (request.Text is null || request.Element is null || string.IsNullOrWhiteSpace(request.Path)) return Fail("The exact source text and authored element location are required.");
        if (request.Version < 0 || request.Version != request.ElementSourceVersion) return Fail("The document version no longer matches the selected preview element. Refresh the preview.");
        var hash = DocumentStore.Hash(Encoding.UTF8.GetBytes(request.Text));
        if (string.IsNullOrWhiteSpace(request.ElementSourceHash) || !string.Equals(hash, request.ElementSourceHash, StringComparison.OrdinalIgnoreCase))
            return Fail("The document text no longer matches the selected preview element. Refresh the preview.");
        try
        {
            if (!string.Equals(System.IO.Path.GetFullPath(request.Path), System.IO.Path.GetFullPath(request.Element.Path), StringComparison.OrdinalIgnoreCase))
                return Fail("The selected element belongs to a different source file.");
            if (!TryIdentity(request, out var identity, out var identityError)) return Fail(identityError!);
            if (request.ClearLocalValue && request.SetNull) return Fail("Setting XAML null and removing the local value are different actions; choose only one.");
            if (!request.ClearLocalValue && !request.SetNull)
            {
                if (request.LiteralValue is null) return Fail("Supply a literal value or explicitly choose XAML null or removal of the local value.");
                XmlConvert.VerifyXmlChars(request.LiteralValue);
            }
            if (request.ContentProperty is not null) VerifyIdentifier(request.ContentProperty);
            var document = XamlPropertySourceDocument.Parse(request.Text);
            var selected = document.Elements.SingleOrDefault(e => e.Start == request.Element.Start);
            if (selected is null || selected.StartTagEnd - selected.Start != request.Element.Length)
                return Fail("The source span does not identify the complete authored element start tag. Refresh the preview.");
            var position = XamlPropertySourceDocument.Position(request.Text, selected.Start);
            if (position.Line != request.Element.Line || position.Column != request.Element.Column
                || request.Element.DisplayText is { Length: > 0 } label && label != selected.Xml.Name.LocalName && label != selected.Name)
                return Fail("The selected element's source name or position no longer matches. Refresh the preview.");
            if (selected.Xml.Name.LocalName.Contains('.')) return Fail("Select an authored object element, not a property-element declaration.");
            string? nullExpression = null;
            if (request.SetNull)
            {
                var prefix = InScopeNamespaces(selected.Xml).Where(pair => pair.Key.Length > 0 && pair.Value == Language)
                    .OrderBy(pair => pair.Key.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).FirstOrDefault();
                if (prefix is null) return Fail("No in-scope namespace alias identifies the XAML language namespace. Add one before setting XAML null.");
                nullExpression = "{" + prefix + ":Null}";
            }
            if (identity!.RequestedPrefix is { } requestedPrefix && OwnerMatches(selected.Xml.GetNamespaceOfPrefix(requestedPrefix)?.NamespaceName,
                    identity.Owner, identity, request.SourceAssembly) != Match.Yes)
                return Fail("The requested attached-property prefix does not identify its canonical CLR owner and assembly in this element's namespace scope.");
            var template = selected.Xml.AncestorsAndSelf().Any(e => e.Name.NamespaceName == Presentation
                && e.Name.LocalName is "DataTemplate" or "ControlTemplate" or "HierarchicalDataTemplate" or "ItemsPanelTemplate" or "FrameworkTemplate");
            var inlineText = IsTextBlockText(request, identity!);
            var targetsContent = !request.IsAttached && (request.ContentProperty == identity!.Member || inlineText);
            var preserveSpace = selected.Xml.AncestorsAndSelf().Select(e => e.Attribute(XNamespace.Xml + "space")?.Value).FirstOrDefault(v => v is not null) == "preserve";
            var content = selected.Parts.Where(p => p.Kind == PartKind.Element ? !p.Child!.Xml.Name.LocalName.Contains('.')
                : p.Kind == PartKind.CData || p.Kind == PartKind.Text && (preserveSpace || !request.Text.AsSpan(p.Start, p.End - p.Start).IsWhiteSpace())).ToArray();
            if (!request.IsAttached && request.ContentProperty is null && content.Length > 0)
                return Fail("The element has implicit content, but its content-property identity is unavailable. This edit cannot safely distinguish content from another local value.");

            var attributes = new List<Attribute>();
            var properties = new List<Element>();
            foreach (var attribute in selected.Attributes.Where(a => !a.Xml.IsNamespaceDeclaration))
            {
                if (!request.IsAttached && identity!.Member == "Name" && attribute.Xml.Name == XName.Get("Name", Language))
                    return Fail("x:Name is a XAML namescope directive. Use source rename instead of adding or replacing a Name property beside it.");
                var match = Matches(attribute.Name, selected.Xml, selected, identity!, request, inlineText);
                if (match == Match.Ambiguous) return Fail($"The existing '{attribute.Name}' declaration could refer to the same property through a different owner or namespace. Edit that declaration directly.");
                if (match == Match.Yes) attributes.Add(attribute);
            }
            foreach (var child in selected.Children.Where(c => c.Xml.Name.LocalName.Contains('.')))
            {
                var match = Matches(child.Name, child.Xml, selected, identity!, request, inlineText);
                if (match == Match.Ambiguous) return Fail($"The existing '{child.Name}' property element has an ambiguous owner or namespace. Edit that declaration directly.");
                if (match == Match.Yes) properties.Add(child);
            }
            if (attributes.Count + properties.Count + (targetsContent && content.Length > 0 ? 1 : 0) > 1)
                return Fail("The property has competing attribute, property-element, or implicit-content declarations. Resolve them in XAML before writing this value.");

            var replacesExpression = attributes.Any(a => IsExpression(a.Xml.Value)) || properties.Any(ContainsExpression)
                || targetsContent && content.Any(p => p.Child is { } child && ContainsExpression(child));
            var replacesObject = properties.Any(p => p.Children.Any()) || targetsContent && content.Any(p => p.Kind == PartKind.Element);
            if (!request.ClearLocalValue && !request.ReplaceExistingValue && (replacesExpression || replacesObject))
                return new XamlPropertyEditResult(null, "Replacing the authored expression or object value requires an explicit replacement action and a source diff.",
                    "Explicit replacement intent is required.", replacesExpression, replacesObject, template);

            var edits = new List<TextEdit>();
            string explanation;
            if (request.ClearLocalValue)
            {
                foreach (var attribute in attributes) edits.Add(new TextEdit(attribute.LeadingStart, attribute.End - attribute.LeadingStart, ""));
                foreach (var property in properties) edits.Add(RemoveElement(property, request.Text));
                if (targetsContent) RemoveContent(selected, content, request.Text, edits);
                explanation = edits.Count == 0 ? "No authored local value exists for this property. Styles, inherited values, and defaults are unchanged."
                    : "Remove the authored local value. Any style, inherited value, or default can become effective; this does not rewrite its declaration.";
            }
            else if (attributes.Count == 1)
            {
                var attribute = attributes[0];
                var existingLiteral = attribute.Xml.Value.StartsWith("{}", StringComparison.Ordinal) ? attribute.Xml.Value[2..] : attribute.Xml.Value;
                var alreadyNull = request.SetNull && IsNullExpression(attribute.Xml.Value, selected.Xml);
                if (!alreadyNull && (request.SetNull || replacesExpression || existingLiteral != request.LiteralValue))
                    edits.Add(new TextEdit(attribute.ValueStart, attribute.ValueEnd - attribute.ValueStart, nullExpression ?? EscapeLiteral(request.LiteralValue!, attribute.Quote)));
                explanation = edits.Count == 0
                    ? request.SetNull ? "The authored attribute already sets XAML null; its namespace alias and spelling are preserved."
                        : "The authored attribute already has this literal value; its existing spelling and entities are preserved."
                    : request.SetNull ? "Replace the authored local attribute with XAML null. This preserves a local assignment and does not remove it."
                        : "Replace the authored local attribute with the supplied literal value.";
            }
            else
            {
                var name = InsertionName(selected, identity!, request);
                if (name is null) return Fail("No in-scope XAML namespace alias proves the attached property's CLR owner and assembly. Add an explicit matching namespace declaration before writing it.");
                var quote = PreferredQuote(selected);
                var insertion = Insertion(selected, request.Text);
                edits.Add(new TextEdit(insertion.Start, 0, insertion.Separator + name + "=" + quote + (nullExpression ?? EscapeLiteral(request.LiteralValue!, quote)) + quote));
                foreach (var property in properties) edits.Add(RemoveElement(property, request.Text));
                if (targetsContent) RemoveContent(selected, content, request.Text, edits);
                var valueKind = request.SetNull ? "XAML null" : "literal";
                explanation = properties.Count > 0 ? $"Replace the property-element value with a local {valueKind} attribute; preserve unrelated property declarations and comments."
                    : targetsContent && content.Length > 0 ? $"Replace the authored implicit content with a local {valueKind} attribute; preserve unrelated property elements and comments."
                    : $"Add a local {valueKind} override on this authored element. Existing style, inherited, and default declarations are unchanged.";
            }
            if (replacesExpression && edits.Count > 0) explanation += " The previous authored binding, resource or other expression is removed.";
            if (replacesObject && edits.Count > 0) explanation += " The existing object value or content subtree is removed.";
            if (template) explanation += " This edits the shared template declaration and affects every instance created from it.";
            var ordered = edits.OrderBy(e => e.Start).ToArray();
            var after = WorkspaceEditTransaction.ApplyTextEdits(request.Text, ordered);
            _ = XamlPropertySourceDocument.Parse(after);
            return new XamlPropertyEditResult(new DocumentEdits(request.Path, request.Version, ordered, hash), explanation,
                ReplacesExpression: replacesExpression, ReplacesObjectValue: replacesObject, AffectsTemplate: template);
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or InvalidOperationException or NotSupportedException or System.IO.PathTooLongException)
        {
            return Fail("The source edit could not be prepared safely: " + exception.Message);
        }
    }

    private static XamlPropertyEditResult Fail(string message) => new(null, message, message);
    private static bool TryIdentity(XamlPropertyEditRequest request, out Identity? identity, out string? error)
    {
        identity = null; error = null;
        if (string.IsNullOrWhiteSpace(request.OwnerType) || string.IsNullOrWhiteSpace(request.OwnerAssembly))
        { error = "Canonical CLR property owner and assembly metadata are required; a display name alone is ambiguous."; return false; }
        var dot = request.OwnerType.LastIndexOf('.');
        var owner = request.OwnerType[(dot + 1)..];
        var ns = dot < 0 ? "" : request.OwnerType[..dot];
        VerifyIdentifier(owner);
        if (ns.Length > 0 && ns.Split('.').Any(part => !ValidIdentifier(part))
            || request.OwnerAssembly.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.')))
        { error = "The CLR property owner identity cannot be represented unambiguously in XAML."; return false; }
        if (!SplitProperty(request.Property, out var prefix, out var namedOwner, out var member))
        { error = "Use a property name or a single owner-qualified attached property name."; return false; }
        if (namedOwner is not null && namedOwner != owner)
        { error = "The property display identifier does not match its canonical CLR owner."; return false; }
        if (!request.IsAttached && (prefix is not null || namedOwner is not null))
        { error = "A verified normal CLR property must be requested by its bare member name."; return false; }
        if (!request.IsAttached && member == "xmlns")
        { error = "Namespace declarations cannot be edited as CLR property values."; return false; }
        identity = new Identity(member!, owner, ns, request.OwnerAssembly, prefix);
        return true;
    }
    private static bool SplitProperty(string name, out string? prefix, out string? owner, out string? member)
    {
        prefix = null; owner = null; member = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            if (colon != name.LastIndexOf(':') || !ValidIdentifier(name[..colon])) return false;
            prefix = name[..colon]; name = name[(colon + 1)..];
        }
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            if (dot != name.LastIndexOf('.') || !ValidIdentifier(name[..dot])) return false;
            owner = name[..dot]; name = name[(dot + 1)..];
        }
        if (!ValidIdentifier(name)) return false;
        member = name; return true;
    }
    private static bool ValidIdentifier(string value)
    {
        try { VerifyIdentifier(value); return true; }
        catch (XmlException) { return false; }
    }
    private static void VerifyIdentifier(string value)
    {
        XmlConvert.VerifyNCName(value);
        if (value.Contains('.') || value.Contains(':')) throw new XmlException("Property and owner names must be individual identifiers.");
    }

    private static Match Matches(string name, XElement scope, Element selected, Identity identity, XamlPropertyEditRequest request, bool inlineText)
    {
        if (!SplitProperty(name, out var prefix, out var owner, out var member))
            return name.EndsWith("." + identity.Member, StringComparison.Ordinal) ? Match.Ambiguous : Match.No;
        if (member != identity.Member && !(inlineText && member == "Inlines")) return Match.No;
        var uri = prefix is null ? scope.GetDefaultNamespace().NamespaceName : scope.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        if (uri is Design or Compatibility or Language || prefix == "xml") return Match.No;
        if (owner is null)
        {
            if (prefix is not null) return Match.Ambiguous;
            return request.IsAttached ? Match.Ambiguous : Match.Yes;
        }
        // A property element qualified by the selected object's own XAML type uses that
        // object's verified CLR wrapper, including inherited/AddOwner properties.
        if (!request.IsAttached && owner == selected.Xml.Name.LocalName && uri == selected.Xml.Name.NamespaceName) return Match.Yes;
        var ownerMatch = OwnerMatches(uri, owner, identity, request.SourceAssembly);
        if (ownerMatch == Match.Yes) return Match.Yes;
        // A different owner (including an attached provider) may expose the same DP via
        // AddOwner. Canonical owner metadata cannot prove distinct DP registrations.
        // Without that schema proof, never introduce a potentially duplicate local value.
        return Match.Ambiguous;
    }
    private static Match OwnerMatches(string? uri, string owner, Identity identity, string? sourceAssembly)
    {
        if (uri == Presentation)
        {
            if (owner != identity.Owner) return Match.No;
            return IsKnownWpfOwner(identity) ? Match.Yes : Match.No;
        }
        if (uri is null || !uri.StartsWith("clr-namespace:", StringComparison.Ordinal)) return Match.Ambiguous;
        var parts = uri["clr-namespace:".Length..].Split(';');
        var assemblies = parts.Skip(1).Where(p => p.StartsWith("assembly=", StringComparison.Ordinal)).ToArray();
        if (parts.Skip(1).Any(p => !p.StartsWith("assembly=", StringComparison.Ordinal)) || assemblies.Length > 1) return Match.Ambiguous;
        var assembly = assemblies.Length == 1 ? assemblies[0]["assembly=".Length..] : sourceAssembly;
        if (string.IsNullOrWhiteSpace(assembly)) return Match.Ambiguous;
        return owner == identity.Owner && parts[0] == identity.Namespace && string.Equals(assembly, identity.Assembly, StringComparison.OrdinalIgnoreCase) ? Match.Yes : Match.No;
    }
    private static bool IsKnownWpfOwner(Identity identity) => identity.Assembly is "PresentationFramework" or "PresentationCore" or "WindowsBase"
        && identity.Namespace is "System.Windows" or "System.Windows.Controls" or "System.Windows.Controls.Primitives" or "System.Windows.Documents"
            or "System.Windows.Input" or "System.Windows.Media" or "System.Windows.Media.Animation" or "System.Windows.Media.Effects"
            or "System.Windows.Media.Imaging" or "System.Windows.Media.Media3D" or "System.Windows.Navigation" or "System.Windows.Shapes"
            or "System.Windows.Shell" or "System.Windows.Automation";
    private static string? InsertionName(Element selected, Identity identity, XamlPropertyEditRequest request)
    {
        if (!request.IsAttached) return identity.Member;
        var namespaces = InScopeNamespaces(selected.Xml);
        if (identity.RequestedPrefix is not null)
            return namespaces.TryGetValue(identity.RequestedPrefix, out var uri) && OwnerMatches(uri, identity.Owner, identity, request.SourceAssembly) == Match.Yes
                ? identity.RequestedPrefix + ":" + identity.Owner + "." + identity.Member : null;
        var match = namespaces.Where(pair => OwnerMatches(pair.Value, identity.Owner, identity, request.SourceAssembly) == Match.Yes)
            .OrderBy(pair => pair.Key.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).FirstOrDefault();
        return match is null ? null : (match.Length == 0 ? "" : match + ":") + identity.Owner + "." + identity.Member;
    }
    private static Dictionary<string, string> InScopeNamespaces(XElement element)
    {
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ancestor in element.AncestorsAndSelf())
        foreach (var declaration in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = declaration.Name.LocalName == "xmlns" ? "" : declaration.Name.LocalName;
            namespaces.TryAdd(prefix, declaration.Value);
        }
        return namespaces;
    }
    private static bool IsNullExpression(string value, XElement scope)
    {
        value = value.Trim();
        if (!value.StartsWith('{') || !value.EndsWith(":Null}", StringComparison.Ordinal)) return false;
        var prefix = value[1..^6];
        return ValidIdentifier(prefix) && scope.GetNamespaceOfPrefix(prefix)?.NamespaceName == Language;
    }
    private static bool IsTextBlockText(XamlPropertyEditRequest request, Identity identity) => !request.IsAttached
        && request.OwnerType == "System.Windows.Controls.TextBlock" && request.OwnerAssembly == "PresentationFramework"
        && identity.Member == "Text" && request.ContentProperty == "Inlines";
    private static bool IsExpression(string value) => value.TrimStart().StartsWith('{') && !value.TrimStart().StartsWith("{}", StringComparison.Ordinal);
    private static bool ContainsExpression(Element element) => element.Xml.DescendantsAndSelf().Any(e =>
        e.Name.NamespaceName == Presentation && e.Name.LocalName is "Binding" or "MultiBinding" or "PriorityBinding" or "StaticResource" or "DynamicResource"
        || e.Name.NamespaceName == Language && e.Name.LocalName is "Static" or "Reference"
        || e.Attributes().Where(a => !a.IsNamespaceDeclaration).Any(a => IsExpression(a.Value)));
    private static TextEdit RemoveElement(Element element, string text) => new(element.Start, element.End - element.Start, PreservedComments(element, text));
    private static string PreservedComments(Element element, string text)
    {
        var parts = new List<Part>();
        var pending = new Stack<Element>(); pending.Push(element);
        while (pending.TryPop(out var current))
        {
            parts.AddRange(current.Parts.Where(p => p.Kind is PartKind.Comment or PartKind.Instruction));
            foreach (var child in current.Children) pending.Push(child);
        }
        return string.Concat(parts.OrderBy(p => p.Start).Select(p => text[p.Start..p.End]));
    }
    private static void RemoveContent(Element selected, IReadOnlyList<Part> content, string text, List<TextEdit> edits)
    {
        // xml:space makes otherwise blank text a real local value; it must also be cleared.
        var preserve = selected.Xml.AncestorsAndSelf().Select(e => e.Attribute(XNamespace.Xml + "space")?.Value).FirstOrDefault(v => v is not null) == "preserve";
        var owned = preserve ? selected.Parts.Where(p => p.Kind is PartKind.Text or PartKind.CData || content.Contains(p)) : content;
        foreach (var part in owned)
            edits.Add(part.Child is { } child ? RemoveElement(child, text) : new TextEdit(part.Start, part.ActualEnd - part.Start, ""));
    }
    private static char PreferredQuote(Element element)
    {
        for (var current = element; current is not null; current = current.Parent)
            if (current.Attributes.LastOrDefault() is { } attribute) return attribute.Quote;
        return '"';
    }
    private static (int Start, string Separator) Insertion(Element element, string text)
    {
        var start = element.Attributes.LastOrDefault()?.End ?? element.NameEnd;
        var multiline = element.Attributes.LastOrDefault(a => text.AsSpan(a.LeadingStart, a.Start - a.LeadingStart).ContainsAny('\r', '\n'));
        if (multiline is not null)
        {
            var trivia = text[multiline.LeadingStart..multiline.Start];
            var lastNewline = trivia.LastIndexOfAny(['\r', '\n']);
            var newlineStart = lastNewline > 0 && trivia[lastNewline] == '\n' && trivia[lastNewline - 1] == '\r' ? lastNewline - 1 : lastNewline;
            return (start, trivia[newlineStart..]);
        }
        return (start, " ");
    }
    private static string EscapeLiteral(string value, char quote)
    {
        var result = new StringBuilder();
        if (value.StartsWith('{')) result.Append("{}");
        foreach (var c in value)
            result.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '\r' => "&#xD;", '\n' => "&#xA;", '\t' => "&#x9;", '"' when quote == '"' => "&quot;", '\'' when quote == '\'' => "&apos;", _ => c.ToString() });
        return result.ToString();
    }
}
