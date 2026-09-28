using System.Xml;
using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

/// <summary>Symbol-based XAML schema assistance. Project markup is parsed, never instantiated.</summary>
public sealed class XamlSchemaService
{
    private const string Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    public IReadOnlyList<WorkspaceDiagnostic> Analyze(string path, string text, long version, Compilation compilation, CancellationToken token = default)
        => AnalyzeBounded(path, text, version, compilation, int.MaxValue, token);

    internal IReadOnlyList<WorkspaceDiagnostic> AnalyzeBounded(string path, string text, long version, Compilation compilation,
        int maximumDiagnostics, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var issues = new List<WorkspaceDiagnostic>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read()) token.ThrowIfCancellationRequested();
        }
        catch (XmlException exception)
        {
            var offset = Offset(text, exception.LineNumber, exception.LinePosition);
            issues.Add(Diagnostic(path, text, "XAMLSYNTAX001", exception.Message, offset, Math.Min(1, text.Length - offset), "Error"));
            return issues;
        }

        var resolver = new SchemaTypeResolver(compilation, token);
        foreach (var element in Read(text, token))
        {
            token.ThrowIfCancellationRequested();
            if (issues.Count >= maximumDiagnostics) return issues;
            if (IgnoredElement(element)) continue;
            if (element.LocalName.Contains('.'))
            {
                AnalyzePropertyElement(path, text, element, resolver, issues);
                continue;
            }
            var type = resolver.Resolve(element, element.Name);
            if (type is null)
            {
                if (element.Namespace is { } ns && ns != Language && resolver.IsKnownNamespace(ns))
                    issues.Add(Diagnostic(path, text, "XAMLSCHEMA001", $"Type '{element.Name}' could not be found in the declared XAML namespace.", element.NameStart, element.NameLength));
                continue;
            }
            if (!SchemaMembers.IsComplete(type)) continue;
            foreach (var attribute in element.Attributes)
            {
                if (issues.Count >= maximumDiagnostics) return issues;
                if (IgnoredAttribute(attribute)) continue;
                var member = Member(element, attribute.Name, type, resolver);
                if (member is null)
                {
                    // A foreign namespace may supply a custom directive unknown to this schema.
                    var name = SplitName(attribute.Name);
                    if (name.Prefix.Length > 0 && !name.Local.Contains('.')) continue;
                    var suggestion = Nearest(attribute.Name, SchemaMembers.Instance(type).Select(m => m.Name));
                    var message = $"Property or event '{attribute.Name}' was not found on '{type.Name}'.";
                    if (suggestion is not null) message += $" Did you mean '{suggestion}'?";
                    issues.Add(Diagnostic(path, text, "XAMLSCHEMA002", message, attribute.NameStart, attribute.NameLength));
                }
                else if (!member.CanWrite)
                    issues.Add(Diagnostic(path, text, "XAMLSCHEMA003", $"Property '{attribute.Name}' is read-only and cannot be assigned with an attribute.", attribute.NameStart, attribute.NameLength));
            }
        }
        return issues;
    }

    public CompletionResult? Complete(string text, int position, long version, Compilation compilation, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        position = Math.Clamp(position, 0, text.Length);
        var elements = Read(text, token);
        var resolver = new SchemaTypeResolver(compilation, token);
        var attribute = elements.SelectMany(element => element.Attributes).FirstOrDefault(a => position >= a.ValueStart && position <= a.ValueEnd);
        if (attribute is not null)
        {
            if (attribute.Name == "xmlns" || attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal))
            {
                var typed = attribute.Value.Text[..attribute.Value.PositionAt(position)];
                return Result(version, attribute.ValueStart, attribute.ValueEnd - attribute.ValueStart,
                    resolver.NamespaceUris().Where(uri => uri.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Select(uri => Entry(uri, "XAML namespace", "Namespace")));
            }
            if (IgnoredAttribute(attribute)) return null;
            if (attribute.Value.Text.TrimStart().StartsWith('{')) return MarkupCompletion(text, position, version, attribute, resolver);
            var owner = resolver.Resolve(attribute.Owner, attribute.Owner.Name);
            if (owner is null) return null;
            var member = Member(attribute.Owner, attribute.Name, owner, resolver);
            var type = member?.ValueType;
            if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) type = nullable.TypeArguments[0];
            IEnumerable<string>? values = type?.SpecialType == SpecialType.System_Boolean ? ["True", "False"]
                : type?.TypeKind == TypeKind.Enum ? type.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue).Select(field => field.Name) : null;
            if (values is null) return null;
            var prefix = attribute.Value.Text[..attribute.Value.PositionAt(position)];
            return Result(version, attribute.ValueStart, attribute.ValueEnd - attribute.ValueStart,
                values.Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(value => Entry(value, type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), "EnumMember")));
        }

        var start = position;
        while (start > 0 && NameCharacter(text[start - 1])) start--;
        var end = position;
        while (end < text.Length && NameCharacter(text[end])) end++;
        var fragment = text[start..position];
        var element = elements.LastOrDefault(candidate => position >= candidate.NameStart
            && (position < candidate.StartTagEnd || position == candidate.StartTagEnd && (position == 0 || text[position - 1] != '>')));
        if (start > 0 && text[start - 1] == '<')
        {
            if (element is null || element.NameStart != start)
                element = Read(text.Insert(position, "SchemaPlaceholder"), token).FirstOrDefault(candidate => candidate.NameStart == start);
            if (element is null || IgnoredElement(element)) return null;
            return TypeCompletion(element, resolver, fragment, start, end - start, version);
        }
        if (element is null || position <= element.NameStart + element.NameLength || IgnoredElement(element)) return null;
        if (position < text.Length && text[position] == '>' && start == position && position > 0 && text[position - 1] == '/') return null;
        var typeOwner = resolver.Resolve(element, element.Name);
        if (typeOwner is null) return null;
        var dot = fragment.LastIndexOf('.');
        IEnumerable<CompletionEntry> items;
        if (dot >= 0)
        {
            var qualifiedOwner = fragment[..dot];
            var attachedOwner = resolver.Resolve(element, qualifiedOwner);
            items = attachedOwner is null ? [] : SchemaMembers.Attached(attachedOwner).Where(member => member.CanWrite).Select(member => MemberEntry(qualifiedOwner + "." + member.Name, member));
        }
        else
        {
            items = SchemaMembers.Instance(typeOwner).Where(member => member.CanWrite).Select(member => MemberEntry(member.Name, member));
            var languagePrefix = Prefixes(element).FirstOrDefault(mapping => mapping.Uri == Language).Prefix;
            if (languagePrefix is not null)
                items = items.Concat(new[] { "Name", "Key", "Uid" }.Select(name => Entry(languagePrefix + ":" + name, "XAML directive", "Property")));
        }
        var alreadyPresent = element.Attributes.Where(a => position < a.NameStart || position > a.NameStart + a.NameLength).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        return Result(version, start, end - start, items.Where(item => item.DisplayText.StartsWith(fragment, StringComparison.OrdinalIgnoreCase) && !alreadyPresent.Contains(item.DisplayText)));
    }

    public IReadOnlyList<SourceLocation> GetDefinition(string path, string text, int position, Compilation compilation, CancellationToken token = default)
    {
        var target = SymbolAt(text, position, compilation, token);
        return target.Symbol?.Locations.Where(location => location.IsInSource && location.SourceTree is not null).Select(location =>
        {
            var line = location.GetLineSpan();
            return new SourceLocation(location.SourceTree!.FilePath, location.SourceSpan.Start, location.SourceSpan.Length,
                line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1, target.Symbol.Name);
        }).ToArray() ?? [];
    }

    public XamlHoverInfo? GetHover(string path, string text, int position, Compilation compilation, CancellationToken token = default)
    {
        var target = SymbolAt(text, position, compilation, token);
        return target.Symbol is null ? null : new(target.Start, target.Length, SchemaMembers.Describe(target.Symbol));
    }

    private static (ISymbol? Symbol, int Start, int Length) SymbolAt(string text, int position, Compilation compilation, CancellationToken token)
    {
        var resolver = new SchemaTypeResolver(compilation, token);
        foreach (var element in Read(text, token))
        {
            if (IgnoredElement(element)) continue;
            var type = resolver.Resolve(element, element.Name);
            if (position >= element.NameStart && position <= element.NameStart + element.NameLength)
            {
                if (element.LocalName.Contains('.'))
                {
                    var dot = element.Name.LastIndexOf('.');
                    var owner = resolver.Resolve(element, element.Name[..dot]);
                    var member = owner is null ? null : SchemaMembers.Find(owner, element.Name[(dot + 1)..], attached: true);
                    return position <= element.NameStart + dot ? (owner, element.NameStart, dot) : (member?.Symbol, element.NameStart + dot + 1, element.NameLength - dot - 1);
                }
                return (type, element.NameStart, element.NameLength);
            }
            if (type is null) continue;
            foreach (var attribute in element.Attributes)
            {
                if (IgnoredAttribute(attribute)) continue;
                if (position >= attribute.NameStart && position <= attribute.NameStart + attribute.NameLength)
                    return (Member(element, attribute.Name, type, resolver)?.Symbol, attribute.NameStart, attribute.NameLength);
            }
        }
        return (null, 0, 0);
    }

    private static void AnalyzePropertyElement(string path, string text, Element element, SchemaTypeResolver resolver, List<WorkspaceDiagnostic> issues)
    {
        var dot = element.Name.LastIndexOf('.');
        var owner = resolver.Resolve(element, element.Name[..dot]);
        if (owner is null || !SchemaMembers.IsComplete(owner)) return;
        var member = SchemaMembers.Find(owner, element.Name[(dot + 1)..], attached: true);
        if (member is null || member.IsEvent)
            issues.Add(Diagnostic(path, text, "XAMLSCHEMA002", $"Property '{element.Name[(dot + 1)..]}' was not found on '{owner.Name}'.", element.NameStart + dot + 1, element.NameLength - dot - 1));
        else if (!member.IsAttached && element.Parent is { } parent && resolver.Resolve(parent, parent.Name) is { } parentType && !SchemaMembers.DerivesFrom(parentType, owner))
            issues.Add(Diagnostic(path, text, "XAMLSCHEMA004", $"Property element '{element.Name}' cannot be applied to '{parentType.Name}'.", element.NameStart, element.NameLength));
    }

    internal static SchemaMember? Member(Element element, string name, INamedTypeSymbol type, SchemaTypeResolver resolver)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0) return SchemaMembers.Find(type, name);
        var owner = resolver.Resolve(element, name[..dot]);
        if (owner is null) return null;
        var member = SchemaMembers.Find(owner, name[(dot + 1)..], attached: true);
        // Routed events can be handled on an ancestor that does not derive from the
        // declaring control (for example Grid Button.Click="OnAnyButtonClick").
        var routedEvent = member?.IsEvent == true && member.Symbol.ContainingType.GetMembers(member.Name + "Event")
            .OfType<IFieldSymbol>().Any(field => field.IsStatic && field.DeclaredAccessibility == Accessibility.Public && field.Type.ToDisplayString() == "System.Windows.RoutedEvent");
        return member?.IsAttached == true || routedEvent || SchemaMembers.DerivesFrom(type, owner) ? member : null;
    }

    private static CompletionResult TypeCompletion(Element element, SchemaTypeResolver resolver, string fragment, int start, int length, long version)
    {
        var dot = fragment.LastIndexOf('.');
        if (dot >= 0)
        {
            var ownerName = fragment[..dot];
            var owner = resolver.Resolve(element, ownerName);
            return Result(version, start, length, owner is null ? [] : SchemaMembers.Instance(owner)
                .Concat(SchemaMembers.Attached(owner)).Where(member => !member.IsEvent).Select(member => MemberEntry(ownerName + "." + member.Name, member)).Where(item => item.DisplayText.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)));
        }
        return Result(version, start, length, TypesInScope(element, resolver).Where(item => item.DisplayText.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<CompletionEntry> TypesInScope(Element element, SchemaTypeResolver resolver, bool extensionsOnly = false)
    {
        foreach (var (prefix, uri) in Prefixes(element))
        {
            if (uri is Design or Compatibility || IgnoredNamespace(element, uri)) continue;
            foreach (var type in resolver.Types(uri))
            {
                if (extensionsOnly && !IsMarkupExtension(type)) continue;
                if (!extensionsOnly && type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Enum)) continue;
                var name = extensionsOnly && type.Name.EndsWith("Extension", StringComparison.Ordinal) ? type.Name[..^9] : type.Name;
                yield return Entry((prefix.Length == 0 ? "" : prefix + ":") + name, SchemaMembers.Describe(type), "Class");
            }
        }
    }

    private static CompletionResult? MarkupCompletion(string text, int position, long version, XamlSyntax.Attribute attribute, SchemaTypeResolver resolver)
    {
        var open = text.LastIndexOf('{', Math.Max(attribute.ValueStart, position - 1));
        if (open < attribute.ValueStart || open + 1 < text.Length && text[open + 1] == '}') return null;
        var start = open + 1;
        var end = start;
        while (end < attribute.ValueEnd && NameCharacter(text[end])) end++;
        if (position >= start && position <= end)
            return Result(version, start, end - start, TypesInScope(attribute.Owner, resolver, extensionsOnly: true).Where(item => item.DisplayText.StartsWith(text[start..position], StringComparison.OrdinalIgnoreCase)));
        var extensionName = text[start..end];
        var split = SplitName(extensionName);
        if (split.Local != "Type" || attribute.Owner.LookupNamespace(split.Prefix) != Language) return null;
        var nameStart = position;
        while (nameStart > end && NameCharacter(text[nameStart - 1])) nameStart--;
        var nameEnd = position;
        while (nameEnd < attribute.ValueEnd && NameCharacter(text[nameEnd])) nameEnd++;
        return TypeCompletion(attribute.Owner, resolver, text[nameStart..position], nameStart, nameEnd - nameStart, version);
    }

    private static bool IsMarkupExtension(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Windows.Markup.MarkupExtension") return true;
        return false;
    }

    private static IEnumerable<(string Prefix, string Uri)> Prefixes(Element element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = element; current is not null; current = current.Parent)
        foreach (var attribute in current.Attributes)
        {
            var prefix = attribute.Name == "xmlns" ? "" : attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal) ? attribute.Name[6..] : null;
            if (prefix is not null && seen.Add(prefix)) yield return (prefix, attribute.Value.Text);
        }
    }

    internal static bool IgnoredElement(Element element)
    {
        for (var current = element; current is not null; current = current.Parent)
            if (current.Namespace is Design or Compatibility || current.Namespace is { } ns && IgnoredNamespace(current, ns)) return true;
        return false;
    }

    internal static bool IgnoredAttribute(XamlSyntax.Attribute attribute)
    {
        if (attribute.Name == "xmlns" || attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal)) return true;
        var name = SplitName(attribute.Name);
        if (name.Prefix.Length == 0) return false;
        var ns = name.Prefix == "xml" ? XmlNamespace : attribute.Owner.LookupNamespace(name.Prefix);
        return ns is Language or Design or Compatibility or XmlNamespace || ns is not null && IgnoredNamespace(attribute.Owner, ns);
    }

    private static bool IgnoredNamespace(Element element, string uri)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            var ignorable = current.Attribute(Compatibility, "Ignorable");
            if (ignorable is null) continue;
            if (ignorable.Value.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(prefix => current.LookupNamespace(prefix) == uri)) return true;
        }
        return false;
    }

    private static CompletionEntry MemberEntry(string name, SchemaMember member) => Entry(name, SchemaMembers.Describe(member.Symbol), member.IsEvent ? "Event" : "Property");
    private static CompletionEntry Entry(string name, string description, string tag) => new(name, name, name, description, [tag]);
    private static CompletionResult Result(long version, int start, int length, IEnumerable<CompletionEntry> items) =>
        new(version, start, length, items.DistinctBy(item => item.DisplayText).OrderBy(item => item.DisplayText, StringComparer.Ordinal).Take(250).ToArray());
    private static bool NameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';
    private static int Offset(string text, int line, int column)
    {
        var offset = 0;
        for (var current = 1; current < line; current++) { var next = text.IndexOf('\n', offset); if (next < 0) return text.Length; offset = next + 1; }
        return Math.Clamp(offset + Math.Max(0, column - 1), 0, text.Length);
    }
    private static WorkspaceDiagnostic Diagnostic(string path, string text, string id, string message, int start, int length, string severity = "Warning")
    {
        start = Math.Clamp(start, 0, text.Length); length = Math.Clamp(length, 0, text.Length - start);
        var before = text.AsSpan(0, start);
        return new(id, message, severity, path, before.Count('\n') + 1, start - before.LastIndexOf('\n'), start, length);
    }
    private static string? Nearest(string name, IEnumerable<string> values)
    {
        if (name.Length > 128) return null;
        return values.Where(value => value.Length <= 128 && Math.Abs(value.Length - name.Length) <= 2)
            .Select(value => (Value: value, Distance: Distance(name, value))).Where(value => value.Distance <= (name.Length <= 3 ? 1 : 2))
            .OrderBy(value => value.Distance).ThenBy(value => value.Value, StringComparer.Ordinal).Select(value => value.Value).FirstOrDefault();
    }
    private static int Distance(string left, string right)
    {
        var costs = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) costs[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) costs[0, j] = j;
        for (var i = 1; i <= left.Length; i++)
        for (var j = 1; j <= right.Length; j++)
        {
            costs[i, j] = Math.Min(Math.Min(costs[i - 1, j] + 1, costs[i, j - 1] + 1), costs[i - 1, j - 1] + (char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1));
            if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1]) costs[i, j] = Math.Min(costs[i, j], costs[i - 2, j - 2] + 1);
        }
        return costs[left.Length, right.Length];
    }
}
