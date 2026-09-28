using System.Globalization;
using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

/// <summary>One bounded, compiler-backed index of authored names; runtime outer-scope lookup is not inferred.</summary>
internal sealed class XamlNameScopeIndex
{
    internal const int MaximumElements = 32768;
    internal const int MaximumNames = 8192;
    internal const int MaximumCharacters = 1_000_000;
    private readonly CancellationToken _token;
    private readonly SchemaTypeResolver _types;
    private readonly XamlResourceSchema _framework;
    private readonly string? _assembly;
    private readonly bool _hasFramework;
    private readonly INamedTypeSymbol? _dependencyObject;
    private readonly INamedTypeSymbol? _nameScope;
    private readonly Dictionary<Element, Scope> _scopes = [];
    private readonly Dictionary<Element, Declaration[]> _declarations = [];
    private readonly Dictionary<INamedTypeSymbol, string?> _runtimeNames = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<Element, INamedTypeSymbol?> _elementTypes = [];
    private readonly Dictionary<string, INamedTypeSymbol?> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _missingBindings = new(StringComparer.Ordinal);
    private readonly List<Declaration> _names = [];

    internal sealed class Scope(Element owner, string kind, bool canResolve, bool canReportMissing)
    {
        internal Element Owner { get; } = owner;
        internal string Kind { get; } = kind;
        internal bool CanResolve { get; } = canResolve;
        internal bool CanReportMissing { get; } = canReportMissing;
        internal bool UnknownNames { get; set; }
        internal Dictionary<string, List<Declaration>> Names { get; } = new(StringComparer.Ordinal);
    }

    internal sealed record Declaration(Element Element, XamlSyntax.Attribute Attribute, string Name, int Start, int Length,
        INamedTypeSymbol? Type, Scope Scope, bool Valid, bool Conflict);

    internal bool IsComplete { get; private set; } = true;
    internal string? Status { get; private set; }
    internal IReadOnlyList<Declaration> Declarations => _names;
    internal IReadOnlyList<Element> Elements { get; }

    internal XamlNameScopeIndex(Element root, Compilation compilation, CancellationToken token = default, string? declaringAssembly = null)
        : this(Collect(root, token), compilation, token, declaringAssembly) { }

