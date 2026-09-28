using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace WpfStudio.Workspace.Xaml;

/// <summary>An evaluated Page item and its current authored buffer, not ApplicationDefinition or loose XAML.</summary>
internal sealed record XamlPageProjectionInput(ProjectId ProjectId, string Path, string Text);
internal sealed record XamlPageProjectionField(ProjectId ProjectId, string Path, string RootClass, string Name,
    string TypeIdentity, int Start, int Length, DocumentId DocumentId, int TypeStart, int TypeLength);
internal sealed record XamlPageProjectionPage(XamlPageProjectionInput Input, string TextHash, bool IsComplete,
    string? Status, IReadOnlyList<XamlPageProjectionField> Fields, IReadOnlyList<DocumentId> ReplacedDocuments,
    DocumentId? ProjectionDocumentId);
internal sealed record XamlPageSemanticProjectionResult(Solution Solution, IReadOnlyList<XamlPageProjectionPage> Pages);

/// <summary>
/// Derives passive C# page declarations from current evaluated XAML. This is editor semantics, never compiler-output
/// provenance: callers retain the authored solution and must not save these documents or feed them to a build.
/// </summary>
internal static class XamlPageSemanticProjection
{
    private const int MaximumClassPages = 512;
    private const int MaximumTotalCharacters = 16_000_000;
    private const int MaximumGeneratedCharacters = 2_000_000;
    private const string Provenance = "// WpfStudio editor-derived XAML declarations. Never compiler output or runtime evidence.\n";
    private sealed record Field(string Name, string Type, string TypeIdentity, string Modifier, int Start, int Length,
        int TypeStart, int TypeLength);
    private sealed class Page(XamlPageProjectionInput input)
    {
        internal XamlPageProjectionInput Input { get; } = input;
        internal string Hash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Text)));
        internal List<DocumentId> Replaced { get; } = [];
        internal List<Field> Fields { get; } = [];
        internal IReadOnlyList<XamlSyntax.Element> Elements { get; set; } = [];
        internal XamlSyntax.Element? Root { get; set; }
        internal string? Class { get; set; }
        internal string Modifier { get; set; } = "public";
        internal string? Base { get; set; }
        internal DocumentId? Document { get; set; }
        internal bool StyleConnector { get; set; }
        internal bool DelegateHelper { get; set; }
        internal bool IsComplete { get; set; } = true;
        internal string? Status { get; set; }
        internal void Incomplete(string status) { IsComplete = false; Status ??= status; }
    }

    internal static async Task<XamlPageSemanticProjectionResult> BuildAsync(Solution authored,
        IReadOnlyList<XamlPageProjectionInput> inputs, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var pages = inputs.Select(input => new Page(input)).ToArray();
        var duplicateInputs = pages.GroupBy(page => (page.Input.ProjectId, Normalize(page.Input.Path)),
            new ProjectPathComparer()).Where(group => group.Count() != 1).SelectMany(group => group).ToHashSet();
        int remainingCharacters = MaximumTotalCharacters, classPages = 0;
        foreach (var page in pages)
        {
            token.ThrowIfCancellationRequested();
            if (duplicateInputs.Contains(page)) { page.Incomplete("The evaluated XAML page has duplicate project ownership."); continue; }
            if (page.Input.Text.Length > remainingCharacters)
            { page.Incomplete("Live XAML page declarations exceeded their workspace text budget; stale compiler declarations are withheld."); continue; }
            remainingCharacters -= page.Input.Text.Length;
            Parse(page, token);
            if (page.Class is not null && ++classPages > MaximumClassPages)
            {
                page.Class = null;
                page.Incomplete("Live XAML page declarations exceeded their 512-class budget; stale compiler declarations are withheld.");
            }
        }
        Solution derived = authored;
        // Establish ownership from the original compiler documents before removing any of them. A changed XAML
        // checksum is expected while typing; it is not an ownership failure and is never claimed as verification.
        foreach (var group in pages.Except(duplicateInputs).GroupBy(page => page.Input.ProjectId))
        {
            token.ThrowIfCancellationRequested();
            var project = authored.GetProject(group.Key);
            if (project?.Language != LanguageNames.CSharp || await project.GetCompilationAsync(token).ConfigureAwait(false) is not { } compilation)
            { foreach (var page in group) page.Incomplete("Live XAML page declarations require the current C# project compilation."); continue; }
            var byPath = group.ToDictionary(page => Normalize(page.Input.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var document in project.Documents)
            {
                token.ThrowIfCancellationRequested();
                var owner = await OwnedPageAsync(document, compilation, byPath, token).ConfigureAwait(false);
                if (owner is null) continue;
                owner.Replaced.Add(document.Id);
                derived = derived.RemoveDocument(document.Id);
            }
        }
        // First introduce every page class, so unbuilt pages and cross-page custom controls can be resolved using
        // ordinary compiler symbols. Then resolve lexical base types against that common bootstrap snapshot.
        foreach (var page in pages)
        {
            if (page.Class is null || duplicateInputs.Contains(page) || derived.GetProject(page.Input.ProjectId)?.Language != LanguageNames.CSharp) continue;
            byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes("WpfStudio.Xaml.Page|" + Normalize(page.Input.Path).ToUpperInvariant()));
            page.Document = DocumentId.CreateFromSerialized(page.Input.ProjectId, new Guid(identity.AsSpan(0, 16)), "XAML semantics: " + page.Input.Path);
            derived = derived.AddDocument(page.Document, System.IO.Path.GetFileName(page.Input.Path) + ".editor.g.cs",
                SourceText.From(Emit(page, includeConnector: false)), filePath: SyntheticPath(page));
        }
        foreach (var group in pages.Where(page => page.Document is not null).GroupBy(page => page.Input.ProjectId))
        {
            var compilation = await derived.GetProject(group.Key)!.GetCompilationAsync(token).ConfigureAwait(false);
            if (compilation is null) continue;
            var resolver = new SchemaTypeResolver(compilation, token);
            foreach (var page in group)
            {
                token.ThrowIfCancellationRequested();
                var type = resolver.Resolve(page.Root!, page.Root!.Name);
                if (type is not null && !type.IsGenericType && type.TypeKind == TypeKind.Class)
                    page.Base = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                else page.Incomplete("The current XAML root type is unresolved or requires unsupported generic page declarations.");
                derived = derived.WithDocumentText(page.Document!, SourceText.From(Emit(page, includeConnector: false)));
            }
        }
        // Field discovery is independent of ElementName lookup: resource objects can have compiler fields even
        // though they are not registered in the page ElementName scope. INameScope suppresses descendants only.
        foreach (var group in pages.Where(page => page.Document is not null).GroupBy(page => page.Input.ProjectId))
        {
            var compilation = await derived.GetProject(group.Key)!.GetCompilationAsync(token).ConfigureAwait(false);
            if (compilation is null) { foreach (var page in group) page.Incomplete("The XAML page compilation is unavailable."); continue; }
            foreach (var page in group)
            {
                token.ThrowIfCancellationRequested();
                DiscoverFields(page, compilation, token);
                bool connector = Canonical(compilation, "System.Windows.Markup.IComponentConnector", "PresentationFramework", "WindowsBase", "System.Xaml") is not null;
                if (!connector) page.Incomplete("The WPF component connector metadata is unavailable.");
                derived = derived.WithDocumentText(page.Document!, SourceText.From(Emit(page, connector)));
            }
        }
        return new(derived, pages.Select(page => new XamlPageProjectionPage(page.Input, page.Hash, page.IsComplete, page.Status,
            page.Document is null ? [] : page.Fields.Select(field => new XamlPageProjectionField(page.Input.ProjectId,
                page.Input.Path, page.Class!, field.Name, field.TypeIdentity, field.Start, field.Length, page.Document,
                field.TypeStart, field.TypeLength)).ToArray(), page.Replaced.ToArray(), page.Document)).ToArray());
    }

    private static void Parse(Page page, CancellationToken token)
    {
        if (page.Input.Text.Length > XamlNameScopeIndex.MaximumCharacters)
        { page.Incomplete("The XAML page exceeds the 1,000,000-character declaration budget."); return; }
        bool wellFormed = true;
        try
        {
            using var reader = XmlReader.Create(new StringReader(page.Input.Text), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = XamlNameScopeIndex.MaximumCharacters });
            int count = 0, attributes = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.Depth > 256 || reader.NodeType == XmlNodeType.Element &&
                    (++count > XamlNameScopeIndex.MaximumElements || (attributes += reader.AttributeCount) > 65536))
                { page.Incomplete("The XAML page exceeds the declaration structure budget."); return; }
            }
        }
        catch (XmlException) { wellFormed = false; page.Incomplete("Live page fields await well-formed XAML; stale compiler fields are withheld."); }
        page.Elements = XamlSyntax.Read(page.Input.Text, token);
        var roots = page.Elements.Where(element => element.Parent is null).Take(2).ToArray();
        if (roots.Length != 1) { page.Incomplete("A unique XAML page root is required."); return; }
        page.Root = roots[0];
        // Check depth before namespace lookups, which follow parent links in the forgiving parser.
        var depths = new Dictionary<XamlSyntax.Element, int>();
        foreach (var element in page.Elements)
        {
            int depth = element.Parent is null ? 0 : depths.GetValueOrDefault(element.Parent, 256) + 1;
            if (depth > 256 || page.Elements.Count > XamlNameScopeIndex.MaximumElements)
            { page.Incomplete("The XAML page exceeds the declaration structure budget."); return; }
            depths[element] = depth;
        }
        var classAttribute = page.Root.Attribute(XamlSyntax.Language, "Class");
        if (classAttribute is null) return;
        string className = classAttribute.Value.Text;
        string[] parts = className.Split('.');
        if (className.Length > 1024 || parts.Any(part => !Identifier(part)))
        { page.Incomplete("The XAML page class is not a supported C# namespace and class identifier."); return; }
        if (page.Root.Attribute(XamlSyntax.Language, "Subclass") is not null
            || page.Elements.Any(element => element.Namespace == XamlSyntax.Language && element.LocalName == "Code"
                || element.Attribute(XamlSyntax.Language, "TypeArguments") is not null))
        { page.Incomplete("x:Code, x:Subclass, and generic XAML declarations require compiler support beyond live page projection."); return; }
        page.Class = className;
        var modifier = page.Root.Attribute(XamlSyntax.Language, "ClassModifier")?.Value.Text;
        if (modifier is not null and not "public" and not "internal")
        { page.Incomplete("The current x:ClassModifier is not supported for C# page declarations."); page.Class = null; return; }
        page.Modifier = modifier ?? "public";
        // No fields are emitted for incomplete syntax, but a proven root header remains useful to authored C#.
        if (!wellFormed) page.Elements = [];
    }

    private static void DiscoverFields(Page page, Compilation compilation, CancellationToken token)
    {
        var resolver = new SchemaTypeResolver(compilation, token);
        var lexicalRoot = resolver.Resolve(page.Root!, page.Root!.Name);
        var actualRoot = compilation.Assembly.GetTypeByMetadataName(page.Class!);
        if (lexicalRoot is null || actualRoot is null || lexicalRoot.IsGenericType || !SchemaMembers.IsComplete(lexicalRoot)
            || !OrdinaryRoot(compilation, lexicalRoot))
        { page.Incomplete("Live declarations require a resolved WPF element or resource dictionary root; this custom root is unsupported."); return; }
        if (actualRoot.IsGenericType || actualRoot.ContainingType is not null)
        { page.Incomplete("Nested and generic page classes are not supported by live XAML declarations."); return; }
        if (actualRoot.GetMembers("InitializeComponent").OfType<IMethodSymbol>().Any(method => method.DeclaringSyntaxReferences.Any(reference =>
                !StringComparer.OrdinalIgnoreCase.Equals(reference.SyntaxTree.FilePath, SyntheticPath(page))
                && (reference.SyntaxTree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                    || reference.SyntaxTree.FilePath.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)))))
            page.Incomplete("An existing page generator could not be authenticated; its declarations are retained and live coverage is incomplete.");
        var nameScope = Canonical(compilation, "System.Windows.Markup.INameScope", "System.Xaml", "WindowsBase");
        var runtimeNameAttribute = Canonical(compilation, "System.Windows.Markup.RuntimeNamePropertyAttribute", "System.Xaml", "WindowsBase");
        var allowedChildren = new Dictionary<XamlSyntax.Element, bool>();
        var fields = new List<Field>();
        var runtimeNames = new Dictionary<INamedTypeSymbol, string?>(SymbolEqualityComparer.Default);
        foreach (var element in page.Elements)
        {
            token.ThrowIfCancellationRequested();
            bool allowed = element.Parent is null || allowedChildren.GetValueOrDefault(element.Parent);
            if (!allowed || XamlSchemaService.IgnoredElement(element) || element.Namespace == XamlSyntax.Language && element.LocalName == "XData")
            { allowedChildren[element] = false; continue; }
            if (element.LocalName.Contains('.')) { allowedChildren[element] = true; continue; }
            var type = ReferenceEquals(element, page.Root) ? actualRoot : resolver.Resolve(element, element.Name);
            if (type is null || !SchemaMembers.IsComplete(type) || type.IsGenericType)
            { page.Incomplete("Some current element types are unresolved; live field coverage is partial."); allowedChildren[element] = false; continue; }
            bool customSerializer = type.ContainingAssembly.Identity.Name != "PresentationFramework"
                && HasMetadata(type, "System.Windows.Markup.XamlSerializerAttribute", "PresentationFramework", "WindowsBase", "System.Xaml");
            bool isScope = nameScope is not null && type.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, nameScope));
            allowedChildren[element] = (!isScope || ReferenceEquals(element, page.Root)) && !customSerializer;
            if (customSerializer) page.Incomplete("A custom XAML serializer makes descendant field generation unavailable.");
            if (!runtimeNames.TryGetValue(type, out string? runtimeName))
            {
                runtimeName = RuntimeName(type, runtimeNameAttribute);
                runtimeNames[type] = runtimeName;
            }
            var names = element.Attributes.Where(attribute => ReferenceEquals(attribute, element.Attribute(XamlSyntax.Language, "Name"))
                || runtimeName is not null && attribute.Name == runtimeName).ToArray();
            if (names.Length == 0)
            {
                if (element.Attribute(XamlSyntax.Language, "FieldModifier") is not null)
                    page.Incomplete("x:FieldModifier requires a field-producing name on the same element.");
                continue;
            }
            if (names.Length != 1) { page.Incomplete("An element has competing name declarations; its field is withheld."); continue; }
            var attribute = names[0];
            string name = attribute.Value.Text;
            if (name.Length == 0) continue;
            if (name.Length > 512 || !XamlNameScopeIndex.ValidName(name) || !Identifier(name))
            { page.Incomplete("A current XAML field name is incomplete or invalid."); continue; }
            string modifier = element.Attribute(XamlSyntax.Language, "FieldModifier")?.Value.Text ?? "internal";
            if (modifier is not ("public" or "private" or "protected" or "internal" or "protected internal"))
            { page.Incomplete("A current x:FieldModifier is not supported by the C# WPF compiler."); continue; }
            var span = attribute.Value.Span(0, name.Length);
            fields.Add(new(name, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), TypeIdentity(type), modifier,
                span.Start, span.Length, element.NameStart, element.NameLength));
            if (fields.Count > XamlNameScopeIndex.MaximumNames)
            { page.Incomplete("The XAML page exceeds the 8,192-field declaration budget."); return; }
        }
        foreach (var group in fields.GroupBy(field => field.Name, StringComparer.Ordinal))
        {
            if (group.Count() != 1) { page.Incomplete("Duplicate XAML page field names are withheld from live declarations."); continue; }
            var field = group.Single();
            if (field.Name == actualRoot.Name || field.Name == "InitializeComponent")
            { page.Incomplete("A XAML field conflicts with its generated page class or initializer."); continue; }
            page.Fields.Add(field);
        }
    }

    private static string Emit(Page page, bool includeConnector)
    {
        string[] names = page.Class!.Split('.');
        var output = new StringBuilder(Provenance).Append("#pragma warning disable\n#nullable disable\n");
        if (names.Length > 1) output.Append("namespace ").Append(string.Join('.', names.SkipLast(1).Select(Escape))).Append(" {\n");
        output.Append(page.Modifier).Append(" partial class ").Append(Escape(names[^1]));
        if (page.Base is not null || includeConnector)
        {
            output.Append(" : ");
            if (page.Base is not null) output.Append(page.Base);
            if (includeConnector)
            {
                output.Append(page.Base is null ? "" : ", ").Append("global::System.Windows.Markup.IComponentConnector");
                if (page.StyleConnector) output.Append(", global::System.Windows.Markup.IStyleConnector");
            }
        }
        output.Append(" {\n");
        foreach (var field in page.Fields)
            output.Append(field.Modifier).Append(' ').Append(field.Type).Append(' ').Append(Escape(field.Name)).Append(";\n");
        output.Append("public void InitializeComponent() {}\n");
        if (includeConnector) output.Append("void global::System.Windows.Markup.IComponentConnector.Connect(int connectionId, object target) {}\n");
        if (includeConnector && page.StyleConnector) output.Append("void global::System.Windows.Markup.IStyleConnector.Connect(int connectionId, object target) {}\n");
        if (page.DelegateHelper) output.Append("internal global::System.Delegate _CreateDelegate(global::System.Type delegateType, string handler) => null;\n");
        output.Append("}\n");
        if (names.Length > 1) output.Append("}\n");
        return output.ToString();
    }

    private static async Task<Page?> OwnedPageAsync(Document document, Compilation compilation,
        IReadOnlyDictionary<string, Page> pages, CancellationToken token)
    {
        string? path = document.FilePath;
        if (path is null || !(path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase))) return null;
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        if (text.Length > MaximumGeneratedCharacters || !text.ToString(new TextSpan(0, Math.Min(text.Length, 4096)))
                .Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)) return null;
        var syntax = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var tree = await document.GetSyntaxTreeAsync(token).ConfigureAwait(false);
        if (syntax is null || tree is null) return null;
        var declarations = syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Take(2).ToArray();
        if (declarations is not [ClassDeclarationSyntax declaration] || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)) return null;
        var model = compilation.GetSemanticModel(tree);
        if (model.GetDeclaredSymbol(declaration, token) is not INamedTypeSymbol owner || owner.ContainingType is not null) return null;
        var connector = Canonical(compilation, "System.Windows.Markup.IComponentConnector", "PresentationFramework", "WindowsBase", "System.Xaml");
        var generatedCode = Canonical(compilation, "System.CodeDom.Compiler.GeneratedCodeAttribute", "System.Runtime", "System.Private.CoreLib", "System.CodeDom", "System", "mscorlib");
        if (connector is null || generatedCode is null || !owner.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, connector))) return null;
        bool producer = declaration.Members.OfType<MethodDeclarationSyntax>().Any(method => method.Identifier.ValueText == "InitializeComponent"
            && model.GetDeclaredSymbol(method, token) is IMethodSymbol { IsStatic: false, ReturnsVoid: true, Parameters.Length: 0 } symbol
            && symbol.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generatedCode)
                && attribute.ConstructorArguments is [{ Value: "PresentationBuildTasks" }, { Value: string }]));
        if (!producer) return null;
        var mappedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int directives = 0;
        foreach (var trivia in syntax.DescendantTrivia(descendIntoTrivia: true))
        {
            token.ThrowIfCancellationRequested();
            if (!trivia.HasStructure || trivia.GetStructure() is not DirectiveTriviaSyntax directive) continue;
            if (++directives > 16384) return null;
            string? mapped = directive switch
            {
                PragmaChecksumDirectiveTriviaSyntax checksum when checksum.IsActive => checksum.File.ValueText,
                LineDirectiveTriviaSyntax line when line.IsActive && !line.File.IsKind(SyntaxKind.None) => line.File.ValueText,
                _ => null
            };
            if (mapped is null || !mapped.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
            try { mappedPaths.Add(Normalize(System.IO.Path.IsPathRooted(mapped) ? mapped : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, mapped))); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
        }
        if (mappedPaths.Count != 1 || !pages.TryGetValue(mappedPaths.Single(), out var page)) return null;
        var styleConnector = Canonical(compilation, "System.Windows.Markup.IStyleConnector", "PresentationFramework", "System.Xaml");
        foreach (var methodSyntax in declaration.Members.OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(methodSyntax, token) is not IMethodSymbol method
                || !method.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generatedCode)
                    && attribute.ConstructorArguments is [{ Value: "PresentationBuildTasks" }, { Value: string }])) continue;
            if (styleConnector is not null && method.ExplicitInterfaceImplementations.Any(implementation =>
                    SymbolEqualityComparer.Default.Equals(implementation.ContainingType, styleConnector) && implementation.Name == "Connect"))
                page.StyleConnector = true;
            if (method is { Name: "_CreateDelegate", IsStatic: false, DeclaredAccessibility: Accessibility.Internal, Parameters.Length: 2 }
                && method.ReturnType.SpecialType == SpecialType.System_Delegate
                && method.Parameters[0].Type.ToDisplayString() == "System.Type"
                && method.Parameters[1].Type.SpecialType == SpecialType.System_String
                && method.Parameters.All(parameter => parameter.RefKind == RefKind.None)) page.DelegateHelper = true;
        }
        return page;
    }

    private static string? RuntimeName(INamedTypeSymbol type, INamedTypeSymbol? attributeType)
    {
        if (attributeType is null) return null;
        int depth = 0;
        for (var current = type; current is not null && depth++ < 64; current = current.BaseType)
        {
            var matches = current.GetAttributes().Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType)).Take(2).ToArray();
            if (matches.Length == 0) continue;
            if (matches is [var match] && match.ConstructorArguments is [{ Value: string name }]
                && SchemaMembers.Find(type, name) is { CanWrite: true, Symbol: IPropertySymbol { IsStatic: false, GetMethod.DeclaredAccessibility: Accessibility.Public }, ValueType.SpecialType: SpecialType.System_String }) return name;
            return null;
        }
        return null;
    }
    private static bool OrdinaryRoot(Compilation compilation, INamedTypeSymbol type) =>
        new[] { "System.Windows.FrameworkElement", "System.Windows.FrameworkContentElement", "System.Windows.ResourceDictionary" }
            .Any(name => Canonical(compilation, name, "PresentationFramework") is { } root && SchemaMembers.DerivesFrom(type, root));
    private static bool HasMetadata(INamedTypeSymbol type, string metadataName, params string[] assemblies)
    {
        int depth = 0;
        for (var current = type; current is not null && depth++ < 64; current = current.BaseType)
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName
                && assemblies.Contains(attribute.AttributeClass.ContainingAssembly.Identity.Name, StringComparer.Ordinal))) return true;
        return false;
    }
    private static INamedTypeSymbol? Canonical(Compilation compilation, string name, params string[] assemblies)
    {
        var matches = compilation.SourceModule.ReferencedAssemblySymbols.Where(assembly => assemblies.Contains(assembly.Identity.Name, StringComparer.Ordinal))
            .Select(assembly => assembly.GetTypeByMetadataName(name) ?? assembly.ResolveForwardedType(name)).OfType<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    internal static string TypeIdentity(ITypeSymbol type) => type.ContainingAssembly.Identity.GetDisplayName() + "|" + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static bool Identifier(string value) => value.Length != 0 && value[0] != '@' && (SyntaxFacts.IsValidIdentifier(value)
        || SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(value) != SyntaxKind.None);
    private static string Escape(string value) => "@" + value;
    private static string Normalize(string path) => System.IO.Path.GetFullPath(path);
    // Shared authored XAML must not make these derived documents Roslyn-linked: each owning project has
    // independent symbols and can resolve different types, references, and conditional declarations.
    private static string SyntheticPath(Page page) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(page.Input.Path)!,
        ".wpfstudio", page.Input.ProjectId.Id.ToString("N"), System.IO.Path.GetFileName(page.Input.Path) + ".editor.g.cs");
    private sealed class ProjectPathComparer : IEqualityComparer<(ProjectId ProjectId, string Path)>
    {
        public bool Equals((ProjectId ProjectId, string Path) left, (ProjectId ProjectId, string Path) right) =>
            left.ProjectId == right.ProjectId && StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path);
        public int GetHashCode((ProjectId ProjectId, string Path) value) => HashCode.Combine(value.ProjectId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
}
