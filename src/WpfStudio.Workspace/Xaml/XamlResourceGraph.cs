using System.Xml;
using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

internal sealed record XamlResourceLookup(Element? Value, bool Unknown, string Reason)
{
    public static XamlResourceLookup Missing { get; } = new(null, false, "No matching declared resource was found.");
    public static XamlResourceLookup Unavailable(string reason) => new(null, true, reason);
}

/// <summary>Operation-local, bounded resource lookup over evaluated source snapshots. Performs no IO.</summary>
internal sealed class XamlResourceGraph(Compilation compilation, XamlResourceContext? context, CancellationToken token)
{
    private const int MaximumWork = 65_536;
    private readonly SchemaTypeResolver _types = new(compilation, token);
    private readonly XamlResourceSchema _schema = new(compilation, token);
    private readonly Dictionary<Element, XamlResourceDocument?> _origins = [];
    private readonly Dictionary<XamlResourceDocument, (Element? Root, string? Error)> _loaded = [];
    private readonly HashSet<(Element Element, string Key)> _queries = [];
    private readonly HashSet<(Element Element, string Key)> _dictionaryQueries = [];
    private readonly HashSet<(Element Element, string Key)> _styles = [];
    private readonly HashSet<XamlResourceDocument> _loading = [];
    private readonly HashSet<XamlResourceDocument> _usedDocuments = [];
    private int _work, _characters;
    public bool CoverageLimited { get; private set; }
    public IReadOnlyList<XamlResourceDocument> UsedDocuments => _usedDocuments.ToArray();

    public string? DeclaringAssembly(Element element) => Origin(element)?.AssemblyName ?? context?.SourceAssembly;
    public string? DeclarationPath(Element element) => Origin(element)?.Path ?? context?.SourcePath;
    public void MarkCoverageLimited() => CoverageLimited = true;

    public XamlResourceLookup Lookup(Element owner, string key, int depth = 0, Element? reference = null)
    {
        reference ??= owner;
        if (!Spend() || depth >= 64) return Limited();
        if (!LiteralKey(key, out key)) return XamlResourceLookup.Unavailable("The resource key requires runtime evaluation.");
        if (!_queries.Add((owner, key))) return XamlResourceLookup.Unavailable("Resource aliases or BasedOn declarations contain a cycle.");
        try
        {
            int ancestors = 0;
            for (var current = owner; current is not null; current = current.Parent)
            {
                if (!Spend() || ++ancestors > 256) return Limited();
                if (HasBaseOverride(current)) return XamlResourceLookup.Unavailable("The resource URI base is overridden by xml:base.");
                if (PresentationElement(current, "ResourceDictionary"))
                {
                    var result = SearchDictionary(current, key, reference, depth + 1);
                    if (result.Value is not null || result.Unknown) return result;
                }
                else
                {
                    if (current.Attribute("Resources") is not null)
                        return XamlResourceLookup.Unavailable("The local Resources value is not a dictionary object declaration.");
                    if (current.Children.Any(child => !XamlSchemaService.IgnoredElement(child) && child.LocalName.EndsWith(".Resources", StringComparison.Ordinal)
                        && !IsResourcesProperty(current, child)))
                        return XamlResourceLookup.Unavailable("A local Resources property has an unverified owner or dictionary type.");
                    var containers = current.Children.Where(child => IsResourcesProperty(current, child)).Take(2).ToArray();
                    if (containers.Length > 1) return XamlResourceLookup.Unavailable("Resources have multiple local declarations.");
                    if (containers.Length == 1)
                    {
                        var result = SearchDictionary(containers[0], key, reference, depth + 1);
                        if (result.Value is not null || result.Unknown) return result;
                    }
                    if (PresentationElement(current, "Style"))
                    {
                        var basedOn = SearchBasedOn(current, key, reference, depth + 1);
                        if (basedOn.Value is not null || basedOn.Unknown) return basedOn;
                    }
                }
            }
            if (context?.ApplicationPath is { } applicationPath)
            {
                var application = context.Index.FindDocument(applicationPath, context.SourceAssembly, "ApplicationDefinition");
                CoverageLimited |= application.CoverageLimited;
                if (application.Document is null) return XamlResourceLookup.Unavailable(application.Status!);
                var loaded = Load(application.Document);
                if (loaded.Root is null) return XamlResourceLookup.Unavailable(loaded.Error!);
                if (!PresentationElement(loaded.Root, "Application")) return XamlResourceLookup.Unavailable("The application resource root is not a known Application declaration.");
                var containers = loaded.Root.Children.Where(child => IsResourcesProperty(loaded.Root, child)).Take(2).ToArray();
                if (containers.Length > 1) return XamlResourceLookup.Unavailable("Application.Resources has multiple declarations.");
                if (containers.Length == 1)
                {
                    var result = SearchDictionary(containers[0], key, reference, depth + 1);
                    if (result.Value is not null || result.Unknown) return result;
                }
            }
            if (context is { IsComplete: false })
            {
                CoverageLimited = true;
                return XamlResourceLookup.Unavailable(context.Status ?? "The evaluated resource snapshot is incomplete.");
            }
            return XamlResourceLookup.Missing;
        }
        finally { _queries.Remove((owner, key)); }
    }