    internal XamlNameScopeIndex(IReadOnlyList<Element> elements, Compilation compilation, CancellationToken token = default, string? declaringAssembly = null)
    {
        _token = token; _assembly = declaringAssembly;
        _hasFramework = compilation.SourceModule.ReferencedAssemblySymbols.Any(assembly => assembly.Identity.Name == "PresentationFramework");
        _types = new(compilation, token); _framework = new(compilation, token);
        var dependencyObjects = compilation.SourceModule.ReferencedAssemblySymbols
            .Where(assembly => assembly.Identity.Name == "WindowsBase")
            .Select(assembly => assembly.GetTypeByMetadataName("System.Windows.DependencyObject"))
            .OfType<INamedTypeSymbol>().Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Take(2).ToArray();
        _dependencyObject = dependencyObjects.Length == 1 ? dependencyObjects[0] : null;
        // NameScope is a WindowsBase type. Include framework forwarders without
        // confusing its identity with types declared in PresentationFramework.
        var nameScopes = compilation.SourceModule.ReferencedAssemblySymbols
            .Where(assembly => assembly.Identity.Name is "WindowsBase" or "PresentationFramework")
            .Select(assembly => assembly.GetTypeByMetadataName("System.Windows.NameScope")
                ?? assembly.ResolveForwardedType("System.Windows.NameScope"))
            .OfType<INamedTypeSymbol>().Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Take(2).ToArray();
        _nameScope = nameScopes.Length == 1 ? nameScopes[0]
            : nameScopes.Length == 0 && !_hasFramework ? compilation.GetTypeByMetadataName("System.Windows.NameScope") : null;
        foreach (string name in new[] { "RuntimeNamePropertyAttribute", "NameScopePropertyAttribute", "INameScope" })
        {
            string fullName = "System.Windows.Markup." + name;
            var assemblies = compilation.SourceModule.ReferencedAssemblySymbols.Where(assembly => assembly.Identity.Name is "System.Xaml" or "WindowsBase").ToArray();
            var matches = assemblies.Select(assembly => assembly.GetTypeByMetadataName(fullName)).OfType<INamedTypeSymbol>()
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Take(2).ToArray();
            // Some reduced compilations include the platform WindowsBase facade,
            // which contains none of this metadata. Keep their unique source
            // schema usable without permitting source lookalikes to replace the
            // canonical metadata in an actual WPF compilation.
            _metadata[fullName] = matches.Length == 1 ? matches[0]
                : matches.Length == 0 && !_hasFramework ? compilation.GetTypeByMetadataName(fullName) : null;
        }
        Elements = elements;
        token.ThrowIfCancellationRequested();
        if (elements.Count > MaximumElements) { Limit(); return; }
        var depths = new Dictionary<Element, int>();
        int attributes = 0;
        // Check depth before any recursive lexical namespace/ignored lookup.
        foreach (var element in elements)
        {
            token.ThrowIfCancellationRequested();
            int depth = element.Parent is null ? 0 : depths.GetValueOrDefault(element.Parent, 256) + 1;
            if (depth > 256 || (attributes += element.Attributes.Count) > 65536) { Limit(); return; }
            depths[element] = depth;
        }
        foreach (var element in elements)
        {
            token.ThrowIfCancellationRequested();
            if (XamlSchemaService.IgnoredElement(element) || InRawData(element)) continue;
            Scope? inherited = element.Parent is null ? null : _scopes.GetValueOrDefault(element.Parent);
            INamedTypeSymbol? type = element.LocalName.Contains('.') ? null : _types.Resolve(element, element.Name, _assembly);
            if (element.Parent is null && element.Attribute(Language, "Class") is { } rootClass)
            {
                var actual = _types.ResolveMetadataName(rootClass.Value.Text.Trim(), _assembly);
                type = type is not null && actual is not null && SchemaMembers.IsComplete(actual) && SchemaMembers.DerivesFrom(actual, type) ? actual : null;
            }
            _elementTypes[element] = type;
            Scope scope;
            bool frameworkTemplate = IsType(type, "System.Windows.FrameworkTemplate", derived: true);
            bool template = new[] { "System.Windows.FrameworkTemplate", "System.Windows.Controls.ControlTemplate", "System.Windows.DataTemplate",
                    "System.Windows.HierarchicalDataTemplate", "System.Windows.Controls.ItemsPanelTemplate" }
                .Any(name => IsType(type, name));
            if (ExplicitScope(element))
                scope = new(element, "custom namescope", false, false);
            else if (template)
                scope = new(element, "template", true, false);
            else if (IsType(type, "System.Windows.Style", derived: true))
                scope = new(element, "style", false, false);
            else if (IsType(type, "System.Windows.ResourceDictionary", derived: true) || ResourceProperty(element))
                scope = new(element, "resources", false, false);
            else if (frameworkTemplate || CustomScope(type))
                scope = new(element, "custom namescope", false, false);
            else if (!element.LocalName.Contains('.') && (type is null || !SchemaMembers.IsComplete(type)))
            {
                if (inherited is not null) inherited.UnknownNames = true;
                scope = new(element, "unresolved type boundary", false, false);
            }
            else if (element.Parent is null)
            {
                bool ordinary = IsType(type, "System.Windows.FrameworkElement", true) || IsType(type, "System.Windows.FrameworkContentElement", true);
                scope = new(element, "page", ordinary, ordinary);
            }
            else if (inherited is null) continue;
            else scope = inherited;
            _scopes[element] = scope;
            // A resource object is not a name registered in its containing page.
            // Templates nested in resources establish their independent local scope above.
            if (scope.Kind == "resources" || element.LocalName.Contains('.')) continue;
            if (template)
            {
                // The template object and its instantiated content have different
                // registration contexts. Do not assign the object's own name to its
                // child-name table or guess its enclosing registration context.
                if (element.Attribute(Language, "Name") is not null && inherited is not null) inherited.UnknownNames = true;
                continue;
            }
            if (type is null || !SchemaMembers.IsComplete(type))
            {
                if (element.Attribute(Language, "Name") is not null || element.Attribute("Name") is not null)
                    scope.UnknownNames = true;
                continue;
            }
            string? runtimeName = RuntimeName(type);
            if (runtimeName is not null && (element.Children.Any(child => RuntimeNameProperty(child, type, runtimeName))
                || element.Attributes.Any(IsQualifiedRuntimeNameAttribute))) scope.UnknownNames = true;
            var declared = element.Attributes.Where(attribute =>
                IsDirective(attribute, "Name") || runtimeName is not null && attribute.Name == runtimeName).ToArray();
            if (declared.Length == 0) continue;
            bool conflict = declared.Length > 1;
            var rows = new List<Declaration>();
            foreach (var attribute in declared)
            {
                string name = attribute.Value.Text;
                // A runtime-name markup extension is unresolved, not a literal identifier.
                if (!IsDirective(attribute, "Name") && name.TrimStart().StartsWith('{')) { scope.UnknownNames = true; continue; }
                if (name.Length == 0) continue; // An empty field is normal while typing; Name also defaults to empty.
                if (name.Length > 512) { Limit(); return; }
                if (_names.Count >= MaximumNames) { Limit(); return; }
                var span = attribute.Value.Span(0, name.Length);
                var declaration = new Declaration(element, attribute, name, span.Start, span.Length, type, scope, ValidName(name), conflict);
                rows.Add(declaration); _names.Add(declaration);
                if (declaration.Valid)
                {
                    if (!scope.Names.TryGetValue(name, out var matches)) scope.Names[name] = matches = [];
                    matches.Add(declaration);
                }
            }
            _declarations[element] = rows.ToArray();
        }
    }

