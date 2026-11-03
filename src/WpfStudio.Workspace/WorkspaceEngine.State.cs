using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Completion;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

// One lock on purpose. These groups are cohesive, but no request reads just one of them: GetDocument needs the page state, a buffer
// version and the solution to agree, an edit replaces all three and invalidates the caches in one step, and a diagnostics pass may only
// start for the solution that is current. With a lock per group each of those would take several locks in one order or see a mix of
// two generations, so the groups share _gate and check it in Debug.
public sealed partial class WorkspaceEngine
{
    private readonly object _gate = new();
    private readonly SyncedDocuments _documents;
    private readonly WorkspaceLifetime _lifetime;
    private readonly PageProjectionState _pages;
    private readonly DiskModelState _disk;
    private readonly XamlOwnership _ownership;
    private readonly XamlBatchCache _batchCache;
    private readonly ProjectDiagnosticsState _projectDiagnostics;
    private readonly LatestRequestEntries<CompletionItem> _completions;
    private readonly LatestRequestEntries<CodeAction> _codeActions;

    public WorkspaceEngine()
    {
        _documents = new(_gate);
        _lifetime = new(_gate);
        _pages = new(_gate);
        _disk = new(_gate);
        _ownership = new(_gate);
        _batchCache = new(_gate);
        _projectDiagnostics = new(_gate);
        _completions = new(_gate);
        _codeActions = new(_gate);
    }

    // What requests answer from, and the generation every snapshot of it is checked against.
    private Solution? CurrentSolution { get { AssertGateHeld(); return field; } set { AssertGateHeld(); field = value; } }
    private long SemanticRevision { get { AssertGateHeld(); return field; } set { AssertGateHeld(); field = value; } }

    private void AssertGateHeld() => Debug.Assert(Monitor.IsEntered(_gate));

    // A change in any file can change the semantic context of a completion.
    private void SemanticsChanged()
    {
        SemanticRevision++;
        _completions.Clear();
        _batchCache.Clear();
    }

    private abstract class GatedState(object gate)
    {
        protected void Held() => Debug.Assert(Monitor.IsEntered(gate));
    }

    /// <summary>The buffers the client synchronized. One entry per path, so a version always identifies its text.</summary>
    private sealed class SyncedDocuments(object gate) : GatedState(gate)
    {
        private readonly Dictionary<string, (long Version, string Text)> _open = new(StringComparer.OrdinalIgnoreCase);

        public bool IsOpen(string path) { Held(); return _open.ContainsKey(path); }
        public long? OpenVersion(string path) { Held(); return _open.TryGetValue(path, out var document) ? document.Version : null; }
        /// <summary>A document the client has not opened is at version 0.</summary>
        public long Version(string path) => OpenVersion(path) ?? 0;
        public string? Text(string path) { Held(); return _open.TryGetValue(path, out var document) ? document.Text : null; }
        public void Set(string path, long version, string text) { Held(); _open[path] = (version, text); }
        public void Remove(string path) { Held(); _open.Remove(path); }
        public void Clear() { Held(); _open.Clear(); }
        public Dictionary<string, long> Versions() { Held(); return _open.ToDictionary(pair => pair.Key, pair => pair.Value.Version, StringComparer.OrdinalIgnoreCase); }
        public XamlDocumentOverlay[] XamlOverlays()
        {
            Held();
            return _open.Where(pair => pair.Key.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Select(pair => new XamlDocumentOverlay(pair.Key, pair.Value.Text, pair.Value.Version)).ToArray();
        }
    }

    /// <summary>The MSBuild workspaces the engine owns and the loads replacing them.</summary>
    private sealed class WorkspaceLifetime(object gate) : GatedState(gate)
    {
        private Microsoft.CodeAnalysis.Workspace? _current;
        // Only ever exchanged whole and compared with nothing, so it needs no lock.
        private Acquired? _prepared;
        private int _loadsInProgress;
        private bool _loadAttempted;
        private bool _inventoryReady;

        public bool IsLoading { get { Held(); return _loadsInProgress != 0; } }
        public bool IsInventoryMissing { get { Held(); return _loadsInProgress != 0 || _loadAttempted && !_inventoryReady; } }
        public void BeginLoad() { Held(); _loadAttempted = true; BeginRefresh(); }
        public void BeginRefresh() { Held(); _inventoryReady = false; _loadsInProgress++; }
        public void InventoryPublished(bool ready) { Held(); _inventoryReady = ready; }
        public void EndLoad() { Held(); _loadsInProgress--; }
        // Returned for the caller to dispose: closing a workspace does not belong under the lock.
        public Microsoft.CodeAnalysis.Workspace? ReplaceWorkspace(Microsoft.CodeAnalysis.Workspace? next) { Held(); var previous = _current; _current = next; return previous; }
        public Acquired? ExchangePrepared(Acquired? next) => Interlocked.Exchange(ref _prepared, next);
    }

