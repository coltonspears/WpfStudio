using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.Services;

/// <summary>One immutable set of user-open XAML buffers and its resource invalidation generation.</summary>
public sealed record XamlResourceSnapshot(long Generation, IReadOnlyList<XamlDocumentOverlay> Overlays);

public sealed class XamlResourceContext
{
    private readonly object _gate = new();
    private XamlResourceSnapshot _snapshot = new(0, []);
    public event EventHandler? Changed;

    public XamlResourceSnapshot Capture()
    {
        lock (_gate) return _snapshot;
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate) return _snapshot.Generation == generation;
    }

    // Called on the document-owning dispatcher. Cached DocumentStore entries
    // are deliberately excluded: only editors the user has open are overlays.
    public void SetOpenDocuments(IEnumerable<DocumentState> documents)
    {
        var overlays = Array.AsReadOnly(documents.Where(document => document.Extension == ".xaml")
            .Select(document => new XamlDocumentOverlay(document.Path, document.Content, document.Version)).ToArray());
        lock (_gate) _snapshot = new(_snapshot.Generation + 1, overlays);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Watchers can invalidate from a worker thread without reading UI collections.
    public void Invalidate()
    {
        lock (_gate) _snapshot = _snapshot with { Generation = _snapshot.Generation + 1 };
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