    internal Element? Resolve(Element owner, string name) => ResolveDeclaration(owner, name)?.Element;
    internal Declaration? ResolveDeclaration(Element owner, string name)
    {
        _token.ThrowIfCancellationRequested();
        name = name.Trim();
        if (!IsComplete || !ValidName(name) || !_scopes.TryGetValue(owner, out var scope) || !scope.CanResolve || scope.UnknownNames) return null;
        return scope.Names.TryGetValue(name, out var matches) && matches.Count == 1 && !matches[0].Conflict ? matches[0] : null;
    }
    internal XamlSyntax.Attribute? GetDeclaration(Element element) => IsComplete && _declarations.TryGetValue(element, out var values)
        && values is [var value] && value.Valid && !value.Conflict && value.Scope.CanResolve && !value.Scope.UnknownNames
        && value.Scope.Names[value.Name].Count == 1 ? value.Attribute : null;
    internal IReadOnlyList<Declaration> Candidates(Element owner)
    {
        if (!IsComplete || !_scopes.TryGetValue(owner, out var scope) || !scope.CanResolve || scope.UnknownNames) return [];
        return scope.Names.Values.Where(values => values.Count == 1 && !values[0].Conflict).Select(values => values[0]).ToArray();
    }
    internal bool MayReportMissing(Element owner, string name) => IsComplete && ValidName(name)
        && _scopes.TryGetValue(owner, out var scope) && scope.CanReportMissing && !scope.UnknownNames && !scope.Names.ContainsKey(name);
    internal string ScopeDescription(Element owner) => _scopes.TryGetValue(owner, out var scope) ? scope.Kind : "unavailable namescope";
    internal Scope? GetScope(Element owner) => _scopes.GetValueOrDefault(owner);
    internal INamedTypeSymbol? GetElementType(Element element) => _elementTypes.GetValueOrDefault(element);
    internal string? GetRuntimeNameProperty(Element element) => GetElementType(element) is { } type ? RuntimeName(type) : null;
    internal bool IsRuntimeNamePropertyElement(Element element) => element.Parent is { } parent
        && GetElementType(parent) is { } type && RuntimeName(type) is { } name && RuntimeNameProperty(element, type, name);
    internal bool IsQualifiedRuntimeNameAttribute(XamlSyntax.Attribute attribute)
    {
        int dot = attribute.Name.LastIndexOf('.');
        if (dot < 1 || GetElementType(attribute.Owner) is not { } type || RuntimeName(type) is not { } name
            || attribute.Name[(dot + 1)..] != name) return false;
        var owner = _types.Resolve(attribute.Owner, attribute.Name[..dot], _assembly);
        var declared = owner is null ? null : SchemaMembers.Find(owner, name);
        var actual = SchemaMembers.Find(type, name);
        return owner is not null && SchemaMembers.DerivesFrom(type, owner) && declared is not null && actual is not null
            && SymbolEqualityComparer.Default.Equals(declared.Symbol, actual.Symbol);
    }
    internal bool IsFrameworkType(INamedTypeSymbol? type, string metadataName) => IsType(type, metadataName);
    internal bool IsBinding(Element element)
    {
        var type = _elementTypes.GetValueOrDefault(element);
        return IsType(type, "System.Windows.Data.Binding") || !_hasFramework && type is null
            && element.LocalName == "Binding" && element.Namespace is null or Presentation && MissingBindingMapping(element, element.Name);
    }
    internal bool IsBindingExtension(Element owner, Extension extension)
    {
        var type = _types.Resolve(owner, extension.Name, _assembly);
        return IsType(type, "System.Windows.Data.Binding") || !_hasFramework && type is null && IsExtension(owner, extension, Presentation, "Binding")
            && MissingBindingMapping(owner, extension.Name);
    }
    private bool MissingBindingMapping(Element owner, string name)
    {
        string? uri = owner.LookupNamespace(SplitName(name).Prefix);
        if (uri is null) return true;
        uri = SchemaTypeResolver.WithDeclaringAssembly(uri, _assembly);
        if (_missingBindings.TryGetValue(uri, out bool missing)) return missing;
        int examined = 0;
        foreach (var type in _types.Types(uri))
        {
            _token.ThrowIfCancellationRequested();
            if (++examined > 4096 || type.Name == "Binding") return _missingBindings[uri] = false;
        }
        return _missingBindings[uri] = true;
    }

