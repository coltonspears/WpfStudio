using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed partial class XamlLanguageService
{
    private sealed partial class SourceResolver(Compilation compilation, CancellationToken token, XamlResourceContext? resources = null)
    {
        private readonly Dictionary<Element, BindingSourceInfo> _contexts = [];
        private readonly HashSet<Element> _resolving = [];
        private readonly SchemaTypeResolver _types = new(compilation, token);
        private readonly XamlResourceGraph _resources = new(compilation, resources, token);
        private readonly HashSet<Element> _resourceValues = [];
        private readonly Dictionary<Element, XamlNameScopeIndex> _nameScopes = [];
        public bool ResourceCoverageLimited => _resources.CoverageLimited;
        public IReadOnlyList<XamlResourceDocument> UsedResourceDocuments => _resources.UsedDocuments;

        public IEnumerable<BindingStep> Walk(XamlSyntax.Attribute attribute, BindingPath binding, IReadOnlyList<Segment> segments)
        {
            var source = BindingSource(attribute, binding);
            foreach (var segment in segments)
            {
                token.ThrowIfCancellationRequested();
                var step = Step(source, segment, attribute.Owner);
                yield return step;
                if (step.Result.Type is null) yield break;
                source = step.Result;
            }
        }
        public BindingSourceInfo Follow(BindingSourceInfo source, IReadOnlyList<Segment> segments, Element syntaxOwner)
        {
            foreach (var segment in segments)
            {
                token.ThrowIfCancellationRequested();
                source = Step(source, segment, syntaxOwner).Result;
                if (source.Type is null) break;
            }
            return source;
        }
        private BindingStep Step(BindingSourceInfo source, Segment segment, Element syntaxOwner, bool allowCurrency = true)
        {
            BindingStep Unknown(string reason) => new(segment, source, BindingSourceInfo.Unknown(reason));
            if (segment.Kind == SegmentKind.QualifiedProperty) return QualifiedStep(source, segment, syntaxOwner);
            if (segment.Kind == SegmentKind.CurrentItem)
                return new(segment, source, new BindingSourceInfo(ItemType(source.Type), null, source.Reason + " → collection item"));
            if (segment.Kind == SegmentKind.Indexer)
            {
                if (source.Type is IArrayTypeSymbol array)
                    return int.TryParse(segment.Name.Trim(), out _) ? new(segment, source, new BindingSourceInfo(array.ElementType, null, source.Reason)) : Unknown("The array index is not a simple integer.");
                var indexType = Normalize(source.Type);
                if (indexType is null) return Unknown(source.Reason);
                var argument = segment.Name.Trim();
                var quoted = argument.Length >= 2 && argument[0] is '\'' or '"' && argument[^1] == argument[0];
                if (argument.Length == 0 || argument.Contains(',') || argument.Contains('^')) return Unknown("This indexer expression needs runtime resolution.");
                var numeric = !quoted && int.TryParse(argument, out _);
                var candidates = TypeHierarchy(indexType).SelectMany(t => t.GetMembers().OfType<IPropertySymbol>())
                    .Where(p => p is { IsIndexer: true, IsStatic: false, DeclaredAccessibility: Accessibility.Public, GetMethod.DeclaredAccessibility: Accessibility.Public }
                        && p.Parameters.Length == 1 && (p.Parameters[0].Type.SpecialType == SpecialType.System_String
                            || numeric && p.Parameters[0].Type.SpecialType == SpecialType.System_Int32))
                    .DistinctBy(p => p.Parameters[0].Type.SpecialType).ToArray();
                if (candidates.Length != 1) return Unknown("The indexer overload cannot be determined statically.");
                return new(segment, source, new BindingSourceInfo(candidates[0].Type, null, source.Reason), candidates[0]);
            }
            var type = source.Type is IArrayTypeSymbol ? compilation.GetSpecialType(SpecialType.System_Array) : Normalize(source.Type);
            if (type is null) return Unknown(source.Reason);
            var members = Properties(type).ToArray();
            var member = members.FirstOrDefault(p => p.Name == segment.Name);
            if (member is null && allowCurrency && ItemType(source.Type) is { } item && !SymbolEqualityComparer.Default.Equals(item, source.Type))
                return Step(new BindingSourceInfo(item, null, source.Reason + " → collection item"), segment, syntaxOwner, allowCurrency: false);
            if (member is null)
            {
                if (HasUnavailableGeneratedMembers(type) || IsCollectionSource(type)) return Unknown("The source's runtime or generated member shape is unavailable.");
                return new(segment, source with { Type = type }, BindingSourceInfo.Unknown("The binding path has a missing member."),
                    IsMissing: true, Suggestion: Suggest(segment.Name, members.Select(p => p.Name)));
            }
            if (member.Name == "DataContext" && member.ContainingType.ToDisplayString() is "System.Windows.FrameworkElement" or "System.Windows.FrameworkContentElement"
                && source.Element is { } element)
            {
                var context = Context(element, 0);
                return new(segment, source, context with { Reason = source.Reason + " → " + context.Reason }, member);
            }
            return new(segment, source, new BindingSourceInfo(member.Type, null, source.Reason), member);
        }
        private static IEnumerable<INamedTypeSymbol> TypeHierarchy(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType) yield return current;
            if (type.TypeKind == TypeKind.Interface)
                foreach (var @interface in type.AllInterfaces) yield return @interface;
        }

        public BindingSourceInfo BindingSource(XamlSyntax.Attribute attribute, BindingPath binding)
        {
            var owner = binding.ContextOwner;
            if (Option(attribute, binding, "XPath") is not null) return BindingSourceInfo.Unknown("XPath bindings require runtime XML data.");
            var explicitSources = new[] { "Source", "ElementName", "RelativeSource" }.Where(name => HasOption(attribute, binding, name)).ToArray();
            if (explicitSources.Length > 1) return BindingSourceInfo.Unknown("The binding declares multiple source selectors.");
            if (explicitSources.Length == 1)
            {
                var kind = explicitSources[0];
                var value = Option(attribute, binding, kind);
                if (kind == "ElementName")
                {
                    if (!HasElementNameSource(attribute, binding))
                        return BindingSourceInfo.Unknown("The ElementName binding declaration is not uniquely recognized.");
                    var name = value is null ? null : binding.Extension is not null ? Unquote(value.Trim()).Trim() : value.Trim();
                    var named = name is null ? null : FindNamedElement(owner, name);
                    return named is null ? BindingSourceInfo.Unknown($"ElementName '{value}' is not uniquely available in this namescope.")
                        : ElementSource(named, $"ElementName '{name}'");
                }
                if (kind == "Source")
                {
                    if (value is not null) return SourceValue(attribute.Owner, owner, value);
                    var property = attribute.Owner.Children.FirstOrDefault(c => c.LocalName == "Binding.Source");
                    return property?.Children.Count == 1 ? ObjectSource(property.Children[0], "Binding.Source object") : BindingSourceInfo.Unknown("Binding.Source is not a known object.");
                }
                if (value is not null) return RelativeSource(attribute.Owner, owner, value);
                var relativeProperty = attribute.Owner.Children.FirstOrDefault(c => c.LocalName == "Binding.RelativeSource");
                var relative = relativeProperty?.Children.Count == 1 ? relativeProperty.Children[0] : null;
                return relative is null ? BindingSourceInfo.Unknown("RelativeSource is not statically known.") : RelativeSourceObject(relative, owner);
            }
            // The expression assigning DataContext sees the context entering that element.
            if (attribute.Name == "DataContext") return Context(owner.Parent, 0);
            for (var current = attribute.Owner.Parent; current is not null && current != owner; current = current.Parent)
                if (current.LocalName.EndsWith(".DataContext", StringComparison.Ordinal)) return Context(owner.Parent, 0);
            return Context(owner, 0);
        }
        private static string? Option(XamlSyntax.Attribute attribute, BindingPath binding, string name) =>
            binding.Extension?.Argument(name)?.Value ?? (IsPresentationElement(attribute.Owner, "Binding") ? attribute.Owner.Attribute(name)?.Value.Text : null);
        private static bool HasOption(XamlSyntax.Attribute attribute, BindingPath binding, string name) => Option(attribute, binding, name) is not null
            || IsPresentationElement(attribute.Owner, "Binding") && attribute.Owner.Children.Any(c => c.LocalName == "Binding." + name);

        public bool HasElementNameSource(XamlSyntax.Attribute attribute, BindingPath binding)
        {
            if (HasOption(attribute, binding, "Source") || HasOption(attribute, binding, "RelativeSource")) return false;
            var index = Names(attribute.Owner);
            return binding.Extension is { } extension
                ? index.IsBindingExtension(attribute.Owner, extension) && extension.Arguments.Count(argument => argument.Name == "ElementName") == 1
                : index.IsBinding(attribute.Owner) && attribute.Owner.Attributes.Count(item => item.Name == "ElementName") == 1;
        }

        private BindingSourceInfo SourceValue(Element syntaxOwner, Element owner, string value)
        {
            var extension = ParseExtension(value);
            if (extension is null) return new BindingSourceInfo(compilation.GetSpecialType(SpecialType.System_String), null, "Literal Binding.Source");
            if (!extension.IsComplete) return BindingSourceInfo.Unknown("Binding.Source is unfinished.");
            if (IsExtension(syntaxOwner, extension, Presentation, "StaticResource"))
            {
                var key = extension.Argument("ResourceKey")?.Value ?? extension.Positional?.Value;
                return key is null ? BindingSourceInfo.Unknown("StaticResource has no declared resource key.") : ResourceSource(owner, Unquote(key));
            }
            if (IsExtension(syntaxOwner, extension, Language, "Reference"))
            {
                var name = extension.Argument("Name")?.Value ?? extension.Positional?.Value;
                var named = name is null ? null : FindReferenceElement(owner, Unquote(name));
                return named is null ? BindingSourceInfo.Unknown("The x:Reference target is unknown.") : ElementSource(named, $"x:Reference '{Unquote(name!)}'");
            }
            return BindingSourceInfo.Unknown("This Binding.Source markup extension needs runtime evaluation.");
        }
        private BindingSourceInfo RelativeSource(Element syntaxOwner, Element owner, string value)
        {
            var extension = ParseExtension(value);
            if (extension is null || !extension.IsComplete || !IsExtension(syntaxOwner, extension, Presentation, "RelativeSource"))
                return BindingSourceInfo.Unknown("RelativeSource is not statically known.");
            return RelativeSourceCore(syntaxOwner, owner, extension.Argument("Mode")?.Value ?? extension.Positional?.Value,
                extension.Argument("AncestorType")?.Value, extension.Argument("AncestorLevel")?.Value);
        }
        private BindingSourceInfo RelativeSourceObject(Element relative, Element owner) => IsPresentationElement(relative, "RelativeSource")
            ? RelativeSourceCore(relative, owner, relative.Attribute("Mode")?.Value.Text, relative.Attribute("AncestorType")?.Value.Text, relative.Attribute("AncestorLevel")?.Value.Text)
            : BindingSourceInfo.Unknown("The relative source object is not recognized.");
        private BindingSourceInfo RelativeSourceCore(Element syntaxOwner, Element owner, string? mode, string? ancestorName, string? levelText)
        {
            mode = mode?.Trim();
            // A Setter is a declaration, not the runtime binding target. Its binding is
            // attached to the styled control; its lexical ancestors are not visual ancestors.
            var setterTarget = IsPresentationElement(owner, "Setter");
            if (mode == "Self") return setterTarget ? SetterSelf(owner) : ElementSource(owner, "RelativeSource Self");
            if (mode == "TemplatedParent")
            {
                if (setterTarget) return BindingSourceInfo.Unknown("The styled control's TemplatedParent needs runtime context.");
                for (var current = owner.Parent; current is not null; current = current.Parent)
                {
                    if (IsPresentationElement(current, "DataTemplate") || IsPresentationElement(current, "HierarchicalDataTemplate")) break;
                    if (!IsPresentationElement(current, "ControlTemplate")) continue;
                    var target = current.Attribute("TargetType");
                    if (target is not null) return new BindingSourceInfo(ResolveTypeValue(current, target.Value.Text), null, "ControlTemplate.TargetType via TemplatedParent");
                    for (var style = current.Parent; style is not null; style = style.Parent)
                        if (IsPresentationElement(style, "Style")) return new BindingSourceInfo(style.Attribute("TargetType") is { } styleTarget ? ResolveTypeValue(style, styleTarget.Value.Text) : null, null, "Style.TargetType via TemplatedParent");
                    break;
                }
                return BindingSourceInfo.Unknown("A ControlTemplate target type is not available.");
            }
            if (mode is not (null or "FindAncestor") || ancestorName is null) return BindingSourceInfo.Unknown("This relative source needs runtime context.");
            var targetType = ResolveTypeValue(syntaxOwner, ancestorName);
            if (targetType is null) return BindingSourceInfo.Unknown("AncestorType could not be resolved.");
            var level = 1;
            if (levelText is not null && (!int.TryParse(levelText, out level) || level < 1)) return BindingSourceInfo.Unknown("AncestorLevel is not a positive integer.");
            var requestedLevel = level;
            for (var current = setterTarget ? null : owner.Parent; current is not null; current = current.Parent)
            {
                if (IsTemplate(current) || IsPresentationElement(current, "Style") || IsPresentationElement(current, "ResourceDictionary")) break;
                if (current.LocalName.Contains('.')) continue;
                var currentType = ElementType(current);
                if (currentType is not null && TypeHierarchy(currentType).Any(t => SymbolEqualityComparer.Default.Equals(t, targetType)) && --level == 0)
                    return ElementSource(current, $"RelativeSource ancestor {targetType.Name}, level {requestedLevel}");
            }
            // AncestorType is still an explicit source contract even when its runtime instance
            // lies outside this document. Do not infer that instance's DataContext.
            return new BindingSourceInfo(targetType, null, $"RelativeSource AncestorType {targetType.Name}; runtime instance unknown");
        }

        private BindingSourceInfo SetterSelf(Element setter)
        {
            // TargetName can redirect template setters to a named child. Do not assume that
            // target is the Style.TargetType; leave it unknown until the template target is
            // separately proven. Likewise, a template declaration is an inference boundary.
            if (setter.Attribute("TargetName") is not null)
                return BindingSourceInfo.Unknown("The setter's named runtime target is not statically resolved.");
            for (var current = setter.Parent; current is not null; current = current.Parent)
            {
                if (IsTemplate(current)) break;
                if (!IsPresentationElement(current, "Style")) continue;
                return new BindingSourceInfo(current.Attribute("TargetType") is { } target ? ResolveTypeValue(current, target.Value.Text) : null,
                    null, "Style.TargetType via Setter RelativeSource Self");
            }
            return BindingSourceInfo.Unknown("The setter's runtime target type is not declared by a containing style.");
        }

        public Element? FindNamedElement(Element owner, string name)
            => Names(owner).Resolve(owner, name.Trim());

        public XamlSyntax.Attribute? FindNameDeclaration(Element owner, string name)
        {
            var index = Names(owner);
            return index.Resolve(owner, name.Trim()) is { } element ? index.GetDeclaration(element) : null;
        }

        private XamlNameScopeIndex Names(Element owner)
        {
            var root = owner;
            while (root.Parent is not null)
            {
                token.ThrowIfCancellationRequested();
                root = root.Parent;
            }
            if (!_nameScopes.TryGetValue(root, out var index))
                _nameScopes[root] = index = new XamlNameScopeIndex(root, compilation, token, _resources.DeclaringAssembly(root));
            return index;
        }

        // x:Reference uses the object writer's name resolver, not ElementName's
        // runtime lookup. Preserve its existing conservative support separately.
        private Element? FindReferenceElement(Element owner, string name)
        {
            if (name.Length == 0 || name.StartsWith('{')) return null;
            var scope = owner;
            while (scope.Parent is not null && !IsNameScopeBoundary(scope)) scope = scope.Parent;
            var matches = new List<Element>();
            var pending = new Stack<Element>(); pending.Push(scope);
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                if (current != scope && (IsNameScopeBoundary(current) || IsPresentationElement(current, "ResourceDictionary") || current.LocalName.EndsWith(".Resources", StringComparison.Ordinal))) continue;
                if ((current.Attribute(Language, "Name") ?? current.Attribute("Name"))?.Value.Text == name) matches.Add(current);
                if (matches.Count > 1) return null;
                foreach (var child in current.Children) pending.Push(child);
            }
            return matches.SingleOrDefault();
        }
        private static bool IsTemplate(Element element) => IsPresentationElement(element, "DataTemplate") || IsPresentationElement(element, "ControlTemplate")
            || IsPresentationElement(element, "HierarchicalDataTemplate") || IsPresentationElement(element, "ItemsPanelTemplate");
        private static bool IsNameScopeBoundary(Element element) => IsTemplate(element) || IsPresentationElement(element, "Style")
            || element.Attributes.Any(a => a.Name.EndsWith("NameScope.NameScope", StringComparison.Ordinal))
            || element.Children.Any(c => c.LocalName == "NameScope.NameScope");

        private BindingSourceInfo ResourceSource(Element owner, string key)
        {
            var result = _resources.Lookup(owner, key);
            return result.Value is { } value ? ObjectSource(value, result.Reason) : BindingSourceInfo.Unknown(result.Reason);
        }
        private BindingSourceInfo ElementSource(Element element, string reason) => new(ElementType(element), element, reason);
        private BindingSourceInfo ObjectSource(Element element, string reason)
        {
            token.ThrowIfCancellationRequested();
            if (_resourceValues.Count >= 64) { _resources.MarkCoverageLimited(); return BindingSourceInfo.Unknown("The resource alias depth budget was reached."); }
            if (!_resourceValues.Add(element)) return BindingSourceInfo.Unknown("Resource value aliases contain a cycle.");
            try
            {
                if (IsPresentationElement(element, "StaticResource") || IsPresentationElement(element, "StaticResourceExtension"))
                    return element.Attribute("ResourceKey") is { } key ? ResourceSource(element, key.Value.Text) : BindingSourceInfo.Unknown("StaticResource has no literal resource key.");
                if (element.Attribute(Language, "FactoryMethod") is not null || element.Children.Any(child => child.Namespace == Language && child.LocalName == "Arguments"))
                    return BindingSourceInfo.Unknown("The resource value requires factory or constructor-argument evaluation.");
                var type = ElementType(element);
                if (type is not null && TypeHierarchy(type).Any(t => t.ToDisplayString() is "System.Windows.Markup.MarkupExtension" or "System.Windows.Data.DataSourceProvider"))
                    return BindingSourceInfo.Unknown("The resource's runtime-provided value is not statically known.");
                return new BindingSourceInfo(type, element, reason);
            }
            finally { _resourceValues.Remove(element); }
        }
        private INamedTypeSymbol? ElementType(Element element) => element.Attribute(Language, "Class") is { } @class
            ? _types.ResolveMetadataName(@class.Value.Text, _resources.DeclaringAssembly(element)) : ResolveType(element, element.Name);
        private INamedTypeSymbol? ResolveType(Element element, string name) => _types.Resolve(element, name, _resources.DeclaringAssembly(element));
        private INamedTypeSymbol? ResolveTypeValue(Element element, string value) => _types.ResolveTypeValue(element, value, _resources.DeclaringAssembly(element));
        private static string Unquote(string value) => value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0] ? value[1..^1] : value;

        private BindingSourceInfo Context(Element? element, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (element is null || depth > 256) return BindingSourceInfo.Unknown("No declared DataContext is available.");
            if (_contexts.TryGetValue(element, out var cached)) return cached;
            if (!_resolving.Add(element)) return BindingSourceInfo.Unknown("DataContext resolution contains a cycle.");
            try { return _contexts[element] = DetermineContext(element, depth); }
            finally { _resolving.Remove(element); }
        }
        private BindingSourceInfo DetermineContext(Element element, int depth)
        {
            var design = element.Attribute(Design, "DataContext");
            var designProperties = element.Children.Where(c => c.Namespace == Design &&
                (c.LocalName == "DataContext" || c.LocalName.EndsWith(".DataContext", StringComparison.Ordinal))).ToArray();
            if (designProperties.Length > 0)
            {
                // An unsupported local design declaration is still a context boundary: it
                // must never fall back to a different runtime or ancestor DataContext.
                if (design is not null || designProperties.Length != 1) return BindingSourceInfo.Unknown("The local design DataContext has conflicting declarations.");
                var declared = designProperties[0];
                var owner = declared.LocalName[..Math.Max(0, declared.LocalName.LastIndexOf('.'))];
                var declaredOwner = ElementType(element) is { } elementType ? TypeHierarchy(elementType).FirstOrDefault(t => t.Name == owner) : null;
                var ownerKnown = declaredOwner is not null && Properties(declaredOwner).Any(p => p.Name == "DataContext" && p.SetMethod?.DeclaredAccessibility == Accessibility.Public);
                if (!ownerKnown || !declared.IsClosed || declared.HasSignificantText || declared.Children.Count != 1)
                    return BindingSourceInfo.Unknown("The local design DataContext property value is not an unambiguous object declaration.");
                var value = declared.Children[0];
                if (!value.IsClosed || value.Namespace is Design or Language || value.LocalName.Contains('.') ||
                    IsPresentationElement(value, "Binding") || IsPresentationElement(value, "MultiBinding") || IsPresentationElement(value, "PriorityBinding"))
                    return BindingSourceInfo.Unknown("The local design DataContext requires designer-specific value resolution.");
                return ObjectSource(value, "DataContext declared by a design property element");
            }
            if (design is not null) return DesignInstanceContext(element, design.Value.Text);
            if (element.Attribute("DataContext") is { } context) return ContextBinding(context);
            var property = element.Children.FirstOrDefault(c => c.LocalName.EndsWith(".DataContext", StringComparison.Ordinal)
                && c.Namespace == element.Namespace && (c.LocalName == element.LocalName + ".DataContext" || c.LocalName is "FrameworkElement.DataContext" or "FrameworkContentElement.DataContext"));
            if (property is not null)
            {
                if (property.Children.Count != 1) return BindingSourceInfo.Unknown("The local DataContext value is unknown.");
                var value = property.Children[0];
                if (IsPresentationElement(value, "Binding")) return ContextBinding(value.Attribute("Path") ?? new XamlSyntax.Attribute("Path", 0, 0, "", value));
                return ObjectSource(value, "DataContext object declaration");
            }
            if (IsPresentationElement(element, "DataTemplate") || IsPresentationElement(element, "HierarchicalDataTemplate"))
            {
                if (element.Attribute("DataType") is { } dataType)
                    return new BindingSourceInfo(dataType.Value.Text.TrimStart().StartsWith('{') ? ResolveTypeValue(element, dataType.Value.Text) : null, null, "DataTemplate.DataType");
                return TemplateItemContext(element);
            }
            if (IsPresentationElement(element, "ControlTemplate") || IsPresentationElement(element, "ItemsPanelTemplate")
                || IsPresentationElement(element, "Style") || IsPresentationElement(element, "ResourceDictionary")
                || element.LocalName.EndsWith(".Resources", StringComparison.Ordinal)) return BindingSourceInfo.Unknown("The template or resource boundary has no declared DataContext.");
            if (StyleMaySetContext(element)) return BindingSourceInfo.Unknown("A style may replace the inherited DataContext.");
            return Context(element.Parent, depth + 1);
        }
        private BindingSourceInfo DesignInstanceContext(Element element, string value)
        {
            var extension = ParseExtension(value);
            if (extension is null || !extension.IsComplete || !IsExtension(element, extension, Design, "DesignInstance"))
                return BindingSourceInfo.Unknown("The local design DataContext is not a known DesignInstance.");
            if (extension.Arguments.Any(a => a.Name is not (null or "Type" or "CreateList" or "IsDesignTimeCreatable")) ||
                extension.Arguments.GroupBy(a => a.Name).Any(g => g.Count() != 1) ||
                extension.Argument("Type") is not null && extension.Positional is not null)
                return BindingSourceInfo.Unknown("The local DesignInstance arguments are unsupported or ambiguous.");
            var createList = false;
            if (extension.Argument("CreateList") is { } list && !bool.TryParse(list.Value.Trim(), out createList) ||
                extension.Argument("IsDesignTimeCreatable") is { } creatable && !bool.TryParse(creatable.Value.Trim(), out _))
                return BindingSourceInfo.Unknown("The local DesignInstance options are not literal Boolean values.");
            var name = extension.Argument("Type") ?? extension.Positional;
            var type = name is null ? null : ResolveTypeValue(element, name.Value);
            if (!createList) return new BindingSourceInfo(type, null, "DataContext declared by d:DesignInstance");
            var listType = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
            if (type is null || type.TypeKind == TypeKind.Error || listType is null || listType.TypeKind == TypeKind.Error ||
                listType.TypeParameters.Length != 1 || listType.ContainingAssembly?.Identity.Name is not ("System.Private.CoreLib" or "mscorlib" or "System.Collections"))
                return BindingSourceInfo.Unknown("The declared DesignInstance list shape is unavailable.");
            return new BindingSourceInfo(listType.Construct(type), null, "DataContext declared by d:DesignInstance CreateList");
        }
        private BindingSourceInfo ContextBinding(XamlSyntax.Attribute context)
        {
            var binding = GetBinding(context);
            if (binding is null) return SourceValue(context.Owner, context.Owner, context.Value.Text);
            if (!binding.Complete || ChangesContextValue(context) || !TrySegments(binding.Path, out var segments)) return BindingSourceInfo.Unknown("The local DataContext value needs runtime evaluation.");
            return Follow(BindingSource(context, binding), segments, context.Owner);
        }
        private BindingSourceInfo TemplateItemContext(Element template)
        {
            var property = template.Parent;
            var owner = property?.Parent;
            if (property is null || owner is null) return BindingSourceInfo.Unknown("The template item type is not declared.");
            var sourceName = property.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal) ? "ItemsSource"
                : property.LocalName.EndsWith(".ContentTemplate", StringComparison.Ordinal) ? "Content" : null;
            if (sourceName is null || owner.Attribute(sourceName) is not { } source) return BindingSourceInfo.Unknown("The template item type is not declared.");
            var binding = GetBinding(source);
            if (binding is null || !binding.Complete || ChangesContextValue(source) || !TrySegments(binding.Path, out var segments)) return BindingSourceInfo.Unknown("The template source needs runtime evaluation.");
            var value = Follow(BindingSource(source, binding), segments, source.Owner);
            return new BindingSourceInfo(sourceName == "ItemsSource" ? ItemType(value.Type) : value.Type, null, $"Inline template inferred from {sourceName}");
        }
        private static bool StyleMaySetContext(Element element)
        {
            static bool IsNull(Element owner, string value)
            {
                var extension = ParseExtension(value);
                return extension is { IsComplete: true } && IsExtension(owner, extension, Language, "Null");
            }
            if (element.Attribute("Style") is { } style && !IsNull(element, style.Value.Text)) return true;
            var inlineStyle = element.Children.FirstOrDefault(c => c.LocalName.EndsWith(".Style", StringComparison.Ordinal));
            if (inlineStyle is null) return false;
            var pending = new Stack<Element>(inlineStyle.Children);
            while (pending.TryPop(out var current))
            {
                if (current.Attribute("BasedOn") is { } basedOn && !IsNull(current, basedOn.Value.Text)) return true;
                if (IsPresentationElement(current, "Setter") && current.Attribute("Property") is { } property
                    && (property.Value.Text == "DataContext" || property.Value.Text.EndsWith(".DataContext", StringComparison.Ordinal) || property.Value.Text.StartsWith('{'))) return true;
                foreach (var child in current.Children) pending.Push(child);
            }
            return false;
        }
        private static bool ChangesContextValue(XamlSyntax.Attribute attribute)
        {
            static bool Changes(string? name) => name is "Converter" or "StringFormat" or "FallbackValue" or "TargetNullValue";
            if (IsPresentationElement(attribute.Owner, "Binding"))
                return attribute.Owner.Attributes.Any(a => Changes(a.Name)) || attribute.Owner.Children.Any(c => Changes(c.LocalName.Split('.').Last()));
            return ParseExtension(attribute.Value.Text)?.Arguments.Any(a => Changes(a.Name)) == true;
        }
    }
}
