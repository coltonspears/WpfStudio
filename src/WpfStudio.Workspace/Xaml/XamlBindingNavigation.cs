using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed partial class XamlLanguageService
{
    public IReadOnlyList<SourceLocation> GetDefinition(string path, string text, int position, Compilation compilation, CancellationToken token = default, XamlResourceContext? resources = null)
    {
        var resolver = new SourceResolver(compilation, token, resources);
        var elements = Read(text, token);
        if (NamedReference(elements, text, position, resolver, token) is { } declaration)
        {
            var before = text.AsSpan(0, declaration.ValueStart);
            return [new SourceLocation(path, declaration.ValueStart, declaration.ValueEnd - declaration.ValueStart,
                before.Count('\n') + 1, declaration.ValueStart - before.LastIndexOf('\n'), declaration.Owner.Name)];
        }
        var selected = SelectedStep(elements, position, resolver);
        if (selected?.Step.Symbol is not { } symbol) return [];
        return symbol.Locations.Where(location => location.IsInSource && location.SourceTree is not null)
            .Select(location =>
            {
                var line = location.GetLineSpan();
                return new SourceLocation(location.SourceTree!.FilePath, location.SourceSpan.Start, location.SourceSpan.Length,
                    line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1,
                    symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }).Distinct().ToArray();
    }

    public XamlHoverInfo? GetHover(string path, string text, int position, Compilation compilation, CancellationToken token = default, XamlResourceContext? resources = null)
    {
        var resolver = new SourceResolver(compilation, token, resources);
        var selected = SelectedStep(Read(text, token), position, resolver);
        if (selected is null) return null;
        var step = selected.Step;
        string content;
        if (step.IsMissing) content = MissingMessage(step);
        else if (step.Symbol is { } symbol)
        {
            content = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var documentation = symbol.GetDocumentationCommentXml(cancellationToken: token);
            if (!string.IsNullOrWhiteSpace(documentation))
            {
                try
                {
                    var summary = XElement.Parse(documentation).Element("summary")?.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(summary)) content += "\n" + summary;
                }
                catch (System.Xml.XmlException) { }
            }
        }
        else if (step.Result.Type is { } resultType) content = resultType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        else content = "Binding source could not be determined statically.";
        content += "\nSource: " + step.Source.Reason;
        if (step.Source.Type is { } type) content += " (" + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + ")";
        return new XamlHoverInfo(selected.Start, selected.Length, content);
    }

    public IReadOnlyList<XamlCodeAction> GetCodeActions(string path, string text, int position, long version, Compilation compilation, CancellationToken token = default, XamlResourceContext? resources = null)
    {
        var resolver = new SourceResolver(compilation, token, resources);
        var selected = SelectedStep(Read(text, token), position, resolver);
        if (selected is not { Step.IsMissing: true, Step.Suggestion: { } suggestion }) return [];
        var edit = new DocumentEdits(path, version, [new TextEdit(selected.Start, selected.Length, suggestion)],
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        var used = resolver.UsedResourceDocuments.Where(document => !string.Equals(document.Path.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .GroupBy(document => document.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        // A single physical file cannot be guarded against two different linked snapshots.
        if (used.Any(group => group.Any(document => document.Text is null) || group.Select(document => (document.Text, document.Version)).Distinct().Skip(1).Any())) return [];
        var prerequisites = used.Select(group => group.First()).Select(document => new DocumentEdits(document.Path, document.Version ?? 0, [],
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Text!))))).ToArray();
        return [new XamlCodeAction($"Change '{selected.Step.Segment.Name}' to '{suggestion}'", edit, prerequisites.Length == 0 ? null : prerequisites)];
    }

    private sealed record SelectedBindingStep(BindingStep Step, int Start, int Length);
    private static SelectedBindingStep? SelectedStep(IReadOnlyList<Element> elements, int position, SourceResolver resolver)
    {
        foreach (var attribute in elements.SelectMany(e => e.Attributes))
        {
            if (position < attribute.ValueStart || position > attribute.ValueEnd) continue;
            if (IgnoredBindingAttribute(attribute)) continue;
            var binding = GetBinding(attribute);
            if (binding is null || !TrySegments(binding.Path, out var segments)) continue;
            foreach (var step in resolver.Walk(attribute, binding, segments))
            {
                if (step.OwnerSymbol is { } owner && step.Segment.OwnerLength > 0)
                {
                    var ownerSpan = attribute.Value.Span(binding.Start + step.Segment.OwnerStart, step.Segment.OwnerLength);
                    if (position >= ownerSpan.Start && position < ownerSpan.Start + ownerSpan.Length)
                        return new SelectedBindingStep(step with { Symbol = owner, IsMissing = false,
                            Result = new BindingSourceInfo(owner, null, "Qualified XAML property owner") }, ownerSpan.Start, ownerSpan.Length);
                }
                var span = attribute.Value.Span(binding.Start + step.Segment.Start, step.Segment.Length);
                if (position >= span.Start && position <= span.Start + span.Length) return new SelectedBindingStep(step, span.Start, span.Length);
            }
        }
        return null;
    }
    private static XamlSyntax.Attribute? NamedReference(IReadOnlyList<Element> elements, string text, int position,
        SourceResolver resolver, CancellationToken token)
    {
        foreach (var attribute in elements.SelectMany(e => e.Attributes))
        {
            if (position < attribute.ValueStart || position > attribute.ValueEnd) continue;
            if (IgnoredBindingAttribute(attribute)) continue;
            if (IsPresentationElement(attribute.Owner, "Binding") && attribute.Name == "ElementName")
            {
                var binding = GetBinding(attribute.Owner.Attribute("Path") ?? new XamlSyntax.Attribute("Path", 0, 0, "", attribute.Owner));
                if (binding is not null && NameReferenceValue(attribute, 0, attribute.Value.Text.Length, position, quoted: false) is { } reference
                    && CanNavigateName() && resolver.HasElementNameSource(attribute, binding))
                    return resolver.FindNameDeclaration(binding.ContextOwner, reference);
                continue;
            }
            var descriptor = GetBinding(attribute);
            var name = descriptor?.Extension?.Argument("ElementName");
            if (descriptor is null || name is null) continue;
            if (NameReferenceValue(attribute, name.Start, name.Length, position, quoted: true) is { } value
                && CanNavigateName() && resolver.HasElementNameSource(attribute, descriptor))
                return resolver.FindNameDeclaration(descriptor.ContextOwner, value);
        }
        return null;

        // Names depend on the whole authored scope. Reuse the secure XML/text/tree
        // validation used by semantic occurrences, without restricting member F12
        // or the tolerant parser used for completion during an unfinished edit.
        bool CanNavigateName() => new XamlOccurrenceCollector().Validate(text, token);
    }

    private static string? NameReferenceValue(XamlSyntax.Attribute attribute, int start, int length, int position, bool quoted)
    {
        var text = attribute.Value.Text;
        int end = start + length;
        TrimRange(text, ref start, ref end);
        if (quoted && end - start >= 2 && text[start] is '\'' or '"' && text[end - 1] == text[start])
        {
            start++;
            end--;
            TrimRange(text, ref start, ref end);
        }
        if (start == end) return null;
        var span = attribute.Value.Span(start, end - start);
        return position >= span.Start && position <= span.Start + span.Length ? text[start..end] : null;
    }
}
