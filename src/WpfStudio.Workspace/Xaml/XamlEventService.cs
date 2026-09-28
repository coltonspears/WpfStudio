using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed record XamlEventTarget(INamedTypeSymbol RootClass, INamedTypeSymbol DelegateType, IMethodSymbol InvokeMethod,
    ISymbol EventSymbol, string EventName, string HandlerName, int Start, int Length, int AttributeStart, int AttributeLength);
public sealed record XamlEventAnalysis(IReadOnlyList<WorkspaceDiagnostic> Diagnostics, bool IsComplete, string? Status = null);

/// <summary>Validates the delegate construction emitted by WPF using the current Roslyn compilation.</summary>
public sealed partial class XamlEventService
{
    private const int MaximumProbes = 512;
    private const int MaximumTargets = 4096;

    public IReadOnlyList<WorkspaceDiagnostic> Analyze(string path, string text, long version, Compilation compilation, CancellationToken token = default)
        => AnalyzeDetailed(path, text, version, compilation, 2001, token).Diagnostics;

    public XamlEventAnalysis AnalyzeDetailed(string path, string text, long version, Compilation compilation,
        int maximumDiagnostics = 2001, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read()) token.ThrowIfCancellationRequested();
        }
        catch (XmlException) { return new([], true); }
        var targets = Targets(text, compilation, token, out var status);
        if (targets.Count == 0) return new([], status is null, status);
        var resolutions = Resolve(compilation, targets, token, out bool complete);
        var diagnostics = new List<WorkspaceDiagnostic>();
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (target.HandlerName.Length == 0) continue; // Normal while typing an empty attribute.
            string? id = null, message = null;
            if (!SimpleName(target.HandlerName))
            {
                id = "XAMLEVENT003";
                message = "An event handler must name one instance method on the root x:Class; qualified names and markup expressions are not supported here.";
            }
            else if (resolutions.TryGetValue(Key(target), out var resolution))
            {
                (id, message) = resolution.State switch
                {
                    "Missing" => ("XAMLEVENT001", $"Event handler '{target.HandlerName}' was not found on '{target.RootClass.Name}'."),
                    "Ambiguous" => ("XAMLEVENT004", $"Event handler '{target.HandlerName}' is ambiguous for '{target.EventName}' ({target.DelegateType.Name})."),
                    "Incompatible" => ("XAMLEVENT002", $"Event handler '{target.HandlerName}' cannot be used as an instance handler for '{target.EventName}' ({target.DelegateType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}). {resolution.Explanation}"),
                    _ => (null, null)
                };
            }
            if (id is null) continue;
            var before = text.AsSpan(0, target.Start);
            diagnostics.Add(new(id, Limit(message!, 2048), "Error", path, before.Count('\n') + 1,
                target.Start - before.LastIndexOf('\n'), target.Start, target.Length));
            if (diagnostics.Count >= maximumDiagnostics) { complete = false; break; }
        }
        return new(diagnostics, complete && status is null, status ?? (!complete ? "Event-handler coverage is incomplete because a C# declaration could not be verified or an analysis budget was reached." : null));
    }

    public XamlEventTarget? GetTarget(string text, int position, Compilation compilation, CancellationToken token = default)
        => Targets(text, compilation, token, out _).FirstOrDefault(target => position >= target.AttributeStart && position <= target.AttributeStart + target.AttributeLength
            || position >= target.Start && position <= target.Start + target.Length);

    public CompletionResult? Complete(string text, int position, long version, Compilation compilation, CancellationToken token = default)
    {
        var target = GetTarget(text, position, compilation, token);
        if (target is null || position < target.Start || position > target.Start + target.Length) return null;
        var attribute = Read(text, token).SelectMany(element => element.Attributes).FirstOrDefault(attribute => attribute.NameStart == target.AttributeStart);
        if (attribute is null) return null;
        int leading = attribute.Value.Text.Length - attribute.Value.Text.TrimStart().Length;
        int count = Math.Clamp(attribute.Value.PositionAt(position) - leading, 0, target.HandlerName.Length);
        string prefix = target.HandlerName[..count];
        if (prefix.Length > 0 && !SimpleName(prefix)) return new(version, target.Start, target.Length, []);
        var names = Methods(target.RootClass).Select(method => method.Name).Distinct(StringComparer.Ordinal)
            .Where(name => SimpleName(name) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal).Take(MaximumProbes).ToArray();
        var candidates = names.Select(name => target with { HandlerName = name }).ToArray();
        var resolved = Resolve(compilation, candidates, token, out _);
        var entries = candidates.Where(candidate => resolved.TryGetValue(Key(candidate), out var resolution) && resolution.Method is not null)
            .Select(candidate => new CompletionEntry(candidate.HandlerName, candidate.HandlerName, candidate.HandlerName,
                SchemaMembers.Describe(resolved[Key(candidate)].Method!), ["Method"])).Take(250).ToArray();
        return new(version, target.Start, target.Length, entries);
    }

    public XamlHoverInfo? GetHover(string path, string text, int position, Compilation compilation, CancellationToken token = default)
    {
        var target = GetTarget(text, position, compilation, token);
        if (target is null || position < target.Start || !SimpleName(target.HandlerName)) return null;
        var resolved = Resolve(compilation, [target], token, out _).GetValueOrDefault(Key(target));
        return resolved?.Method is { } method ? new(target.Start, target.Length,
            SchemaMembers.Describe(method) + $"\n\nHandles {target.EventName} ({target.DelegateType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}).") : null;
    }

    public IReadOnlyList<SourceLocation> GetDefinition(string path, string text, int position, Compilation compilation, CancellationToken token = default)
    {
        var target = GetTarget(text, position, compilation, token);
        if (target is null || position < target.Start || !SimpleName(target.HandlerName)) return [];
        var resolved = Resolve(compilation, [target], token, out _).GetValueOrDefault(Key(target));
        if (resolved?.Method is not { } method) return [];
        method = method.PartialImplementationPart ?? method;
        return method.Locations.Where(location => location.IsInSource).Select(location =>
        {
            var line = location.GetLineSpan();
            return new SourceLocation(location.SourceTree!.FilePath, location.SourceSpan.Start, location.SourceSpan.Length,
                line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1, method.ToDisplayString());
        }).ToArray();
    }

    private static IReadOnlyList<XamlEventTarget> Targets(string text, Compilation compilation, CancellationToken token, out string? status)
    {
        status = null;
        if (compilation is not CSharpCompilation) return [];
        var elements = Read(text, token);
        var root = elements.FirstOrDefault(element => element.Parent is null);
        if (root is null) return [];
        if (root.Attribute(Language, "Subclass") is not null || elements.Any(element => element.Namespace == Language && element.LocalName == "Code"))
        { status = "Event-handler analysis is unavailable for x:Subclass or inline x:Code."; return []; }
        var className = root.Attribute(Language, "Class")?.Value.Text.Trim();
        if (string.IsNullOrEmpty(className)) return [];
        var rootClass = compilation.Assembly.GetTypeByMetadataName(className);
        if (rootClass is null || rootClass.TypeKind != TypeKind.Class || rootClass.Arity != 0 || rootClass.ContainingType is not null || !SchemaMembers.IsComplete(rootClass))
        { status = "Event-handler analysis is unavailable because the root x:Class or its base types could not be resolved completely."; return []; }
        var resolver = new SchemaTypeResolver(compilation, token);
        var targets = new List<XamlEventTarget>();
        foreach (var element in elements)
        {
            token.ThrowIfCancellationRequested();
            if (XamlSchemaService.IgnoredElement(element) || resolver.Resolve(element, element.Name) is not { } type || !SchemaMembers.IsComplete(type)) continue;
            foreach (var attribute in element.Attributes)
            {
                if (XamlSchemaService.IgnoredAttribute(attribute)) continue;
                var member = type.ToDisplayString() == "System.Windows.EventSetter" && attribute.Name == "Handler"
                    ? EventSetterMember(element, resolver) : XamlSchemaService.Member(element, attribute.Name, type, resolver);
                if (member?.IsEvent != true || member.ValueType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke } delegateType) continue;
                if (!SignatureComplete(invoke))
                { status = "Event-handler coverage is incomplete because an event delegate signature contains unresolved type information."; continue; }
                if (type.ToDisplayString() != "System.Windows.EventSetter" && member.IsAttached && member.Symbol is IMethodSymbol adder
                    && !((CSharpCompilation)compilation).ClassifyConversion(type, adder.Parameters[0].Type).IsImplicit) continue;
                string handler = attribute.Value.Text.Trim();
                // Markup extensions and dynamic event providers are deliberately unknown.
                if (handler.StartsWith('{')) continue;
                int leading = attribute.Value.Text.Length - attribute.Value.Text.TrimStart().Length;
                var span = handler.Length == 0 ? (Start: attribute.ValueStart, Length: attribute.ValueEnd - attribute.ValueStart)
                    : attribute.Value.Span(leading, handler.Length);
                targets.Add(new(rootClass, delegateType, invoke, member.Symbol, member.Name, handler, span.Start, span.Length,
                    attribute.NameStart, attribute.NameLength));
                if (targets.Count >= MaximumTargets) { status = "Event-handler analysis reached its authored-event budget; coverage is incomplete."; return targets; }
            }
        }
        return targets;
    }

    private static SchemaMember? EventSetterMember(Element element, SchemaTypeResolver resolver)
    {
        string? name = element.Attribute("Event")?.Value.Text.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        int dot = name.LastIndexOf('.');
        INamedTypeSymbol? owner = dot > 0 ? resolver.Resolve(element, name[..dot]) : null;
        if (dot < 0)
            for (var ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (resolver.Resolve(ancestor, ancestor.Name)?.ToDisplayString() == "System.Windows.Style")
                { owner = resolver.ResolveTypeValue(ancestor, ancestor.Attribute("TargetType")?.Value.Text ?? ""); break; }
        if (owner is null || !SchemaMembers.IsComplete(owner)) return null;
        var member = SchemaMembers.Find(owner, dot >= 0 ? name[(dot + 1)..] : name, attached: true);
        // EventSetter.Event requires a registered RoutedEvent, not an arbitrary CLR event.
        if (member?.IsEvent != true) return null;
        string fieldName = member.Name + "Event";
        for (var current = owner; current is not null; current = current.BaseType)
            if (current.GetMembers(fieldName).OfType<IFieldSymbol>().Any(field => field.IsStatic
                && field.DeclaredAccessibility == Accessibility.Public && field.Type.ToDisplayString() == "System.Windows.RoutedEvent")) return member;
        return null;
    }

    private static IEnumerable<IMethodSymbol> Methods(INamedTypeSymbol type)
    {
        int depth = 0;
        for (var current = type; current is not null && depth++ < 64; current = current.BaseType)
        foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            if (method.MethodKind == MethodKind.Ordinary && method.ExplicitInterfaceImplementations.Length == 0) yield return method;
    }
    private static string Key(XamlEventTarget target) => target.DelegateType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "\n" + target.HandlerName;
    private static bool SimpleName(string value)
    {
        if (value.Length is not (> 0 and <= 512) || value.Contains('@') || value.Contains('\\')) return false;
        string escaped = "@" + value;
        var identifier = SyntaxFactory.ParseToken(escaped);
        return identifier.IsKind(SyntaxKind.IdentifierToken) && !identifier.ContainsDiagnostics
            && identifier.Text == escaped && identifier.ValueText == value;
    }
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";
}
