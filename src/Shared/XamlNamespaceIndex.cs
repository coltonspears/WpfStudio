using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml;

namespace WpfStudio.PreviewDependencies;

/// <summary>The XAML namespaces an assembly declares through XmlnsDefinition and XmlnsCompatibleWith, read from metadata.</summary>
internal sealed record XamlNamespaceLibrary(string Path, string Name, IReadOnlyList<string> Namespaces, IReadOnlyList<KeyValuePair<string, string>> Compatible);

/// <summary>Namespaces a document declares (loaded when a library defines them) and those its markup uses (reported when none does).</summary>
internal sealed record XamlNamespaceUse(IReadOnlySet<string> Declared, IReadOnlySet<string> Used);

internal sealed record XamlNamespaceSelection(IReadOnlyList<XamlNamespaceLibrary> Libraries, IReadOnlyList<string> Unresolved);

// Compiled into both sides of the preview transport so the selection rules can be tested without starting WPF.
internal static partial class XamlNamespaceIndex
{
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    // Namespaces WPF itself defines or the XAML reader handles without a library.
    private static readonly HashSet<string> Builtin = new(StringComparer.Ordinal)
    {
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation", "http://schemas.microsoft.com/winfx/2006/xaml",
        "http://schemas.microsoft.com/netfx/2007/xaml/presentation", "http://schemas.microsoft.com/netfx/2009/xaml/presentation",
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation/options", "http://schemas.microsoft.com/winfx/2006/xaml/composite-font",
        "http://schemas.microsoft.com/expression/blend/2008", MarkupCompatibility, "http://www.w3.org/XML/1998/namespace"
    };

    public static XamlNamespaceUse Read(string xaml)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ignorable = new HashSet<string>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(new StringReader(xaml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            used.Add(reader.NamespaceURI);
            var values = new List<string>();
            string? ignore = null;
            while (reader.MoveToNextAttribute())
            {
                if (reader.Prefix == "xmlns" || reader.Name == "xmlns") { declared.Add(reader.Value); continue; }
                if (reader.NamespaceURI.Length > 0) used.Add(reader.NamespaceURI);
                if (reader.NamespaceURI == MarkupCompatibility && reader.LocalName == "Ignorable") ignore = reader.Value;
                else values.Add(reader.Value);
            }
            reader.MoveToElement();
            // Types also appear in values: {vendor:Extension}, TargetType="vendor:Gauge", x:Type vendor:Gauge, and so on.
            foreach (var value in values)
                foreach (Match match in PrefixInValue().Matches(value))
                    if (reader.LookupNamespace(match.Groups[1].Value) is { } uri) used.Add(uri);
            foreach (var prefix in (ignore ?? "").Split(' ', '\t', '\r', '\n'))
                if (prefix.Length > 0 && reader.LookupNamespace(prefix) is { } uri) ignorable.Add(uri);
        }
        bool Library(string uri) => uri.Length > 0 && !Builtin.Contains(uri) && !ignorable.Contains(uri)
            && !uri.StartsWith("clr-namespace:", StringComparison.Ordinal);
        return new(declared.Where(Library).ToHashSet(StringComparer.Ordinal), used.Where(Library).ToHashSet(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"(?<![\w.:-])([A-Za-z_][\w.-]*):(?=[A-Za-z_])")]
    private static partial Regex PrefixInValue();

    /// <summary>
    /// Reads an assembly's XAML namespace declarations without loading it. Returns null for files that are not
    /// assemblies, reference assemblies, Visual Studio design-time assemblies, and assemblies that declare no namespace.
    /// </summary>
    public static XamlNamespaceLibrary? ReadLibrary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) return null;
            // Design-time parts reference the Visual Studio designer, which is never present at run time.
            foreach (var handle in metadata.AssemblyReferences)
            {
                string reference = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (reference.StartsWith("Microsoft.VisualStudio.DesignTools.", StringComparison.Ordinal)
                    || reference.StartsWith("Microsoft.Windows.Design.", StringComparison.Ordinal)) return null;
            }
            var namespaces = new List<string>();
            var compatible = new List<KeyValuePair<string, string>>();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                string typeNamespace = metadata.GetString(type.Namespace), typeName = metadata.GetString(type.Name);
                if (typeNamespace == "System.Runtime.CompilerServices" && typeName == "ReferenceAssemblyAttribute") return null;
                if (typeNamespace != "System.Windows.Markup") continue;
                var value = metadata.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1) continue;
                if (typeName == "XmlnsDefinitionAttribute" && value.ReadSerializedString() is { Length: > 0 } uri) namespaces.Add(uri);
                else if (typeName == "XmlnsCompatibleWithAttribute" && value.ReadSerializedString() is { Length: > 0 } old
                    && value.ReadSerializedString() is { Length: > 0 } current) compatible.Add(new(old, current));
            }
            if (namespaces.Count == 0 && compatible.Count == 0) return null;
            string name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            return new(path, name, namespaces.Distinct(StringComparer.Ordinal).ToArray(), compatible);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The libraries to load for a document, one per assembly name in the given order, and the used namespaces that
    /// neither those libraries nor <paramref name="defined"/> (already loaded assemblies) declare.
    /// </summary>
    public static XamlNamespaceSelection Select(XamlNamespaceUse use, IEnumerable<XamlNamespaceLibrary> libraries, IEnumerable<string> defined)
    {
        var candidates = libraries.ToArray();
        // XmlnsCompatibleWith maps a retired namespace to its replacement, possibly through several versions.
        var replacements = candidates.SelectMany(library => library.Compatible).GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Value).ToArray(), StringComparer.Ordinal);
        HashSet<string> Expand(IEnumerable<string> uris)
        {
            var result = new HashSet<string>(uris, StringComparer.Ordinal);
            var pending = new Queue<string>(result);
            while (pending.TryDequeue(out var uri) && result.Count < 1024)
                foreach (var replacement in replacements.GetValueOrDefault(uri, []))
                    if (result.Add(replacement)) pending.Enqueue(replacement);
            return result;
        }
        var wanted = Expand(use.Declared);
        var selected = new List<XamlNamespaceLibrary>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in candidates)
            if (library.Namespaces.Any(wanted.Contains) && names.Add(library.Name)) selected.Add(library);
        var available = new HashSet<string>(defined, StringComparer.Ordinal);
        available.UnionWith(selected.SelectMany(library => library.Namespaces));
        var unresolved = use.Used.Where(uri => !Expand([uri]).Any(available.Contains)).Order(StringComparer.Ordinal).ToArray();
        return new(selected, unresolved);
    }
}
