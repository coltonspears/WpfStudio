using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

// Design-time data: d:DesignInstance, d:DesignData and d:SampleData, and the view model matched by name when a view
// declares none. Each is resolved to an object here, while the source is read, and reaches the loaded view through
// a DesignDataExtension placed where the declaration stood.
internal sealed partial class PreviewDocument
{
    private const int MaximumDesignDataFileLength = 2 * 1024 * 1024;
    private static readonly Dictionary<string, WeakReference<PreviewDocument>> DesignDocuments = new(StringComparer.Ordinal);
    private static long _lastDesignToken;
    private readonly List<object?> _designValues = [];
    private string? _designToken;
    private DesignSampleFactory? _samples;

    /// <summary>What each design-time data declaration resolved to, in document order.</summary>
    public List<PreviewDesignData> DesignData { get; } = [];
    /// <summary>The root's x:Class, which pairs the view with a view model by name.</summary>
    public string? ClassName { get; private set; }
    /// <summary>
    /// Whether the loaded markup contains extensions that came from design-time declarations. They run during load
    /// and layout, so a failure there may be theirs rather than the authored view's.
    /// </summary>
    public bool UsesDesignMarkup { get; private set; }

    /// <summary>The value a <see cref="DesignDataExtension"/> stands for, while its document is still the one being previewed.</summary>
    internal static bool TryGetDesignValue(string? key, out object? value)
    {
        value = null;
        // "<document>_<declaration>", as RegisterDesignValue wrote it.
        if (key?.Split('_') is not [var token, var position]) return false;
        PreviewDocument? document;
        lock (DesignDocuments)
            if (!DesignDocuments.TryGetValue(token, out var reference) || !reference.TryGetTarget(out document)) return false;
        if (!int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= document._designValues.Count) return false;
        value = document._designValues[index];
        return true;
    }

    /// <summary>Keeps <paramref name="value"/> for the loader and returns the markup that asks for it.</summary>
    private string RegisterDesignValue(object? value, string prefix)
    {
        if (_designToken is null)
        {
            _designToken = Interlocked.Increment(ref _lastDesignToken).ToString(CultureInfo.InvariantCulture);
            lock (DesignDocuments)
            {
                // Documents of earlier renders are gone once their preview closed.
                foreach (string stale in DesignDocuments.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray()) DesignDocuments.Remove(stale);
                DesignDocuments[_designToken] = new WeakReference<PreviewDocument>(this);
            }
        }
        _designValues.Add(value);
        UsesDesignMarkup = true;
        return "{" + prefix + ":DesignData " + _designToken + "_" + (_designValues.Count - 1).ToString(CultureInfo.InvariantCulture) + "}";
    }

    /// <returns>The markup to load in place of the declaration and what it resolved to, or null when the reason was reported and the authored value stays.</returns>
    private (string Markup, PreviewDesignData Data)? ResolveDesignData(XElement element, XObject source, DesignMember member, DesignMarkup markup, Assembly? assembly,
        PreviewRequest request, string prefix)
    {
        Type target = member.Property.PropertyType;
        bool list = target != typeof(object) && target != typeof(string) && typeof(IEnumerable).IsAssignableFrom(target);
        if (target != typeof(object) && !(list && target.IsAssignableFrom(typeof(ObservableCollection<object>))))
        {
            DesignDiagnostic(source, $"d:{markup.Name} supplies an object or a list, which '{member.Property.Name}' does not take. Use it for DataContext, ItemsSource or another property of type object or IEnumerable.");
            return null;
        }
        (object? Value, string Kind, string? TypeName, string Summary)? data = markup.Name switch
        {
            "DesignInstance" => DesignInstance(element, source, markup, assembly, list),
            "DesignData" => DesignDataFile(source, markup, assembly, request),
            "SampleData" => SampleData(source, markup, list),
            _ => null
        };
        if (data is not { } resolved)
        {
            if (markup.Name is not ("DesignInstance" or "DesignData" or "SampleData"))
                DesignDiagnostic(source, $"Design-time extension 'd:{markup.Name}' is not supported; the authored runtime value is retained.");
            return null;
        }
        if (list && resolved.Value is not (null or IEnumerable) || resolved.Value is not null && !target.IsInstanceOfType(resolved.Value))
        {
            DesignDiagnostic(source, $"The design data for '{member.Property.Name}' is not a list; the authored runtime value is retained. Add CreateList=True, or declare a collection type.");
            return null;
        }
        var line = (IXmlLineInfo)source;
        return (RegisterDesignValue(resolved.Value, prefix), new PreviewDesignData(member.Property.Name, resolved.Kind, resolved.TypeName, resolved.Summary,
            line.HasLineInfo() ? line.LineNumber : null, line.HasLineInfo() ? line.LinePosition : null));
    }