    private XamlResourceLookup SearchDictionary(Element container, string key, Element reference, int depth)
    {
        if (!Spend() || depth >= 64) return Limited();
        if (!container.IsClosed || container.HasSignificantText || HasBaseOverride(container))
            return XamlResourceLookup.Unavailable("The resource dictionary is incomplete or has unsupported content/base metadata.");
        if (container.Attribute(Language, "Class") is not null || container.Attribute(Language, "FactoryMethod") is not null)
            return XamlResourceLookup.Unavailable("A code-backed or factory-created dictionary may provide additional runtime resources.");
        if (container.Attribute("Source") is not null && container.Children.Any(child => !XamlSchemaService.IgnoredElement(child)))
            return XamlResourceLookup.Unavailable("Source combined with inline dictionary content is not statically resolved.");
        if (!_dictionaryQueries.Add((container, key))) return XamlResourceLookup.Unavailable("Resource dictionary lookup contains a cycle.");
        try
        {
            // A Resources property may wrap one dictionary. It is not another merge list.
            var wrappers = container.Children.Where(child => PresentationElement(child, "ResourceDictionary")
                && child.Attribute(Language, "Key") is null && !XamlSchemaService.IgnoredElement(child)).Take(2).ToArray();
            bool propertyContainer = !PresentationElement(container, "ResourceDictionary");
            if (propertyContainer && wrappers.Length != 0)
            {
                if (wrappers.Length != 1 || container.Children.Any(child => child != wrappers[0] && !XamlSchemaService.IgnoredElement(child)))
                    return XamlResourceLookup.Unavailable("Resources mix a dictionary object with other entries.");
                return SearchDictionary(wrappers[0], key, reference, depth + 1);
            }

            var direct = new List<Element>();
            bool uncertainKey = false;
            foreach (var child in container.Children)
            {
                if (!Spend()) return Limited();
                if (XamlSchemaService.IgnoredElement(child) || IsMergeProperty(child)) continue;
                if (child.LocalName.Contains('.')) return XamlResourceLookup.Unavailable("The dictionary contains unsupported property-element content.");
                var declared = child.Attribute(Language, "Key");
                if (declared is not null)
                {
                    if (!LiteralKey(declared.Value.Text, out var candidate)) uncertainKey = true;
                    else if (candidate == key) direct.Add(child);
                }
                else if (!PresentationElement(child, "Style") && !PresentationElement(child, "DataTemplate")) uncertainKey = true;
            }
            if (direct.Count > 1) return XamlResourceLookup.Unavailable("The dictionary declares the resource key more than once.");
            if (direct.Count == 1)
            {
                var value = direct[0];
                if (uncertainKey) return XamlResourceLookup.Unavailable("A runtime resource key could conflict with the declared key.");
                if (!value.IsClosed) return XamlResourceLookup.Unavailable("The resource declaration is unfinished.");
                if (SameRoot(value, reference) && value.Start >= reference.Start)
                    return XamlResourceLookup.Unavailable("A StaticResource forward reference is not supported by this declaration analysis.");
                return new(value, false, "StaticResource '" + Short(key) + "' declared in " + Short(DeclarationPath(value) ?? "this document"));
            }
            if (uncertainKey) return XamlResourceLookup.Unavailable("The dictionary contains keys requiring runtime evaluation.");

            var source = container.Attribute("Source");
            var mergeProperties = container.Children.Where(IsMergeProperty).Take(2).ToArray();
            if (mergeProperties.Length > 1) return XamlResourceLookup.Unavailable("MergedDictionaries has multiple declarations.");
            if (source is not null)
            {
                if (container.Children.Any(child => !XamlSchemaService.IgnoredElement(child)))
                    return XamlResourceLookup.Unavailable("Source combined with inline dictionary content is not statically resolved.");
                var resolved = ResolveDocument(container, source.Value.Text);
                if (resolved.Document is null) return XamlResourceLookup.Unavailable(resolved.Error!);
                if (!_loading.Add(resolved.Document)) return XamlResourceLookup.Unavailable("Resource dictionary Source declarations contain a cycle.");
                try
                {
                    var loaded = Load(resolved.Document);
                    if (loaded.Root is null) return XamlResourceLookup.Unavailable(loaded.Error!);
                    if (!PresentationElement(loaded.Root, "ResourceDictionary"))
                        return XamlResourceLookup.Unavailable("The external resource root is not a known ResourceDictionary.");
                    return SearchDictionary(loaded.Root, key, reference, depth + 1);
                }
                finally { _loading.Remove(resolved.Document); }
            }
            if (mergeProperties.Length == 1)
            {
                var merges = mergeProperties[0];
                if (!merges.IsClosed || merges.HasSignificantText) return XamlResourceLookup.Unavailable("MergedDictionaries is unfinished or contains unsupported content.");
                for (int i = merges.Children.Count - 1; i >= 0; i--)
                {
                    var dictionary = merges.Children[i];
                    if (XamlSchemaService.IgnoredElement(dictionary)) continue;
                    if (!PresentationElement(dictionary, "ResourceDictionary"))
                        return XamlResourceLookup.Unavailable("A custom merged dictionary may provide runtime keys.");
                    if (SameRoot(dictionary, reference) && dictionary.Start >= reference.Start)
                        return XamlResourceLookup.Unavailable("A merged dictionary is declared after this StaticResource use.");
                    var result = SearchDictionary(dictionary, key, reference, depth + 1);
                    if (result.Value is not null || result.Unknown) return result;
                }
            }
            return XamlResourceLookup.Missing;
        }
        finally { _dictionaryQueries.Remove((container, key)); }
    }

