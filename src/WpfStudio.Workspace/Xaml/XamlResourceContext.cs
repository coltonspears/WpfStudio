namespace WpfStudio.Workspace.Xaml;

/// <summary>An evaluated resource identity and its immutable source snapshot. Null text denotes unavailable input.</summary>
public sealed record XamlResourceDocument(string Path, string? Text, string AssemblyName, string ResourcePath,
    string Kind, string? Status = null, long? Version = null);

/// <summary>
/// Resource evidence supplied by the worker. The language service performs no file access;
/// linked resources retain their evaluated logical path and declaring assembly.
/// </summary>
public sealed class XamlResourceContext
{
    public XamlResourceContext(string SourcePath, string SourceAssembly, IReadOnlyList<XamlResourceDocument> Documents,
        string? ApplicationPath = null, bool IsComplete = true, string? Status = null, XamlResourceIndex? Index = null)
    {
        this.SourcePath = SourcePath;
        this.SourceAssembly = SourceAssembly;
        this.Documents = Array.AsReadOnly(Documents.ToArray());
        this.ApplicationPath = ApplicationPath;
        // Legacy callers can report incomplete resource coverage while still supplying
        // a proven application identity. Catalog truncation is a separate, stricter
        // condition; the worker supplies an explicit index for evaluated inventories.
        this.Index = Index ?? new XamlResourceIndex(Documents);
        this.IsComplete = IsComplete && this.Index.IsComplete;
        this.Status = Status ?? this.Index.Status;
        _captured = this.Documents.GroupBy(document => XamlResourceIndex.NormalizePhysicalPath(document.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public string SourcePath { get; }
    public string SourceAssembly { get; }
    public IReadOnlyList<XamlResourceDocument> Documents { get; }
    public string? ApplicationPath { get; }
    public bool IsComplete { get; }
    public string? Status { get; }
    public XamlResourceIndex Index { get; }

    private readonly Dictionary<string, XamlResourceDocument[]> _captured;
    internal XamlResourceDocument? Captured(XamlResourceDocument identity)
    {
        if (!_captured.TryGetValue(XamlResourceIndex.NormalizePhysicalPath(identity.Path), out var candidates)) return null;
        var matches = candidates.Where(document => XamlResourceIndex.SameIdentity(identity, document)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
