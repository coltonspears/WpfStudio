using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed partial class XamlLanguageService
{
    public XamlSymbolOccurrenceResult GetSymbolOccurrences(string text, Compilation compilation, CancellationToken token = default, XamlResourceContext? resources = null)
    {
        var collector = new XamlOccurrenceCollector();
        if (!collector.Validate(text, token)) return collector.Result();
        var resolver = new SourceResolver(compilation, token, resources);
        int paths = 0, walkedSegments = 0;
        foreach (var element in Read(text, token))
        {
            token.ThrowIfCancellationRequested();
            if (XamlSchemaService.IgnoredElement(element)) continue;
            if (IsPresentationElement(element, "Binding") && element.Children.Any(child => child.LocalName == "Binding.Path"))
                collector.Warn("Binding.Path property-element values are not covered by symbol analysis.");
            foreach (var attribute in element.Attributes)
            {
                token.ThrowIfCancellationRequested();
                if (XamlSchemaService.IgnoredAttribute(attribute)) continue;
                var binding = GetBinding(attribute);
                if (binding is null)
                {
                    if (ContainsUnexaminedBinding(element, ParseExtension(attribute.Value.Text), 0, collector))
                        collector.Warn("A template binding or nested binding markup extension is not covered by symbol analysis.");
                    continue;
                }
                if (++paths > 4096)
                { collector.Warn("The binding-path analysis budget was reached; coverage is incomplete.", coverageLimited: true); return collector.Result(); }
                if (!binding.Complete || !TrySegments(binding.Path, out var segments))
                { collector.Warn("A binding path is incomplete or uses unsupported property-path syntax."); continue; }
                if (binding.Extension?.Arguments.Any(argument => ContainsUnexaminedBinding(element, ParseExtension(argument.Value), 0, collector)) == true)
                    collector.Warn("A nested binding markup extension is not covered by symbol analysis.");
                if (segments.Count == 0) continue;
                int visited = 0;
                foreach (var step in resolver.Walk(attribute, binding, segments))
                {
                    token.ThrowIfCancellationRequested();
                    if (++walkedSegments > 32768)
                    { collector.Warn("The total binding-segment analysis budget was reached; coverage is incomplete.", coverageLimited: true); return collector.Result(); }
                    visited++;
                    if (step.Source.Element is { } sourceElement && XamlSchemaService.IgnoredElement(sourceElement))
                    { collector.Warn("A binding source refers to ignored or design-only content and cannot establish a runtime member reference."); break; }
                    if (step.Segment.Kind == SegmentKind.QualifiedProperty)
                        collector.Warn("Parenthesized and owner-qualified binding members are not included in property rename/reference coverage; dependency-property registration and accessor names are separate declarations.");
                    else if (step.Segment.Kind == SegmentKind.Property && step.Symbol is IPropertySymbol property)
                    {
                        if (step.Source.Reason.Contains("d:DesignInstance", StringComparison.Ordinal))
                            collector.Note("Some binding references are inferred from d:DesignInstance; runtime DataContext identity has not been verified.");
                        var span = attribute.Value.Span(binding.Start + step.Segment.Start, step.Segment.Length);
                        if (!collector.Add(property, span.Start, span.Length, "BindingProperty")) return collector.Result();
                    }
                    else if (step.Segment.Kind == SegmentKind.Property || step.Result.Type is null)
                        collector.Warn("A binding path could not be resolved completely: " + step.Result.Reason);
                }
                if (visited < segments.Count)
                    collector.Warn("One or more binding path continuations could not be resolved statically.");
            }
        }
        if (resolver.ResourceCoverageLimited) collector.Warn("Resource lookup exceeded its snapshot or traversal budget; dependent references could not be verified.", coverageLimited: true);
        return collector.Result();
    }

    private static bool ContainsUnexaminedBinding(Element owner, Extension? extension, int depth, XamlOccurrenceCollector collector)
    {
        if (extension is null) return false;
        if (depth >= 16)
        { collector.Warn("Nested markup extensions exceeded the symbol-analysis depth budget.", coverageLimited: true); return true; }
        if (IsExtension(owner, extension, Presentation, "Binding") || IsExtension(owner, extension, Presentation, "TemplateBinding")
            || IsExtension(owner, extension, Presentation, "MultiBinding") || IsExtension(owner, extension, Presentation, "PriorityBinding")) return true;
        return extension.Arguments.Any(argument => ContainsUnexaminedBinding(owner, ParseExtension(argument.Value), depth + 1, collector));
    }
}