    private XamlResourceLookup SearchBasedOn(Element style, string key, Element reference, int depth)
    {
        if (!Spend() || depth >= 64) return Limited();
        if (!_styles.Add((style, key))) return XamlResourceLookup.Unavailable("Style.BasedOn contains a cycle.");
        try
        {
            var properties = style.Children.Where(child => child.LocalName == "Style.BasedOn" && child.Namespace == Presentation).ToArray();
            if (properties.Length > 0) return XamlResourceLookup.Unavailable("Object-element Style.BasedOn needs additional ambient resource analysis.");
            if (style.Attribute("BasedOn") is not { } basedOn) return XamlResourceLookup.Missing;
            var extension = ParseExtension(basedOn.Value.Text);
            if (extension is { IsComplete: true } && IsExtension(style, extension, Language, "Null")) return XamlResourceLookup.Missing;
            if (extension is not { IsComplete: true } || !IsExtension(style, extension, Presentation, "StaticResource"))
                return XamlResourceLookup.Unavailable("Style.BasedOn requires runtime resource lookup.");
            // Searching from the style would recursively consult its own BasedOn. The
            // style object is obtained in its declaration's containing ambient scope.
            var name = extension.Argument("ResourceKey") ?? extension.Positional;
            if (name is null || style.Parent is null) return XamlResourceLookup.Unavailable("The base style resource is not known.");
            var resolved = Lookup(style.Parent, Unquote(name.Value), depth + 1, style);
            if (resolved.Value is not { } baseStyle || !PresentationElement(baseStyle, "Style"))
                return XamlResourceLookup.Unavailable(resolved.Reason);
            if (baseStyle.Attribute("Resources") is not null || HasBaseOverride(baseStyle)
                || baseStyle.Children.Any(child => !XamlSchemaService.IgnoredElement(child) && child.LocalName.EndsWith(".Resources", StringComparison.Ordinal)
                    && !IsResourcesProperty(baseStyle, child)))
                return XamlResourceLookup.Unavailable("The base style Resources value or URI base is not statically known.");
            var resources = baseStyle.Children.Where(child => IsResourcesProperty(baseStyle, child)).Take(2).ToArray();
            if (resources.Length > 1) return XamlResourceLookup.Unavailable("The base style declares multiple Resources properties.");
            if (resources.Length == 1)
            {
                var found = SearchDictionary(resources[0], key, reference, depth + 1);
                if (found.Value is not null || found.Unknown) return found;
            }
            return SearchBasedOn(baseStyle, key, reference, depth + 1);
        }
        finally { _styles.Remove((style, key)); }
    }

