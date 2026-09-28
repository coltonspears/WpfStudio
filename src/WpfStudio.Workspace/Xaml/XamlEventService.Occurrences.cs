using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed partial class XamlEventService
{
    public XamlSymbolOccurrenceResult GetSymbolOccurrences(string text, Compilation compilation, CancellationToken token = default)
    {
        var collector = new XamlOccurrenceCollector();
        if (!collector.Validate(text, token)) return collector.Result();
        var targets = Targets(text, compilation, token, out string? status);
        if (status is not null) collector.Warn(status, coverageLimited: targets.Count >= MaximumTargets);
        var resolutions = Resolve(compilation, targets, token, out bool complete);
        if (!complete) collector.Warn("Event-handler symbol coverage is incomplete because a C# context could not be verified or the probe budget was reached.",
            coverageLimited: targets.Where(target => SimpleName(target.HandlerName)).DistinctBy(Key).Take(MaximumProbes + 1).Count() > MaximumProbes);
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (target.HandlerName.Length == 0) continue;
            if (resolutions.TryGetValue(Key(target), out var resolved) && resolved.Method is { } method)
            {
                if (!collector.Add(method, target.Start, target.Length, "EventHandler")) return collector.Result();
            }
            else collector.Warn("One or more event values do not resolve to a unique compatible instance handler on the root x:Class.");
        }

        // Targets deliberately omits unknown/dynamic contexts for normal editor diagnostics.
        // Rename/reference callers must also see that omission as incomplete coverage.
        var resolver = new SchemaTypeResolver(compilation, token);
        int attributes = 0;
        var targetNames = targets.Select(target => target.AttributeStart).ToHashSet();
        foreach (var element in Read(text, token))
        {
            token.ThrowIfCancellationRequested();
            if (XamlSchemaService.IgnoredElement(element) || element.LocalName.Contains('.')) continue;
            var type = resolver.Resolve(element, element.Name);
            if (type is null || !SchemaMembers.IsComplete(type))
            { collector.Warn("Some authored element types are unresolved; their event-handler references could not be examined."); continue; }
            foreach (var attribute in element.Attributes)
            {
                token.ThrowIfCancellationRequested();
                if (XamlSchemaService.IgnoredAttribute(attribute)) continue;
                if (++attributes > 16384)
                { collector.Warn("The event-attribute analysis budget was reached; coverage is incomplete.", coverageLimited: true); return collector.Result(); }
                var member = type.ToDisplayString() == "System.Windows.EventSetter" && attribute.Name == "Handler"
                    ? EventSetterMember(element, resolver) : XamlSchemaService.Member(element, attribute.Name, type, resolver);
                if (member is null)
                { collector.Warn("Some authored members are unresolved; possible event-handler references could not be examined."); continue; }
                if (member.IsEvent && attribute.Value.Text.Trim().Length > 0 && !targetNames.Contains(attribute.NameStart))
                    collector.Warn("An event value has an unknown root class, dynamic value, or unsupported semantic context.");
            }
        }
        return collector.Result();
    }
}