    private (object? Value, string Kind, string? TypeName, string Summary)? DesignInstance(XElement element, XObject source, DesignMarkup markup, Assembly? assembly, bool listTarget)
    {
        string? typeText = markup.Argument("Type", 0);
        if (typeText is null || markup.Positional.Count > 1 || markup.Positional.Count == 1 && markup.Named.ContainsKey("Type")
            || markup.Named.Keys.Any(key => key is not ("Type" or "IsDesignTimeCreatable" or "CreateList"))
            || !TryReadFlag(markup, "IsDesignTimeCreatable", out bool creatable) || !TryReadFlag(markup, "CreateList", out bool createList))
        {
            DesignDiagnostic(source, "d:DesignInstance takes a Type and the options IsDesignTimeCreatable and CreateList (True or False); the authored runtime value is retained.");
            return null;
        }
        if (ResolveTypeValue(element, typeText, assembly) is not { } declared)
        {
            DesignDiagnostic(source, $"Design type '{typeText}' could not be loaded, so no design data is shown. " + (assembly is null
                ? "Build the project: the preview reads the type from the built assembly."
                : "Check its namespace mapping, then rebuild the project so the build contains the type."));
            return null;
        }
        string name = declared.FullName ?? declared.Name;
        _samples ??= new DesignSampleFactory(assembly);
        // A list property given a single item's type means a list of that item, as CreateList says outright.
        bool asList = createList || listTarget && !DesignSampleFactory.IsSequence(declared);
        if (creatable)
        {
            try
            {
                if (!asList) return (Activator.CreateInstance(declared), PreviewDesignDataKinds.Created, name, $"Design data: {name}, constructed with its parameterless constructor.");
                var created = new ObservableCollection<object>();
                for (int item = 0; item < 3; item++) created.Add(Activator.CreateInstance(declared)!);
                return (created, PreviewDesignDataKinds.Created, name, $"Design data: a list of 3 {name}, each constructed with its parameterless constructor.");
            }
            // The constructor is project code: whatever it throws, the view is still worth showing with sample values.
            catch (Exception exception)
            {
                string reason = exception is MissingMethodException or MemberAccessException ? "it has no public parameterless constructor" : exception.GetBaseException().Message;
                DesignDiagnostic(source, $"{name} could not be constructed for the designer ({reason}). Sample data generated without its constructor is shown instead.");
            }
        }
        bool sequence = !asList && DesignSampleFactory.IsSequence(declared);
        object? value;
        try { value = asList ? _samples.CreateList(declared, 5) : sequence ? _samples.CreateSequence(declared) : _samples.Create(declared); }
        // Filling a collection runs its constructor, which is project code for a project's own collection class.
        catch (Exception) { value = null; }
        if (value is IList { Count: > 0 } or not (null or IList))
            return (value, PreviewDesignDataKinds.Sample, name,
                asList ? $"Design data: a list of sample {name} items, generated without running a constructor."
                : sequence ? $"Design data: a {name} filled with generated sample items."
                : $"Design data: sample values generated from {name}; its constructor was not run.");
        // An interface nothing implements, or a type with no public properties: answer bindings by name instead.
        return (asList ? UntypedItems(5) : new DesignSampleItem(0), PreviewDesignDataKinds.Untyped, name,
            $"Design data: {name} has no class the preview can fill, so untyped sample values answer its bindings by property name.");
    }

