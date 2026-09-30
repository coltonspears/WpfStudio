using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;

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

    public static PreviewDocument Parse(PreviewRequest request, Assembly? projectAssembly)
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

        result.ApplyDesignTimeValues(root, request, projectAssembly, sourceNamespace);

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

        if (projectAssembly is not null)
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
        result.Markup = document.ToString(SaveOptions.DisableFormatting);
        result._sourceDocument = document;
        return result;
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
            var wrapper = type.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
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
        int start = 0;
        for (int line = 1; line < info.LineNumber && start < request.Text.Length; line++)
        {
            while (start < request.Text.Length && request.Text[start] is not ('\r' or '\n')) start++;
            if (start < request.Text.Length && request.Text[start++] == '\r' && start < request.Text.Length && request.Text[start] == '\n') start++;
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

    private static Type? ResolveType(XName name, Assembly? projectAssembly)
    {
        if (name.Namespace == Presentation)
        {
            foreach (var assembly in new[] { typeof(FrameworkElement).Assembly, typeof(System.Windows.Media.Brush).Assembly, typeof(DependencyObject).Assembly })
                foreach (string prefix in new[] { "System.Windows.", "System.Windows.Controls.", "System.Windows.Controls.Primitives.", "System.Windows.Documents.", "System.Windows.Shapes.", "System.Windows.Media." })
                    if (assembly.GetType(prefix + name.LocalName) is { } type) return type;
        }
        else if (name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            string[] parts = name.NamespaceName[14..].Split(';');
            string? assemblyName = parts.FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?[9..];
            Assembly? assembly = assemblyName is null ? projectAssembly : Assembly.Load(new AssemblyName(assemblyName));
            return assembly?.GetType(parts[0] + "." + name.LocalName);
        }
        else if (!name.LocalName.Contains('.'))
        {
            // XmlnsDefinition libraries are discovered before preprocessing.
            return new System.Xaml.XamlSchemaContext().GetXamlType(new System.Xaml.Schema.XamlTypeName(name.NamespaceName, name.LocalName))?.UnderlyingType;
        }
        return null;
    }
}

internal sealed partial class PreviewAssemblyResolver : IDisposable
{
    private readonly string? _shadowDirectory;
    private readonly string? _shadowToken;
    private string? _directory;
    private string? _assemblyPath;
    private Assembly? _assembly;
    private AssemblyDependencyResolver? _dependencies;
    public PreviewAssemblyResolver(string? shadowDirectory, string? shadowToken)
    {
        _shadowDirectory = shadowDirectory;
        _shadowToken = shadowToken;
        AssemblyLoadContext.Default.Resolving += Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
    }

    public Assembly? Load(PreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AssemblyPath)) return null;
        if (string.IsNullOrWhiteSpace(request.ProjectDirectory)) throw new InvalidOperationException("A project directory is required to load a project assembly.");
        string root = Path.GetFullPath(request.ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(request.AssemblyPath);
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("The preview assembly must be an existing DLL inside the selected local project directory.");
        if (_assemblyPath is not null && !string.Equals(_assemblyPath, path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restart the preview host before switching project assemblies.");
        if (_assembly is not null) return _assembly;
        string shadowRoot = ValidateShadowDirectory();
        string sourceDirectory = Path.GetDirectoryName(path)!;
        if (shadowRoot.StartsWith(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The project output directory cannot contain the preview's temporary directory.");
        _directory = Path.Combine(shadowRoot, "output");
        CopyOutputTree(sourceDirectory, _directory);
        StageDependencies(request);
        string shadowAssembly = Path.Combine(_directory, Path.GetFileName(path));
        _dependencies = new AssemblyDependencyResolver(shadowAssembly);
        _assemblyPath = path;
        _assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(shadowAssembly);
        return _assembly;
    }

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (_directory is null || string.IsNullOrWhiteSpace(name.Name) || name.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        string? candidate = _dependencies?.ResolveAssemblyToPath(name);
        if (candidate is not null && IsShadowPath(candidate) && File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
        candidate = string.IsNullOrWhiteSpace(name.CultureName) ? Path.Combine(_directory, name.Name + ".dll") :
            Path.Combine(_directory, name.CultureName, name.Name + ".dll");
        if (IsShadowPath(candidate) && File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
        if (_missingDependencies.TryGetValue(name.Name, out var missing))
            throw new FileNotFoundException($"Preview dependency '{name.Name}' is missing from the built output and restored package cache. Restore and rebuild the selected project. Expected: {missing}", missing);
        return null;
    }

    private IntPtr ResolveNative(Assembly assembly, string name)
    {
        if (!IsShadowPath(assembly.Location)) return IntPtr.Zero;
        string? path = _dependencies?.ResolveUnmanagedDllToPath(name);
        return path is not null && IsShadowPath(path) && File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    private bool IsShadowPath(string path) => _directory is not null && !string.IsNullOrWhiteSpace(path) &&
        Path.GetFullPath(path).StartsWith(_directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string ValidateShadowDirectory()
    {
        if (string.IsNullOrWhiteSpace(_shadowDirectory) || string.IsNullOrWhiteSpace(_shadowToken))
            throw new InvalidOperationException("Project assembly previews require a client-owned shadow directory.");
        string path = Path.GetFullPath(_shadowDirectory);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(path), temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("WpfStudio.Preview.", StringComparison.Ordinal) ||
            !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(Path.Combine(path, "owner.token")) != _shadowToken)
            throw new InvalidOperationException("The preview shadow directory is not owned by this client.");
        return path;
    }

    private static void CopyOutputTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A project output dependency is a filesystem link. Copy it into the build output before previewing.");
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A project output dependency is a filesystem link. Copy it into the build output before previewing.");
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyOutputTree(entry, target);
            else File.Copy(entry, target, overwrite: true);
        }
    }

    public void Dispose()
    {
        AssemblyLoadContext.Default.Resolving -= Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveNative;
        // The owning client removes only its verified temp directory after this
        // process exits and Windows releases mapped managed/native DLL files.
    }
}