    private sealed class PageProjectionState(object gate) : GatedState(gate)
    {
        // Serializes builds; it is awaited, so never while the gate is held.
        public SemaphoreSlim BuildGate { get; } = new(1, 1);
        // Authored buffers and actual MSBuild documents are the inputs. Editor-derived page
        // partials exist only in CurrentSolution, never in the workspace or on disk.
        public Solution? Authored { get { Held(); return field; } set { Held(); field = value; } }
        public XamlPageSemanticProjectionResult? Result { get { Held(); return field; } private set { Held(); field = value; } }
        public bool Pending { get { Held(); return field; } private set { Held(); field = value; } }
        public long Epoch { get { Held(); return field; } private set { Held(); field = value; } }
        public long LoadEpoch { get { Held(); return field; } private set { Held(); field = value; } }
        public Dictionary<string, string> Texts { get { Held(); return field; } } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ReadStatuses { get { Held(); return field; } } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> NameStatuses { get { Held(); return field; } } = new(StringComparer.OrdinalIgnoreCase);

        public void Invalidate() { Pending = true; Epoch++; }

        public void Published(XamlPageSemanticProjectionResult result)
        {
            Result = result;
            Pending = false;
            NameStatuses.Clear();
        }

        public void Reset()
        {
            Authored = null;
            Result = null;
            Texts.Clear();
            ReadStatuses.Clear();
            NameStatuses.Clear();
            Pending = false;
            Epoch++;
            LoadEpoch++;
        }
    }

    /// <summary>The sweep that reconciles closed C# documents with what is on disk.</summary>
    private sealed class DiskModelState(object gate) : GatedState(gate)
    {
        // Serializes sweeps; it is awaited, so never while the gate is held.
        public SemaphoreSlim RefreshGate { get; } = new(1, 1);
        public Dictionary<string, DocumentId[]> Documents { get { Held(); return field; } } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Unavailable { get { Held(); return field; } } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Pending { get { Held(); return field; } } = new(StringComparer.OrdinalIgnoreCase);
        public string? Cursor { get { Held(); return field; } set { Held(); field = value; } }

        public void Settled(string path) { Unavailable.Remove(path); Pending.Remove(path); }

        public void Reset()
        {
            Documents.Clear();
            Unavailable.Clear();
            Pending.Clear();
            Cursor = null;
        }
    }

    // Neither replayed open buffers nor fallback directory listings define
    // project ownership: these inventories come from the evaluated workspace only.
    private sealed class XamlOwnership(object gate) : GatedState(gate)
    {
        public Dictionary<ProjectId, string[]> Files { get { Held(); return field; } } = [];
        public Dictionary<ProjectId, ResourceInventory> Resources { get { Held(); return field; } } = [];
        public HashSet<ProjectId> Unavailable { get { Held(); return field; } } = [];
        public Dictionary<string, HashSet<ProjectId>> Owners { get { Held(); return field; } } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsEvaluated(ProjectId project) => !Unavailable.Contains(project) && Files.ContainsKey(project);

        // Owners stay until the next solution replaces them.
        public void Reset()
        {
            Files.Clear();
            Resources.Clear();
            Unavailable.Clear();
        }
    }

    /// <summary>Bounded results of batch XAML analysis, which only ever adds them for the current revision.</summary>
    private sealed class XamlBatchCache(object gate) : GatedState(gate)
    {
        private readonly Dictionary<BatchCacheKey, BatchAnalysis> _entries = [];

        public BatchAnalysis? Find(BatchCacheKey key) { Held(); return _entries.GetValueOrDefault(key); }
        public void Clear() { Held(); _entries.Clear(); }

        public void Add(BatchCacheKey key, BatchAnalysis analysis)
        {
            Held();
            while (_entries.Count > 0 && (_entries.Count >= 128 || _entries.Values.Sum(value => value.Diagnostics.Length) + analysis.Diagnostics.Length > 8192))
                _entries.Remove(_entries.Keys.First());
            _entries[key] = analysis;
        }
    }

    private sealed class ProjectDiagnosticsState(object gate) : GatedState(gate)
    {
        public Dictionary<ProjectId, ProjectDiagnosticsPass> Passes { get { Held(); return field; } } = [];
        public Dictionary<string, ReportedDiagnostics> Reported { get { Held(); return field; } } = new(StringComparer.OrdinalIgnoreCase);

        // A pass that finished stays valid for its compilation; one still running would only be discarded.
        public void CancelRunning()
        {
            foreach (var pass in Passes.Values)
                if (pass.Diagnostics is null) pass.Cancellation.Cancel();
        }

        public void Reset()
        {
            CancelRunning();
            Passes.Clear();
            Reported.Clear();
        }
    }

    /// <summary>The most recent request's entries only, with the solution snapshot each was computed from.</summary>
    private sealed class LatestRequestEntries<T>(object gate) : GatedState(gate)
    {
        private readonly ConcurrentDictionary<string, (Document Document, long Version, T Item)> _entries = new();
        private long _request;

        public long Begin() { Held(); _entries.Clear(); return ++_request; }
        public bool IsLatest(long request) { Held(); return request == _request; }
        public string Add(Document document, long version, T item) { Held(); var id = Guid.NewGuid().ToString("N"); _entries[id] = (document, version, item); return id; }
        public void Clear() { Held(); _entries.Clear(); }
        // Takes no lock: whoever uses an entry checks its solution against the current one under the gate.
        public bool TryGet(string id, out (Document Document, long Version, T Item) entry) => _entries.TryGetValue(id, out entry);
        public bool Contains(string id) { Held(); return _entries.ContainsKey(id); }
    }
}
