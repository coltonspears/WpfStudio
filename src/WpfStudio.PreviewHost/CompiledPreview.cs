using System.Collections;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Baml2006;
using System.Xaml;
using WpfStudio.Contracts;
using WpfXamlReader = System.Windows.Markup.XamlReader;

namespace WpfStudio.PreviewHost;

internal static class CompiledPreview
{
    public static (FrameworkElement View, PreviewBuildProvenance Build) Create(PreviewRequest request, Assembly assembly, PreviewScenarioActivation? scenario = null)
    {
        bool useFactory = scenario?.HasViewFactory == true;
        if (request.ViewTypeName is { Length: > 1024 }) throw new InvalidOperationException("The expected view type name is limited to 1024 characters.");
        if (string.IsNullOrWhiteSpace(request.ViewTypeName) && !useFactory)
            throw new InvalidOperationException("Compiled preview requires the built view's full type name (its x:Class).");
        var type = string.IsNullOrWhiteSpace(request.ViewTypeName) ? null : assembly.GetType(request.ViewTypeName, throwOnError: true)!;
        if (type is not null && (!typeof(FrameworkElement).IsAssignableFrom(type) || !useFactory && type.IsAbstract || type.ContainsGenericParameters))
            throw new InvalidOperationException("The compiled view must be a concrete WPF FrameworkElement type.");
        if (!useFactory && type!.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException("The compiled view needs a public parameterless constructor. A custom preview factory is not configured.");

        PrepareApplication(request, assembly);
        // Activating the built class invokes its own constructor/InitializeComponent
        // and connection handlers. The editor buffer is deliberately not parsed.
        var view = useFactory ? scenario!.CreateView(type) : (FrameworkElement)Activator.CreateInstance(type!)!;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        string? resourcePath = string.IsNullOrWhiteSpace(request.ApplicationResourcePath) ? null : request.ApplicationResourcePath;
        return (view, new PreviewBuildProvenance(Path.GetFullPath(request.AssemblyPath!), assembly.GetName().Name ?? "",
            hash, assembly.ManifestModule.ModuleVersionId.ToString("D"), view.GetType().FullName!, resourcePath));
    }

    internal static bool PrepareApplication(PreviewRequest request, Assembly assembly, bool optionalResources = false)
    {
        ConfigureApplicationAssembly(assembly);
        string? resourcePath = string.IsNullOrWhiteSpace(request.ApplicationResourcePath) ? null : request.ApplicationResourcePath;
        if (resourcePath is not null)
        {
            var application = Application.Current ?? throw new InvalidOperationException("The preview host application is unavailable.");
            var resources = LoadApplicationResources(assembly, resourcePath, optionalResources);
            application.Resources = resources ?? new ResourceDictionary();
            return resources is not null;
        }
        return false;
    }

    private static void ConfigureApplicationAssembly(Assembly assembly)
    {
        // Source renders reuse their isolated host. The resource identity is
        // immutable, but reapplying that same identity is safe.
        if (Assembly.GetEntryAssembly() == assembly && Application.ResourceAssembly == assembly) return;
        var hostAssembly = typeof(CompiledPreview).Assembly;
        if (Assembly.GetEntryAssembly() != hostAssembly)
            throw new InvalidOperationException("Compiled preview requires a fresh isolated preview host before setting application resource identity.");

        // WPF only permits its one-time ResourceAssembly setter while no entry
        // assembly is available. The public .NET API lets this dedicated host
        // supply that identity without running the project's Main or App.
        // The WPF setter also initializes its internal pack-URI resource identity.
        Assembly.SetEntryAssembly(null);
        try { Application.ResourceAssembly = assembly; }
        catch (InvalidOperationException exception)
        {
            Assembly.SetEntryAssembly(hostAssembly);
            throw new InvalidOperationException("The preview host already initialized WPF application resources. Refresh to start a fresh compiled preview process.", exception);
        }
        Assembly.SetEntryAssembly(assembly);
    }

    private static ResourceDictionary? LoadApplicationResources(Assembly assembly, string resourcePath, bool optional)
    {
        string normalized = resourcePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(p => p is ".." or ".") || !normalized.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Application resources must name a compiled application XAML path within the assembly, such as App.xaml.");
        string bamlName = normalized[..^5].ToLowerInvariant() + ".baml";
        using var manifest = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources");
        if (manifest is null)
        {
            if (optional) return null;
            throw new InvalidOperationException("The built assembly has no compiled WPF resources. Clear the application resource path for a view without App.xaml.");
        }
        using var resources = new ResourceReader(manifest);
        using var baml = new MemoryStream();
        bool found = false;
        foreach (DictionaryEntry entry in resources)
        {
            if (entry.Key is not string key || !string.Equals(key, bamlName, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Value is not Stream stream) throw new InvalidOperationException("The compiled application resource is not BAML.");
            stream.CopyTo(baml);
            found = true;
            break;
        }
        if (!found)
        {
            if (optional) return null;
            throw new InvalidOperationException($"The build contains no '{normalized}' application resource. Rebuild the project or clear the application resource path.");
        }
        baml.Position = 0;
        var baseUri = new Uri($"pack://application:,,,/{assembly.GetName().Name};component/{normalized}", UriKind.Absolute);
        using var reader = new Baml2006Reader(baml, new XamlReaderSettings { LocalAssembly = assembly, BaseUri = baseUri });
        var namespaces = new List<NamespaceDeclaration>();
        int depth = 0;
        bool applicationRoot = false;
        while (reader.Read())
        {
            if (reader.NodeType == XamlNodeType.NamespaceDeclaration) namespaces.Add(reader.Namespace);
            if (reader.NodeType is XamlNodeType.StartObject or XamlNodeType.GetObject)
            {
                depth++;
                if (depth == 1) applicationRoot = reader.Type?.UnderlyingType is { } rootType && typeof(Application).IsAssignableFrom(rootType);
            }
            else if (reader.NodeType == XamlNodeType.EndObject) depth--;
            if (reader.NodeType != XamlNodeType.StartMember || depth != 1 || reader.Member.Name != "Resources") continue;
            if (!applicationRoot) throw new InvalidOperationException("The requested application resource must have an Application root.");
            if (!reader.Read() || reader.NodeType is not (XamlNodeType.GetObject or XamlNodeType.StartObject))
                throw new InvalidOperationException("The compiled Application.Resources member is not a resource dictionary.");
            var queue = new XamlNodeQueue(reader.SchemaContext);
            using (var writer = queue.Writer)
            {
                foreach (var ns in namespaces.GroupBy(n => n.Prefix).Select(g => g.Last())) writer.WriteNamespace(ns);
                writer.WriteStartObject(reader.SchemaContext.GetXamlType(typeof(ResourceDictionary)));
                writer.WriteStartMember(XamlLanguage.Base);
                writer.WriteValue(baseUri.AbsoluteUri);
                writer.WriteEndMember();
                int dictionaryDepth = 1;
                while (reader.Read())
                {
                    if (reader.NodeType is XamlNodeType.StartObject or XamlNodeType.GetObject) dictionaryDepth++;
                    if (reader.NodeType == XamlNodeType.EndObject && --dictionaryDepth == 0) break;
                    writer.WriteNode(reader);
                }
                writer.WriteEndObject();
            }
            using var dictionaryReader = queue.Reader;
            return WpfXamlReader.Load(dictionaryReader) as ResourceDictionary
                ?? throw new InvalidOperationException("The compiled application resources could not be loaded.");
        }
        if (!applicationRoot) throw new InvalidOperationException("The requested application resource must have an Application root.");
        return new ResourceDictionary();
    }
}
