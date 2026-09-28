namespace WpfStudio.Workspace.Xaml;

/// <summary>A URI identity lookup. A returned identity does not imply that its text was captured.</summary>
public sealed record XamlResourceResolution(XamlResourceDocument? Document, string? Status = null, bool CoverageLimited = false);

/// <summary>Immutable evaluated URI metadata, independent of the operation's captured text budget.</summary>
public sealed class XamlResourceIndex
{
    public const int MaximumDocuments = 16_384;
    private const int MaximumMetadataCharacters = 16_000_000;
    private readonly Dictionary<Key, List<XamlResourceDocument>> _logical = new(KeyComparer.Instance);
    private readonly Dictionary<Key, List<XamlResourceDocument>> _physical = new(KeyComparer.Instance);
    private readonly Dictionary<string, HashSet<string>> _assemblies = new(StringComparer.OrdinalIgnoreCase);

    public XamlResourceIndex(IReadOnlyList<XamlResourceDocument> Documents, bool IsComplete = true, string? Status = null,
        CancellationToken token = default)
    {
        var retained = new List<XamlResourceDocument>(Math.Min(Documents.Count, MaximumDocuments));
        long characters = 0;
        int visited = 0;
        bool limited = false;
        foreach (var document in Documents)
        {
            token.ThrowIfCancellationRequested();
            if (++visited > MaximumDocuments) { limited = true; break; }
            if (!Valid(document.Path, 4096) || !Valid(document.AssemblyName, 2048) || !Valid(document.ResourcePath, 4096) || !Valid(document.Kind, 64))
            { limited = true; continue; }
            characters += (long)document.Path.Length + document.AssemblyName.Length + document.ResourcePath.Length + document.Kind.Length;
            if (characters > MaximumMetadataCharacters) { limited = true; break; }
            // Do not retain source text through the metadata catalog. Text/version/status
            // belong to a captured document, not to an evaluated URI identity.
            var identity = new XamlResourceDocument(document.Path, null, document.AssemblyName, document.ResourcePath, document.Kind);
            retained.Add(identity);
            Add(_logical, new(identity.AssemblyName, NormalizeResourcePath(identity.ResourcePath)), identity);
            Add(_physical, new(identity.AssemblyName, NormalizePhysicalPath(identity.Path)), identity);
            string name = SimpleAssembly(identity.AssemblyName);
            if (!_assemblies.TryGetValue(name, out var assemblies)) _assemblies[name] = assemblies = new(StringComparer.OrdinalIgnoreCase);
            assemblies.Add(identity.AssemblyName);
        }
        this.Documents = retained.AsReadOnly();
        this.IsComplete = IsComplete && !limited;
        this.Status = limited ? "The evaluated resource identity catalog exceeds its metadata budget or contains unsupported identities." : Status;
    }

    public IReadOnlyList<XamlResourceDocument> Documents { get; }
    public bool IsComplete { get; }
    public string? Status { get; }

    public XamlResourceResolution FindDocument(string path, string assembly, string? kind = null)
    {
        if (!IsComplete) return new(null, Status ?? "An incomplete resource identity catalog cannot establish a unique declaring document.", true);
        if (!_physical.TryGetValue(new(assembly, NormalizePhysicalPath(path)), out var entries))
            return new(null, "The declaring document has no evaluated resource identity.", !IsComplete);
        var matches = (kind is null ? entries : entries.Where(document => document.Kind == kind)).Take(2).ToArray();
        return matches.Length == 1 ? new(matches[0]) : new(null, "The declaring document has no unique evaluated resource identity.", !IsComplete);
    }