    private (XamlResourceDocument? Document, string? Error) ResolveDocument(Element owner, string value)
    {
        if (context is null) return (null, "External resources require an evaluated project snapshot.");
        if (!context.IsComplete) { CoverageLimited = true; return (null, context.Status ?? "The evaluated resource inventory is incomplete."); }
        if (_schema.HasBaseOverride(owner)) return (null, "The resource URI base is overridden by xml:base.");
        var origin = Origin(owner);
        if (origin is null) return (null, "The declaring document has no unique evaluated resource URI.");
        var resolved = context.Index.ResolveSource(origin, value, context.SourceAssembly, token);
        CoverageLimited |= resolved.CoverageLimited;
        return (resolved.Document, resolved.Status);
    }
    private (Element? Root, string? Error) Load(XamlResourceDocument document)
    {
        if (_loaded.TryGetValue(document, out var cached)) return cached;
        if (!Spend() || _loaded.Count >= 512) { CoverageLimited = true; return (null, "The resource document budget was reached."); }
        var captured = context?.Captured(document) ?? (context is null ? document : null);
        if (captured is null)
        { CoverageLimited = true; return _loaded[document] = (null, "The evaluated resource identity was not captured within this operation's dependency budget."); }
        _usedDocuments.Add(captured);
        if (captured.Text is not { } text) return _loaded[document] = (null, captured.Status ?? "The resource document is unavailable.");
        if (text.Length > 1_000_000 || (long)_characters + text.Length > 8_000_000)
        { CoverageLimited = true; return _loaded[document] = (null, "The resource text budget was reached."); }
        _characters += text.Length;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
            while (reader.Read())
            {
                if (!Spend() || reader.Depth > 256) { CoverageLimited = true; return _loaded[document] = (null, "The resource syntax budget was reached."); }
            }
        }
        catch (XmlException) { return _loaded[document] = (null, "The resource document is not well formed."); }
        var elements = Read(text, token);
        var root = elements.FirstOrDefault();
        if (root is null) return _loaded[document] = (null, "The resource document has no root element.");
        _origins[root] = document;
        return _loaded[document] = (root, null);
    }

    private XamlResourceDocument? Origin(Element element)
    {
        var root = Root(element);
        if (root is null) return null;
        if (_origins.TryGetValue(root, out var origin)) return origin;
        if (context is null) return _origins[root] = null;
        return _origins[root] = context.Index.FindDocument(context.SourcePath, context.SourceAssembly).Document;
    }

    private bool IsResourcesProperty(Element owner, Element property)
    {
        if (XamlSchemaService.IgnoredElement(property) || !property.LocalName.EndsWith(".Resources", StringComparison.Ordinal)) return false;
        var declaring = _types.Resolve(property, property.Name[..property.Name.LastIndexOf('.')], DeclaringAssembly(property));
        var actual = _types.Resolve(owner, owner.Name, DeclaringAssembly(owner));
        if (declaring is null || actual is null || !SchemaMembers.DerivesFrom(actual, declaring)) return false;
        var member = SchemaMembers.Find(declaring, "Resources");
        if (member is { IsEvent: false, ValueType: INamedTypeSymbol valueType }) return IsFrameworkType(valueType, "System.Windows.ResourceDictionary");
        // Framework Resources wrappers are well-known even in a reduced reference
        // schema. A custom same-named member with a different type was rejected above.
        for (var type = declaring; type is not null; type = type.BaseType)
            if (IsFrameworkType(type, "System.Windows.FrameworkElement") || IsFrameworkType(type, "System.Windows.FrameworkContentElement")
                || IsFrameworkType(type, "System.Windows.Application") || IsFrameworkType(type, "System.Windows.Style") || IsFrameworkType(type, "System.Windows.FrameworkTemplate")) return true;
        return false;
    }

    private bool Spend() { token.ThrowIfCancellationRequested(); return ++_work <= MaximumWork; }
    private XamlResourceLookup Limited() { CoverageLimited = true; return XamlResourceLookup.Unavailable("The resource lookup budget was reached."); }
    private static bool HasBaseOverride(Element element) => element.Attributes.Any(attribute => attribute.Name == "xml:base");
    private bool IsMergeProperty(Element element)
        => _schema.IsMergeProperty(element, DeclaringAssembly(element), allowReduced: context is null);
    private bool PresentationElement(Element element, string name)
        => _schema.IsElement(element, name, DeclaringAssembly(element), allowReduced: context is null);
    private bool IsFrameworkType(INamedTypeSymbol? type, string metadataName)
        => _schema.IsFrameworkType(type, metadataName);
    private static string Short(string text) => text.Length <= 256 ? text : text[..256] + "…";
    private static Element? Root(Element element)
    {
        for (int depth = 0; depth < 256; depth++) { if (element.Parent is null) return element; element = element.Parent; }
        return null;
    }
    private static bool SameRoot(Element first, Element second) => Root(first) is { } root && ReferenceEquals(root, Root(second));
    internal static bool LiteralKey(string value, out string key)
    {
        key = value;
        if (key.StartsWith("{}", StringComparison.Ordinal)) key = key[2..];
        else if (key.StartsWith('{')) return false;
        return key.Length > 0 && key.Length <= 4096;
    }
    private static string Unquote(string value) => value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0] ? value[1..^1] : value;
}