    private string? RuntimeName(INamedTypeSymbol type)
    {
        if (_runtimeNames.TryGetValue(type, out var saved)) return saved;
        string? result = null;
        int depth = 0;
        for (var current = type; current is not null && depth++ < 64; current = current.BaseType)
        {
            _token.ThrowIfCancellationRequested();
            var attributes = current.GetAttributes().Where(attribute => Metadata(attribute.AttributeClass, "RuntimeNamePropertyAttribute")).Take(2).ToArray();
            if (attributes.Length == 0) continue;
            if (attributes.Length == 1 && attributes[0].ConstructorArguments is [{ Value: string name }])
            {
                var member = SchemaMembers.Find(type, name);
                if (member is { CanWrite: true, Symbol: IPropertySymbol { IsStatic: false, GetMethod.DeclaredAccessibility: Accessibility.Public }, ValueType.SpecialType: SpecialType.System_String }) result = name;
            }
            return _runtimeNames[type] = result;
        }
        // Supports reduced semantic fixtures while still requiring the real framework
        // base identity whenever PresentationFramework is available.
        if (IsType(type, "System.Windows.FrameworkElement", true) || IsType(type, "System.Windows.FrameworkContentElement", true))
        {
            if (SchemaMembers.Find(type, "Name") is { CanWrite: true, ValueType.SpecialType: SpecialType.System_String }) result = "Name";
        }
        return _runtimeNames[type] = result;
    }
    private bool IsType(INamedTypeSymbol? type, string metadataName, bool derived = false)
    {
        int depth = 0;
        for (var current = type; current is not null && depth++ < 64; current = derived ? current.BaseType : null)
            if (_framework.IsFrameworkType(current, metadataName)) return true;
        return false;
    }
    private bool CustomScope(INamedTypeSymbol? type)
    {
        if (type is null) return false;
        if (type.AllInterfaces.Any(item => Metadata(item, "INameScope"))) return true;
        int depth = 0;
        for (var current = type; current is not null && depth++ < 64; current = current.BaseType)
            if (current.GetAttributes().Any(attribute => Metadata(attribute.AttributeClass, "NameScopePropertyAttribute")
                && !StandardNameScopeCarrier(current, attribute))) return true;
        return false;
    }
    private bool StandardNameScopeCarrier(INamedTypeSymbol type, AttributeData attribute) =>
        // WPF declares its ordinary attached NameScope carrier on DependencyObject.
        // That metadata makes a scope possible; an actual NameScope assignment is
        // the boundary, handled by ExplicitScope. An application declaration of
        // the same attribute (even with the same arguments) remains a custom scope.
        _dependencyObject is not null && SymbolEqualityComparer.Default.Equals(type, _dependencyObject)
        && attribute.ConstructorArguments is [{ Value: "NameScope" }, { Value: INamedTypeSymbol owner }]
        && IsNameScope(owner);
    private bool IsNameScope(INamedTypeSymbol? type) => type is not null && _nameScope is not null
        && SymbolEqualityComparer.Default.Equals(type, _nameScope);
    private bool Metadata(INamedTypeSymbol? type, string name) => type is not null
        && _metadata.TryGetValue("System.Windows.Markup." + name, out var expected) && expected is not null
        && SymbolEqualityComparer.Default.Equals(type, expected);
    private bool RuntimeNameProperty(Element property, INamedTypeSymbol type, string name)
    {
        int dot = property.Name.LastIndexOf('.');
        if (dot < 1 || property.Name[(dot + 1)..] != name) return false;
        var owner = _types.Resolve(property, property.Name[..dot], _assembly);
        return owner is not null && SchemaMembers.DerivesFrom(type, owner) && RuntimeName(owner) == name;
    }
    private bool ExplicitScope(Element element)
    {
        bool NamedScope(Element owner, string name)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 && name[(dot + 1)..] == "NameScope" &&
                IsNameScope(_types.Resolve(owner, name[..dot], _assembly));
        }
        return element.Attributes.Any(attribute => NamedScope(element, attribute.Name))
            || element.Children.Any(child => NamedScope(child, child.Name));
    }
    private bool ResourceProperty(Element element)
    {
        int dot = element.Name.LastIndexOf('.');
        if (dot < 1 || element.Name[(dot + 1)..] != "Resources") return false;
        var owner = _types.Resolve(element, element.Name[..dot], _assembly);
        return owner is not null && SchemaMembers.Find(owner, "Resources")?.ValueType is INamedTypeSymbol type
            && IsType(type, "System.Windows.ResourceDictionary", true);
    }
    private static bool InRawData(Element element)
    {
        for (Element? current = element; current is not null; current = current.Parent)
            if (current.Namespace == Language && current.LocalName is "XData" or "Code") return true;
        return false;
    }
    internal static bool IsDirective(XamlSyntax.Attribute attribute, string name)
    {
        var qualified = SplitName(attribute.Name);
        return qualified.Prefix.Length > 0 && qualified.Local == name && attribute.Owner.LookupNamespace(qualified.Prefix) == Language;
    }
    internal static bool ValidName(string name)
    {
        if (name.Length is < 1 or > 512) return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            var category = char.GetUnicodeCategory(c);
            bool start = c == '_' || category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
            if (!start && (i == 0 || category is not (UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ModifierLetter))) return false;
        }
        return true;
    }
    private void Limit() { IsComplete = false; Status = "XAML namescope analysis reached its element, depth, attribute, or name budget; name resolution was withheld."; }
    private static IReadOnlyList<Element> Collect(Element root, CancellationToken token)
    {
        var result = new List<Element>();
        var pending = new Stack<Element>(); pending.Push(root);
        while (pending.TryPop(out var element))
        {
            token.ThrowIfCancellationRequested(); result.Add(element);
            if (result.Count > MaximumElements) break;
            for (int index = element.Children.Count - 1; index >= 0; index--) pending.Push(element.Children[index]);
        }
        return result;
    }
}
