using System.Text.RegularExpressions;
using System.Xml;

namespace WpfStudio.PreviewDependencies;

/// <summary>Namespaces a document declares (loaded when a library defines them) and those its markup uses (reported when none does).</summary>
internal sealed record XamlNamespaceUse(IReadOnlyCollection<string> Declared, IReadOnlyCollection<string> Used);

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
        using var reader = XmlReader.Create(new System.IO.StringReader(xaml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
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

    private const string PrefixInValuePattern = @"(?<![\w.:-])([A-Za-z_][\w.-]*):(?=[A-Za-z_])";
#if NETFRAMEWORK
    private static readonly Regex PrefixInValueRegex = new(PrefixInValuePattern, RegexOptions.CultureInvariant);
    private static Regex PrefixInValue() => PrefixInValueRegex;
#else
    [GeneratedRegex(PrefixInValuePattern)]
    private static partial Regex PrefixInValue();
#endif

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