    /// <summary>The single URI implementation used by dependency capture and semantic lookup.</summary>
    public XamlResourceResolution ResolveSource(XamlResourceDocument origin, string value, string sourceAssembly, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!IsComplete) return new(null, Status ?? "The evaluated resource identity catalog is incomplete.", true);
        if (value.Length > 4096 || value.Length == 0 || value.Trim() != value || value.Contains('{') || value.Contains('?') || value.Contains('#'))
            return new(null, "The dictionary Source URI is unsupported or unfinished.");
        if (!_physical.TryGetValue(new(origin.AssemblyName, NormalizePhysicalPath(origin.Path)), out var origins) || !origins.Any(candidate => SameIdentity(candidate, origin)))
            return new(null, "The declaring document is not in the evaluated resource catalog.");
        string uri = value.Replace('\\', '/');
        if (uri.StartsWith("//", StringComparison.Ordinal)) return new(null, "Network dictionary URIs are not evaluated project resources.");
        string assembly = origin.AssemblyName;
        bool absolute = uri.StartsWith('/');
        const string pack = "pack://application:,,,/";
        if (uri.StartsWith(pack, StringComparison.OrdinalIgnoreCase)) { uri = uri[pack.Length..]; absolute = true; }
        else if (uri.Contains(':')) return new(null, "Only evaluated application resource URIs are supported.");
        uri = uri.TrimStart('/');
        int component = uri.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (component >= 0)
        {
            string requestedAssembly = uri[..component];
            if (requestedAssembly.Length == 0 || requestedAssembly.Contains(';'))
                return new(null, "Version-qualified component URI resolution is unavailable.");
            if (!_assemblies.TryGetValue(requestedAssembly, out var assemblies) || assemblies.Count != 1)
                return new(null, "The component assembly identity is unavailable or ambiguous.");
            assembly = assemblies.Single(); uri = uri[(component + ";component/".Length)..]; absolute = true;
        }
        else if (absolute) assembly = sourceAssembly;
        string logical;
        try
        {
            string basePath = absolute ? "" : NormalizeResourcePath(origin.ResourcePath);
            if (!absolute && basePath.Length == 0) return new(null, "The declaring document has no evaluated logical resource path.");
            var baseUri = new Uri("https://wpf-resources.invalid/" + string.Join('/', basePath.Split('/').Select(Uri.EscapeDataString)));
            var combined = new Uri(baseUri, uri);
            if (combined.Host != baseUri.Host || combined.Scheme != baseUri.Scheme) return new(null, "The dictionary URI leaves the evaluated resource scope.");
            logical = Uri.UnescapeDataString(combined.AbsolutePath).TrimStart('/');
        }
        catch (UriFormatException) { return new(null, "The dictionary Source URI is malformed."); }
        if (!_logical.TryGetValue(new(assembly, logical), out var matches))
            return new(null, "The dictionary URI is not available in the evaluated resource catalog.");
        if (matches.Count != 1) return new(null, "Multiple evaluated resources have the same URI.");
        var document = matches[0];
        if (document.Kind is not ("Page" or "Resource" or "Content")) return new(null, "The URI does not identify a supported evaluated resource item.");
        if (document.Kind == "Content" && !StringComparer.OrdinalIgnoreCase.Equals(document.AssemblyName, sourceAssembly))
            return new(null, "Content resources in referenced assemblies are not supported by WPF pack URIs.");
        return new(document);
    }

    internal static bool SameIdentity(XamlResourceDocument first, XamlResourceDocument second) =>
        StringComparer.OrdinalIgnoreCase.Equals(first.AssemblyName, second.AssemblyName) &&
        StringComparer.OrdinalIgnoreCase.Equals(NormalizePhysicalPath(first.Path), NormalizePhysicalPath(second.Path)) &&
        StringComparer.OrdinalIgnoreCase.Equals(NormalizeResourcePath(first.ResourcePath), NormalizeResourcePath(second.ResourcePath)) && first.Kind == second.Kind;
    internal static string NormalizePhysicalPath(string path) => path.Replace('\\', '/');
    internal static string NormalizeResourcePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string SimpleAssembly(string name) => name.Split(',')[0].Trim();
    private static bool Valid(string value, int maximum) => !string.IsNullOrEmpty(value) && value.Length <= maximum && !value.Contains('\0');
    private static void Add(Dictionary<Key, List<XamlResourceDocument>> map, Key key, XamlResourceDocument value)
    {
        if (!map.TryGetValue(key, out var entries)) map[key] = entries = [];
        entries.Add(value);
    }
    private readonly record struct Key(string Assembly, string Path);
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static KeyComparer Instance { get; } = new();
        public bool Equals(Key x, Key y) => StringComparer.OrdinalIgnoreCase.Equals(x.Assembly, y.Assembly) && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);
        public int GetHashCode(Key obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Assembly), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
    }
}
