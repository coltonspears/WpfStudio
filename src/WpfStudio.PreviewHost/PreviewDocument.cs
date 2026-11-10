using System.IO;
using System.Reflection;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.PreviewHost;

internal sealed partial class PreviewDocument
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly Dictionary<string, SourceLocation> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type> _sourceTypes = new(StringComparer.Ordinal);
    private XDocument? _sourceDocument;
    public string Markup { get; private set; } = "";
    public SourceLocation? RootSource { get; private set; }
    public List<PreviewDiagnostic> Diagnostics { get; } = [];

    // The reader keeps authored XML positions through transformations. Serializing
    // this tree would shift attributes, decode entities, and collapse start tags.
    internal XmlReader CreateSourceReader() => new OriginalPositionXmlReader(
        (_sourceDocument ?? throw new InvalidOperationException("The preview document has not been parsed.")).CreateReader());

    /// <summary>
    /// What one parse works out more than once: where each line of the text starts, and the type of each element name. Every element
    /// asks for its source position, and for its own and each ancestor's type. Only types that were found are remembered, because a
    /// library loaded later in the parse can make a name resolve that did not before.
    /// </summary>
    private sealed class ParseCache(string text)
    {
        public string Text { get; } = text;
        public int[]? LineStarts { get; set; }
        public Dictionary<(XName Name, Assembly? Assembly), Type> Types { get; } = [];
        public System.Xaml.XamlSchemaContext? Schema { get; set; }
    }
    [ThreadStatic] private static ParseCache? _parse;

    public static PreviewDocument Parse(PreviewRequest request, Assembly? projectAssembly)
    {
        var outer = _parse;
        _parse = new ParseCache(request.Text);
        try { return ParseCore(request, projectAssembly); }
        finally { _parse = outer; }
    }

    private static PreviewDocument ParseCore(PreviewRequest request, Assembly? projectAssembly)
    {
        var result = new PreviewDocument();
        result.InitializeLayoutSources(request);
        using var reader = XmlReader.Create(new StringReader(request.Text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2_000_000
        }, Path.IsPathFullyQualified(request.Path) ? new Uri(Path.GetFullPath(request.Path), UriKind.Absolute).AbsoluteUri : "");
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.SetBaseUri | LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("The document has no root element.");
        result.RememberBindingOrigins(root, request.Path);
        result.RootSource = GetSource(request, root);
        XNamespace sourceNamespace = "clr-namespace:WpfStudio.PreviewHost;assembly=WpfStudio.PreviewHost";
        string sourcePrefix = "wpfStudioPreview";
        for (int suffix = 1; root.GetNamespaceOfPrefix(sourcePrefix) is not null; suffix++) sourcePrefix = "wpfStudioPreview" + suffix;
        root.Add(new XAttribute(XNamespace.Xmlns + sourcePrefix, sourceNamespace.NamespaceName));
        foreach (var element in root.DescendantsAndSelf())
        {
            // Loose XAML cannot compile code-behind. Suppression is explicit and
            // reported; constructor/event behavior is never presented as executed.
            foreach (var attribute in element.Attributes().ToArray())
            {
                if (attribute.Name == Xaml + "Class" || attribute.Name == Xaml + "ClassModifier" ||
                    attribute.Name == Xaml + "FieldModifier" || attribute.Name == Xaml + "Subclass")
                {
                    if (element == root && attribute.Name == Xaml + "Class") result.ClassName = attribute.Value.Trim();
                    result.AddSuppression(attribute, $"{attribute.Name.LocalName} is omitted from the preview; code-behind is not executed.");
                    attribute.Remove();
                }
            }
            Type? type = ResolveType(element.Name, projectAssembly);
            if (type is not null && typeof(ResourceDictionary).IsAssignableFrom(type)
                && element.Attribute("Source") is { } dictionarySource)
            {
                // Loose XAML keeps a file base URI for neighboring source dictionaries.
                // An assembly-qualified resource still belongs to WPF's pack scheme,
                // otherwise /Library;component/... becomes a drive-rooted file path.
                string uri = dictionarySource.Value;
                int component = uri.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
                if (uri.StartsWith('/') && component > 1 && !uri[1..component].Contains('/'))
                    dictionarySource.Value = "pack://application:,,," + uri;
            }
            if (type is not null && typeof(DependencyObject).IsAssignableFrom(type))
            {
                // Attached identity does not change NameScope behavior. It survives
                // templates and item replication, and is absent on framework-created
                // implementation visuals for which there is no source element.
                string sourceId = result._sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result._sources[sourceId] = GetSource(request, element);
                result._sourceTypes[sourceId] = type;
                result.RememberLayoutSource(sourceId, element, type, request, projectAssembly);
                element.SetAttributeValue(sourceNamespace + "PreviewSource.Id", sourceId);
            }
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray())
            {
                if (attribute.Name.Namespace != XNamespace.None) continue;
                string memberName = attribute.Name.LocalName;
                bool isEvent = type?.GetEvent(memberName, BindingFlags.Instance | BindingFlags.Public) is not null;
                int separator = memberName.LastIndexOf('.');
                if (!isEvent && separator > 0)
                {
                    var owner = ResolveType(element.Name.Namespace + memberName[..separator], projectAssembly);
                    isEvent = owner?.GetField(memberName[(separator + 1)..] + "Event", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) is not null;
                }
                if (isEvent || (element.Name == Presentation + "EventSetter" && memberName == "Handler"))
                {
                    result.AddSuppression(attribute, $"Event handler '{attribute.Value}' is omitted from the preview.");
                    attribute.Remove();
                }
            }
        }

        result.ApplyDesignTimeValues(root, request, projectAssembly, sourceNamespace, sourcePrefix);

        // Windows retain their real type and use the engine's hidden Window
        // surface, preserving Window ancestor bindings and attached behaviors.
        // Pages still omit navigation by previewing their content.
        if (root.Name == Presentation + "Page")
        {
            string oldType = root.Name.LocalName;
            var presentationOnly = new HashSet<string>(StringComparer.Ordinal)
            {
                "Title", "Icon", "WindowStartupLocation", "WindowState", "WindowStyle", "ResizeMode", "SizeToContent",
                "ShowInTaskbar", "Topmost", "AllowsTransparency", "Left", "Top", "Owner", "DialogResult",
                "NavigationWindow", "WindowTitle", "WindowWidth", "WindowHeight", "KeepAlive", "ShowsNavigationUI"
            };
            root.Name = Presentation + "ContentControl";
            foreach (var attribute in root.Attributes().Where(a => a.Name.Namespace == XNamespace.None && presentationOnly.Contains(a.Name.LocalName)).ToArray())
                attribute.Remove();
            foreach (var property in root.Elements().Where(e => e.Name.Namespace == Presentation && e.Name.LocalName.StartsWith(oldType + ".", StringComparison.Ordinal)))
                property.Name = Presentation + ("ContentControl" + property.Name.LocalName[oldType.Length..]);
            result.Diagnostics.Add(new PreviewDiagnostic($"{oldType} content is previewed without window chrome, navigation, or application startup.", "Information"));
        }
        // EventSetter has no meaning without its handler and otherwise fails load.
        foreach (var eventSetter in root.Descendants(Presentation + "EventSetter").Where(e => e.Attribute("Handler") is null).ToArray()) eventSetter.Remove();

        if (projectAssembly is not null) QualifyProjectNamespaces(root, projectAssembly);
        result.Markup = document.ToString(SaveOptions.DisableFormatting);
        result._sourceDocument = document;
        return result;
    }

    /// <summary>Names the project assembly in every <c>clr-namespace</c> mapping that leaves it out, as the XAML compiler does for a project's own markup. Loose XAML has no assembly of its own to default to.</summary>
    private static void QualifyProjectNamespaces(XElement root, Assembly projectAssembly)
    {
        // An XML name stores its expanded namespace URI independently of the xmlns
        // attribute. Update both together; changing only the declaration leaves custom
        // element/property names in the old URI and can produce conflicting prefixes.
        var namespaces = root.DescendantsAndSelf().Attributes()
            .Where(a => a.IsNamespaceDeclaration && a.Value.StartsWith("clr-namespace:", StringComparison.Ordinal) && !a.Value.Contains(";assembly=", StringComparison.Ordinal))
            .Select(a => a.Value).Distinct(StringComparer.Ordinal)
            .ToDictionary(uri => uri, uri => XNamespace.Get(uri + ";assembly=" + projectAssembly.GetName().Name), StringComparer.Ordinal);
        foreach (var element in root.DescendantsAndSelf())
        {
            if (namespaces.TryGetValue(element.Name.NamespaceName, out var elementNamespace))
                element.Name = elementNamespace + element.Name.LocalName;
            var attributes = element.Attributes().ToArray();
            if (!attributes.Any(a => a.IsNamespaceDeclaration ? namespaces.ContainsKey(a.Value) : namespaces.ContainsKey(a.Name.NamespaceName))) continue;
            foreach (var attribute in attributes.Where(attribute => attribute.IsNamespaceDeclaration))
                if (namespaces.TryGetValue(attribute.Value, out var declaredNamespace)) attribute.Value = declaredNamespace.NamespaceName;
            // Keep unchanged XAttribute instances: their original line information
            // belongs to authored bindings even when an xmlns value is normalized.
            if (attributes.Any(attribute => !attribute.IsNamespaceDeclaration && namespaces.ContainsKey(attribute.Name.NamespaceName)))
                element.ReplaceAttributes(attributes.Select(attribute => !attribute.IsNamespaceDeclaration && namespaces.TryGetValue(attribute.Name.NamespaceName, out var memberNamespace)
                    ? CopySourceAnnotations(attribute, new XAttribute(memberNamespace + attribute.Name.LocalName, attribute.Value)) : attribute));
        }
    }

    private static XAttribute CopySourceAnnotations(XAttribute source, XAttribute replacement)
    {
        // XObject exposes annotations publicly. Transfer the original line/base
        // metadata without reflecting over WPF or LINQ-to-XML private state.
        foreach (object annotation in source.Annotations<object>()) replacement.AddAnnotation(annotation);
        return replacement;
    }

    public SourceLocation? FindSource(DependencyObject target, bool isRoot) =>
        PreviewSource.GetId(target) is { } sourceId && _sources.TryGetValue(sourceId, out var source) ? source : isRoot ? RootSource : null;

    public bool CanWriteProperty(DependencyObject target, DependencyProperty property, bool attached)
    {
        if (PreviewSource.GetId(target) is not { } sourceId || !_sourceTypes.TryGetValue(sourceId, out var type)) return false;
        if (!attached)
        {
            var wrapper = PropertyWrappers.Find(type, property.Name);
            return wrapper?.SetMethod?.IsPublic == true && wrapper.PropertyType == property.PropertyType;
        }
        return property.OwnerType.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m =>
            m.Name == "Set" + property.Name && m.GetParameters() is [{ ParameterType: var owner }, { ParameterType: var value }] &&
            owner.IsAssignableFrom(type) && value == property.PropertyType);
    }

    private void AddSuppression(XAttribute attribute, string message)
    {
        var line = (IXmlLineInfo)attribute;
        Diagnostics.Add(new PreviewDiagnostic(message, "Information", line.LineNumber, line.LinePosition));
    }

    private static SourceLocation GetSource(PreviewRequest request, XElement element)
    {
        var info = (IXmlLineInfo)element;
        int start;
        if (_parse is { } parse && ReferenceEquals(parse.Text, request.Text))
        {
            var lines = parse.LineStarts ??= LineStarts(request.Text);
            start = info.LineNumber <= 1 ? 0 : info.LineNumber - 1 < lines.Length ? lines[info.LineNumber - 1] : request.Text.Length;
        }
        else
        {
            start = 0;
            for (int line = 1; line < info.LineNumber && start < request.Text.Length; line++)
            {
                while (start < request.Text.Length && request.Text[start] is not ('\r' or '\n')) start++;
                if (start < request.Text.Length && request.Text[start++] == '\r' && start < request.Text.Length && request.Text[start] == '\n') start++;
            }
        }
        start = Math.Min(request.Text.Length, start + Math.Max(0, info.LinePosition - 1));
        // LINQ to XML positions the element on the first character of its name.
        if (start > 0 && request.Text[start - 1] == '<') start--;
        int end = start;
        char quote = '\0';
        for (; end < request.Text.Length; end++)
        {
            char character = request.Text[end];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (character is '\'' or '"') quote = character;
            else if (character == '>') break;
        }
        return new SourceLocation(request.Path, start, Math.Min(request.Text.Length, end + 1) - start, info.LineNumber,
            Math.Max(1, info.LinePosition - 1), element.Name.LocalName);
    }

    /// <summary>The offset each line starts at, by the rule of the scan in <see cref="GetSource"/>: a line ends at \r\n, \r or \n.</summary>
    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            starts.Add(index + 1);
        }
        return starts.ToArray();
    }

    private static Type? ResolveType(XName name, Assembly? projectAssembly)
    {
        if (_parse is not { } parse) return ResolveType(name.NamespaceName, name.LocalName, projectAssembly);
        if (parse.Types.TryGetValue((name, projectAssembly), out var known)) return known;
        var found = ResolveType(name.NamespaceName, name.LocalName, projectAssembly);
        if (found is not null) parse.Types[(name, projectAssembly)] = found;
        return found;
    }

    /// <summary>Takes the names apart from an <see cref="XName"/>, so a type named in an attribute value, such as a nested <c>Outer+Inner</c>, resolves too.</summary>
    private static Type? ResolveType(string namespaceName, string localName, Assembly? projectAssembly)
    {
        if (namespaceName == Presentation.NamespaceName)
        {
            foreach (var assembly in new[] { typeof(FrameworkElement).Assembly, typeof(System.Windows.Media.Brush).Assembly, typeof(DependencyObject).Assembly })
                foreach (string prefix in new[] { "System.Windows.", "System.Windows.Controls.", "System.Windows.Controls.Primitives.", "System.Windows.Documents.", "System.Windows.Shapes.", "System.Windows.Media." })
                    if (assembly.GetType(prefix + localName) is { } type) return type;
        }
        else if (namespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            string[] parts = namespaceName[14..].Split(';');
            string? assemblyName = parts.FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?[9..];
            Assembly? assembly = assemblyName is null ? projectAssembly : Assembly.Load(new AssemblyName(assemblyName));
            return assembly?.GetType(parts[0] + "." + localName);
        }
        else if (!localName.Contains('.'))
        {
            // XmlnsDefinition libraries are discovered before preprocessing.
            // One context for a parse. Each new one reads the XmlnsDefinition attributes of every loaded assembly again, and a view
            // of a control library names a type from such a namespace on nearly every element. It follows assemblies loaded later.
            var schema = _parse is { } parse ? parse.Schema ??= new System.Xaml.XamlSchemaContext() : new System.Xaml.XamlSchemaContext();
            return schema.GetXamlType(new System.Xaml.Schema.XamlTypeName(namespaceName, localName))?.UnderlyingType;
        }
        return null;
    }
}
