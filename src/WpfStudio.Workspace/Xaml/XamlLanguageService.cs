using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

/// <summary>
/// Resolves declared XAML binding sources against the worker's compilation. It never creates
/// controls, loads user assemblies into the editor, or assumes an unknown runtime DataContext.
/// </summary>
public sealed partial class XamlLanguageService
{
    public IReadOnlyList<WorkspaceDiagnostic> Analyze(string path, string text, long version, Compilation compilation, CancellationToken token = default, XamlResourceContext? resources = null)
        => AnalyzeBounded(path, text, version, compilation, int.MaxValue, token, resources);

    internal IReadOnlyList<WorkspaceDiagnostic> AnalyzeBounded(string path, string text, long version, Compilation compilation,
        int maximumDiagnostics, CancellationToken token, XamlResourceContext? resources = null)
    {
        token.ThrowIfCancellationRequested();
        // An unfinished XML document is normal during editing. Syntax errors belong to the XML
        // service; avoid drawing semantic conclusions from a potentially different element tree.
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            _ = XDocument.Load(reader);
        }
        catch (XmlException) { return []; }

        var resolver = new SourceResolver(compilation, token, resources);
        var diagnostics = new List<WorkspaceDiagnostic>();
        foreach (var element in Read(text, token))
        foreach (var attribute in element.Attributes)
        {
            token.ThrowIfCancellationRequested();
            if (IgnoredBindingAttribute(attribute)) continue;
            var binding = GetBinding(attribute);
            if (binding is null || !binding.Complete || !TrySegments(binding.Path, out var segments)) continue;
            foreach (var step in resolver.Walk(attribute, binding, segments))
            {
                if (!step.IsMissing) continue;
                var span = attribute.Value.Span(binding.Start + step.Segment.Start, step.Segment.Length);
                var before = text.AsSpan(0, span.Start);
                diagnostics.Add(new WorkspaceDiagnostic("XAMLBIND001", MissingMessage(step), "Warning", path,
                    before.Count('\n') + 1, span.Start - before.LastIndexOf('\n'), span.Start, span.Length));
                if (diagnostics.Count >= maximumDiagnostics) return diagnostics;
            }
        }
        return diagnostics;
    }

    public CompletionResult? Complete(string text, int position, long version, Compilation compilation, CancellationToken token = default, XamlResourceContext? resources = null)
    {
        position = Math.Clamp(position, 0, text.Length);
        var attribute = Read(text, token).SelectMany(e => e.Attributes)
            .FirstOrDefault(a => position >= a.ValueStart && position <= a.ValueEnd);
        if (attribute is null) return null;
        var binding = GetBinding(attribute);
        if (binding is null) return null;
        if (IgnoredBindingAttribute(attribute)) return new CompletionResult(version, position, 0, []);
        var caret = attribute.Value.PositionAt(position);
        if (caret < binding.Start || caret > binding.Start + binding.Path.Length) return null;
        if (!CompletionMember(binding.Path, caret - binding.Start, out var memberStart, out var memberEnd, out var qualified))
            return new CompletionResult(version, position, 0, []);
        var fragment = binding.Path[memberStart..(caret - binding.Start)];
        var segment = binding.Path[memberStart..memberEnd];
        // Match the prefix before the caret, but replace the complete current member. The
        // decoded-to-source mapping also consumes any entity-encoded suffix without altering
        // following path segments or binding options.
        var span = attribute.Value.Span(binding.Start + memberStart, segment.Length);
        CompletionResult Empty() => new(version, span.Start, span.Length, []);
        var resolver = new SourceResolver(compilation, token, resources);
        var source = resolver.BindingSource(attribute, binding);
        int prefixEnd = qualified?.PrefixEnd ?? memberStart;
        if (!TrySegments(binding.Path[..prefixEnd].TrimEnd().TrimEnd('.'), out var prefixSegments)) return Empty();
        source = resolver.Follow(source, prefixSegments, attribute.Owner);
        if (prefixSegments.Count > 0 && source.Type is null) return Empty();
        if (qualified is { OwnerPosition: true })
        {
            int colon = fragment.IndexOf(':');
            string prefix = colon < 0 ? "" : fragment[..colon], typeFragment = colon < 0 ? fragment : fragment[(colon + 1)..];
            if (colon >= 0 && !IsIdentifier(prefix, false) || !IsIdentifier(typeFragment, true)
                || segment.Length > 0 && !IsTypeName(segment) && !segment.EndsWith(':')) return Empty();
            string spelling = colon < 0 ? "" : prefix + ":";
            var types = resolver.QualifiedOwnerTypes(attribute.Owner, prefix).Take(4096)
                .Where(type => type.Name.StartsWith(typeFragment, StringComparison.OrdinalIgnoreCase))
                .Select(type => new CompletionEntry(spelling + type.Name, spelling + type.Name, spelling + type.Name,
                    type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), ["Class"]));
            var ordinary = colon < 0 && qualified.Owner is null
                ? CompletionProperties(source.Type).Where(property => property.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
                    .Select(property => new CompletionEntry(property.Name, property.Name, property.Name,
                        property.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), ["Property"])) : [];
            return new(version, span.Start, span.Length, types.Concat(ordinary).OrderBy(item => item.DisplayText, StringComparer.Ordinal).Take(250).ToArray());
        }
        if (!IsIdentifier(fragment, allowEmpty: true) || !IsIdentifier(segment, allowEmpty: true)) return Empty();
        if (qualified is { Owner: { } ownerName })
        {
            var qualifiedItems = resolver.QualifiedMembers(attribute.Owner, ownerName, source.Type)
                .Where(member => member.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
                .OrderBy(member => member.Name, StringComparer.Ordinal).Take(250)
                .Select(member => new CompletionEntry(member.Name, member.Name, member.Name,
                    $"{member.ValueType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {ownerName}.{member.Name}", ["Property"])).ToArray();
            return new(version, span.Start, span.Length, qualifiedItems);
        }
        var items = CompletionProperties(source.Type).Where(p => p.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.Ordinal).Take(250)
            .Select(p => new CompletionEntry(p.Name, p.Name, p.Name,
                $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.ContainingType.Name}.{p.Name}", ["Property"]))
            .ToArray();
        return new CompletionResult(version, span.Start, span.Length, items);
    }

    private sealed record BindingPath(string Path, int Start, bool Complete, bool HasExplicitSource, Element ContextOwner, Extension? Extension = null);

    private static bool IgnoredBindingAttribute(XamlSyntax.Attribute attribute) =>
        XamlSchemaService.IgnoredElement(attribute.Owner) || XamlSchemaService.IgnoredAttribute(attribute);

    private static BindingPath? GetBinding(XamlSyntax.Attribute attribute)
    {
        var owner = attribute.Owner;
        if (IsPresentationElement(owner, "Binding") && attribute.Name == "Path")
        {
            var explicitSource = owner.Attributes.Any(a => a.Name is "Source" or "ElementName" or "RelativeSource" or "XPath")
                || owner.Children.Any(c => c.LocalName is "Binding.Source" or "Binding.RelativeSource");
            // A Binding object typically sits beneath the target's property element.
            var target = owner.Parent;
            while (target is not null && (target.LocalName.Contains('.') || IsPresentationElement(target, "MultiBinding") || IsPresentationElement(target, "PriorityBinding"))) target = target.Parent;
            return new BindingPath(attribute.Value.Text, 0, true, explicitSource, target ?? owner);
        }
        var extension = ParseExtension(attribute.Value.Text);
        if (extension is null || !IsExtension(owner, extension, Presentation, "Binding")) return null;
        var path = extension.Argument("Path") ?? extension.Positional;
        var explicitBindingSource = extension.Arguments.Any(a => a.Name is "Source" or "ElementName" or "RelativeSource" or "XPath");
        if (path is not null)
        {
            string pathValue = path.Value;
            int pathStart = path.Start;
            if (pathValue.Length > 0 && pathValue[0] is '\'' or '"')
            {
                char quote = pathValue[0];
                bool closed = pathValue.Length > 1 && pathValue[^1] == quote;
                pathValue = closed ? pathValue[1..^1] : pathValue[1..];
                pathStart++;
                return new BindingPath(pathValue, pathStart, extension.IsComplete && closed, explicitBindingSource, owner, extension);
            }
            return new BindingPath(pathValue, pathStart, extension.IsComplete, explicitBindingSource, owner, extension);
        }

        // Empty positional bindings still need a completion context at `{Binding |}`. Do not
        // interpret positions in other options (for example Mode=) as a property path.
        var value = attribute.Value.Text;
        var open = value.IndexOf('{');
        var start = open + 1 + extension.Name.Length;
        while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
        return new BindingPath("", start, extension.IsComplete, explicitBindingSource, owner, extension);
    }

    private static bool IsIdentifier(string name, bool allowEmpty)
    {
        if (name.Length == 0) return allowEmpty;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_' || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.ConnectorPunctuation);
    }
    private static bool IsPresentationElement(Element element, string name) => element.LocalName == name && (element.Namespace is null || element.Namespace == Presentation);

    private static INamedTypeSymbol? Normalize(ITypeSymbol? symbol)
    {
        if (symbol is not INamedTypeSymbol type || type.TypeKind == TypeKind.Error || type.SpecialType == SpecialType.System_Object) return null;
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return Normalize(type.TypeArguments[0]);
        for (var current = type; current is not null; current = current.BaseType)
            if (current.TypeKind == TypeKind.Error) return null;
        if (type.AllInterfaces.Any(i => i.ToDisplayString() is "System.ComponentModel.ICustomTypeDescriptor" or "System.Dynamic.IDynamicMetaObjectProvider" or "System.Reflection.ICustomTypeProvider")) return null;
        if (type.ToDisplayString() is "System.ComponentModel.ICustomTypeDescriptor" or "System.Dynamic.IDynamicMetaObjectProvider" or "System.Reflection.ICustomTypeProvider") return null;
        return type;
    }
    private static bool IsCollectionSource(ITypeSymbol symbol) => symbol is INamedTypeSymbol type
        && type.SpecialType != SpecialType.System_String
        && (type.AllInterfaces.Any(i => i.ToDisplayString() == "System.Collections.IEnumerable") || type.ToDisplayString() == "System.Collections.IEnumerable");

    private static bool HasUnavailableGeneratedMembers(ITypeSymbol symbol)
    {
        if (symbol is not INamedTypeSymbol type) return false;
        for (var current = type; current is not null; current = current.BaseType)
        foreach (var member in current.GetMembers())
        foreach (var attribute in member.GetAttributes())
        {
            var attributeName = attribute.AttributeClass?.ToDisplayString();
            string? expected = null;
            if (member is IFieldSymbol && attributeName == "CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute")
            {
                var name = member.Name.StartsWith("m_", StringComparison.Ordinal) ? member.Name[2..] : member.Name.TrimStart('_');
                if (name.Length > 0) expected = char.ToUpperInvariant(name[0]) + name[1..];
            }
            else if (member is IMethodSymbol && attributeName == "CommunityToolkit.Mvvm.Input.RelayCommandAttribute")
            {
                var name = member.Name;
                if (name.StartsWith("On", StringComparison.Ordinal) && name.Length > 2 && char.IsUpper(name[2])) name = name[2..];
                if (name.EndsWith("Async", StringComparison.Ordinal)) name = name[..^5];
                expected = name + "Command";
            }
            // This is only a completeness guard. Suggested/generated properties always come
            // from the compilation; an attribute never fabricates a bindable symbol.
            if (expected is not null && !current.GetMembers(expected).OfType<IPropertySymbol>().Any()) return true;
        }
        return false;
    }
    private static IEnumerable<IPropertySymbol> Properties(ITypeSymbol symbol)
    {
        var type = Normalize(symbol);
        if (type is null) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var types = new List<INamedTypeSymbol>();
        for (var current = type; current is not null; current = current.BaseType) types.Add(current);
        if (type.TypeKind == TypeKind.Interface) types.AddRange(type.AllInterfaces);
        foreach (var current in types)
        foreach (var member in current.GetMembers())
        {
            if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod.DeclaredAccessibility: Accessibility.Public } property && seen.Add(property.Name))
                yield return property;
        }
    }
    private static string? Suggest(string name, IEnumerable<string> candidates)
    {
        if (name.Length > 128) return null;
        // Adjacent transpositions are common in XAML names (Nmae -> Name).
        var limit = name.Length <= 3 ? 1 : 2;
        return candidates.Where(candidate => candidate.Length <= 128).Distinct(StringComparer.Ordinal).Select(candidate => (Name: candidate, Distance: Distance(name, candidate)))
            .Where(c => c.Distance <= limit).OrderBy(c => c.Distance).ThenBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name).FirstOrDefault();
    }
    private static int Distance(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 2) return 3;
        var matrix = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) matrix[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) matrix[0, j] = j;
        for (var i = 1; i <= left.Length; i++)
        for (var j = 1; j <= right.Length; j++)
        {
            var a = char.ToUpperInvariant(left[i - 1]); var b = char.ToUpperInvariant(right[j - 1]);
            matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + (a == b ? 0 : 1));
            if (i > 1 && j > 1 && a == char.ToUpperInvariant(right[j - 2]) && char.ToUpperInvariant(left[i - 2]) == b)
                matrix[i, j] = Math.Min(matrix[i, j], matrix[i - 2, j - 2] + 1);
        }
        return matrix[left.Length, right.Length];
    }

}