    private (object? Value, string Kind, string? TypeName, string Summary)? SampleData(XObject source, DesignMarkup markup, bool listTarget)
    {
        int count = 5;
        if (markup.Positional.Count > 0 || markup.Named.Keys.Any(key => key != "ItemCount")
            || markup.Named.TryGetValue("ItemCount", out string? text) && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count is < 1 or > 100))
        {
            DesignDiagnostic(source, "d:SampleData takes one option, ItemCount, from 1 to 100; the authored runtime value is retained.");
            return null;
        }
        return listTarget
            ? (UntypedItems(count), PreviewDesignDataKinds.Untyped, null, $"Design data: {count} sample items that answer the item template's bindings by property name.")
            : (new DesignSampleItem(0), PreviewDesignDataKinds.Untyped, null, "Design data: a sample object that answers bindings by property name.");
    }

    private static ObservableCollection<object> UntypedItems(int count)
    {
        var items = new ObservableCollection<object>();
        for (int item = 0; item < count; item++) items.Add(new DesignSampleItem(item));
        return items;
    }

    /// <summary>
    /// Loads a design data XAML file as its build action DesignDataWithDesignTimeCreatableTypes does: the objects it
    /// declares are constructed and their properties set. Only a file inside the project is read.
    /// </summary>
    private (object? Value, string Kind, string? TypeName, string Summary)? DesignDataFile(XObject source, DesignMarkup markup, Assembly? assembly, PreviewRequest request)
    {
        string? location = markup.Argument("Source", 0);
        if (string.IsNullOrWhiteSpace(location) || markup.Positional.Count > 1 || markup.Named.Keys.Any(key => key != "Source"))
        {
            DesignDiagnostic(source, "d:DesignData takes a Source naming a XAML file in the project; the authored runtime value is retained.");
            return null;
        }
        try
        {
            string path = DesignDataPath(location!, request);
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException($"The file was not found at {path}.");
            if (file.Length > MaximumDesignDataFileLength) throw new InvalidOperationException("The file is larger than 2 MB.");
            using var reader = XmlReader.Create(new StringReader(File.ReadAllText(path)), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumDesignDataFileLength
            }, new Uri(path, UriKind.Absolute).AbsoluteUri);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.SetBaseUri);
            var root = document.Root ?? throw new InvalidOperationException("The file has no root element.");
            if (assembly is not null) QualifyProjectNamespaces(root, assembly);
            using var markupReader = document.CreateReader();
            object value = System.Windows.Markup.XamlReader.Load(markupReader);
            string name = value.GetType().FullName ?? value.GetType().Name;
            return (value, PreviewDesignDataKinds.File, name, $"Design data: {name} loaded from {Path.GetFileName(path)}; the objects it declares are constructed.");
        }
        // The file is the project's and so are the types it constructs: a missing file, a type that needs the
        // running application and a property with no setter all end here, with the view still shown.
        catch (Exception exception)
        {
            DesignDiagnostic(source, $"Design data '{location}' could not be loaded, so none is shown: {exception.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>The file a Source names: from the project root when it starts with a slash (or is a component path), otherwise beside the view.</summary>
    private static string DesignDataPath(string location, PreviewRequest request)
    {
        string relative = location.Replace('\\', '/');
        int component = relative.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        bool rooted = relative.StartsWith('/') || component >= 0;
        if (component >= 0) relative = relative.Substring(component + ";component/".Length);
        relative = relative.TrimStart('/');
        if (relative.Length == 0 || relative.Contains(':') || !relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source must be a relative path to a .xaml file.");
        string? project = string.IsNullOrWhiteSpace(request.ProjectDirectory) ? null : Path.GetFullPath(request.ProjectDirectory);
        string? beside = Path.IsPathFullyQualified(request.Path) ? Path.GetDirectoryName(Path.GetFullPath(request.Path)) : null;
        string start = (rooted ? project ?? beside : beside ?? project) ?? throw new InvalidOperationException("The view has no saved location to resolve the path from.");
        string path = Path.GetFullPath(Path.Combine(start, relative));
        // A file beside the view was the documented fallback for a rooted path that is not at the project root.
        if (!File.Exists(path) && project is not null && beside is not null && Path.GetFullPath(Path.Combine(rooted ? beside : project, relative)) is { } other && File.Exists(other)) path = other;
        string boundary = (project ?? beside)!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Source must name a file inside the project.");
        return path;
    }

    private static bool TryReadFlag(DesignMarkup markup, string name, out bool value)
    {
        value = false;
        return !markup.Named.TryGetValue(name, out string? text) || bool.TryParse(text.Trim(), out value);
    }

    /// <summary>The type an attribute value names, written as <c>prefix:Name</c> or <c>{x:Type prefix:Name}</c>.</summary>
    private static Type? ResolveTypeValue(XElement element, string text, Assembly? assembly)
    {
        text = text.Trim();
        if (text.StartsWith('{'))
        {
            if (DesignMarkup.Parse(text) is not { Name: "Type" } type || NamespaceOf(element, type.Prefix) != Xaml || (type.Argument("TypeName", 0) ?? type.Argument("Type")) is not { } inner) return null;
            text = inner.Trim();
        }
        int colon = text.IndexOf(':');
        string local = text.Substring(colon + 1);
        if (local.Length == 0 || NamespaceOf(element, colon < 0 ? null : text.Substring(0, colon)) is not { } scope) return null;
        try { return ResolveType(scope.NamespaceName, local, assembly); }
        // A mapping that names an assembly the build does not contain. The caller reports the type as not loadable.
        catch (Exception exception) when (exception is IOException or BadImageFormatException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Gives a view that declares no DataContext sample data from the view model its name pairs it with. Most
    /// views get theirs from code-behind or a container, which a source preview does not run.
    /// </summary>
    private void InferDataContext(XElement root, PreviewRequest request, Assembly? assembly, XNamespace sourceNamespace, string prefix)
    {
        if (!request.UseDesignTimeValues || !request.InferDesignData || assembly is null || ClassName is null || request.Scenario?.DataContextFactory is not null) return;
        if (ResolveType(root.Name, assembly) is not { } type || !typeof(FrameworkElement).IsAssignableFrom(type)) return;
        if (root.Attribute("DataContext") is not null || root.Elements().Any(child => child.Name.LocalName.EndsWith(".DataContext", StringComparison.Ordinal))) return;
        var (viewModel, ambiguity) = DesignDataConventions.FindViewModel(ClassName, assembly);
        if (viewModel is null)
        {
            if (ambiguity is not null)
                DesignDiagnostic(root, $"Several view models match this view by name ({ambiguity}), so no sample data was chosen. Declare d:DataContext to pick one.", "Information");
            return;
        }
        object? value;
        try { value = (_samples ??= new DesignSampleFactory(assembly)).Create(viewModel); }
        catch (Exception) { value = null; }
        if (value is null) return;
        string name = viewModel.FullName ?? viewModel.Name;
        string summary = $"Design data: sample values generated from {name}, matched to this view by name; its constructor was not run.";
        root.SetAttributeValue("DataContext", RegisterDesignValue(value, prefix));
        if (root.Attribute(sourceNamespace + "PreviewSource.Id") is { } identity)
        {
            if (!_designProperties.TryGetValue(identity.Value, out var properties)) _designProperties[identity.Value] = properties = [];
            properties.Add(FrameworkElement.DataContextProperty);
        }
        var line = (IXmlLineInfo)root;
        DesignData.Add(new PreviewDesignData("DataContext", PreviewDesignDataKinds.Inferred, name, summary,
            line.HasLineInfo() ? line.LineNumber : null, line.HasLineInfo() ? line.LinePosition : null, viewModel.Assembly.GetName().Name));
        DesignDiagnostic(root, summary + " Declare d:DataContext to choose the type yourself, or turn off inferred sample data in the preview settings.", "Information");
    }
}
