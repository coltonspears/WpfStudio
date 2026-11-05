using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.App.Features.XamlAnalysis;

/// <summary>
/// Keeps project-wide XAML diagnostics current: it watches the workspace's source files, synchronizes open buffers
/// into the language worker, runs the worker's project analysis through a debounced single-flight scheduler, and
/// decides which diagnostics (project, editor, index) still describe the text they were computed from.
/// </summary>
public sealed partial class ProjectXamlAnalysisCoordinator : ObservableObject, IDisposable
{
    private const string NoWorkspaceStatus = "Open a workspace to analyze project XAML.";
    private readonly IProjectXamlAnalysisHost _host;
    private readonly DocumentStore _store;
    private readonly WorkspaceClient _workspace;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;
    private readonly ProjectXamlAnalysisScheduler _scheduler;
    private readonly KnownSourceWatcherFactory _watchSources;
    private IKnownSourceWatcher? _sourceWatcher;
    private WorkspaceSnapshot? _diagnosticWorkspace;
    private IReadOnlyList<XamlFileAnalysisResult> _projectXamlFiles = [];
    // Something happened that the current results do not reflect, as opposed to a periodic check of the disk.
    private bool _scanDue = true;
    private readonly HashSet<DocumentState> _trackedDocuments = [];
    private readonly HashSet<string> _invalidIndexPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyModelPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _indexTextHashes = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexDiagnosticsInvalid;
    private sealed record DiagnosticOrigin(XamlFileAnalysisResult File, WorkspaceSnapshot Workspace, long SourceEpoch);
    private readonly ConditionalWeakTable<WorkspaceDiagnostic, DiagnosticOrigin> _diagnosticOrigins = new();
    private sealed class DocumentFingerprint { public long Version; public string Hash = ""; }
    private readonly ConditionalWeakTable<DocumentState, DocumentFingerprint> _documentHashes = new();
    private int _synchronizing;
    private int _retries;
    private long _indexRevision;
    private long _sourceEpoch;
    private bool _disposed;

    public ProjectXamlAnalysisCoordinator(IProjectXamlAnalysisHost host, DocumentStore store, WorkspaceClient workspace, IUiDispatcher dispatcher, ILogger logger, CancellationToken lifetime, KnownSourceWatcherFactory watchSources)
    {
        _host = host; _store = store; _workspace = workspace; _dispatcher = dispatcher; _logger = logger; _lifetime = lifetime; _watchSources = watchSources;
        _scheduler = new(async (generation, token) =>
        {
            Task work = Task.CompletedTask;
            await _dispatcher.InvokeAsync(() => work = ScanAsync(generation, token), token);
            await work;
        }, (generation, exception) => _dispatcher.Post(() =>
        {
            if (_disposed || !_scheduler!.IsCurrent(generation)) return;
            IsRunning = false;
            Status = "Project XAML analysis unavailable: " + exception.Message;
        }));
        _workspace.SemanticStateChanged += WorkerSemanticStateChanged;
        _workspace.CSharpBodyChanged += WorkerCSharpBodyChanged;
    }

    [ObservableProperty] public partial string Status { get; set; } = NoWorkspaceStatus;
    [ObservableProperty] public partial bool IsRunning { get; set; }

    /// <summary>Project files changed since the workspace was loaded; XAML contexts are stale until it is reloaded.</summary>
    public bool ProjectReloadRequired { get; private set; }
    /// <summary>Changed C# files are still being synchronized, so project types may be stale.</summary>
    public bool ProjectTypesPending { get; private set; }
    /// <summary>True when open XAML editors can rely on the project model.</summary>
    public bool XamlContextsAvailable => !ProjectReloadRequired && !ProjectTypesPending;
    /// <summary>Bumped whenever the WPF index must be recomputed.</summary>
    public long IndexRevision => Volatile.Read(ref _indexRevision);
    /// <summary>Bumped whenever source text that diagnostics were computed from may have changed.</summary>
    public long SourceEpoch => Volatile.Read(ref _sourceEpoch);

    // ------------------------------------------------------------------ Workspace lifecycle

    /// <summary>Forgets the previous workspace's analysis and starts watching the new one.</summary>
    public void Reset(WorkspaceSnapshot? workspace)
    {
        _scheduler.Invalidate();
        Interlocked.Increment(ref _indexRevision);
        _sourceWatcher?.Dispose(); _sourceWatcher = null;
        if (!string.Equals(_diagnosticWorkspace?.Path, workspace?.Path, StringComparison.OrdinalIgnoreCase)) _host.ForgetIndexedState();
        _diagnosticWorkspace = workspace;
        ProjectReloadRequired = false;
        ProjectTypesPending = false;
        _dirtyModelPaths.Clear();
        _indexTextHashes.Clear();
        _host.InvalidateXamlResources();
        _host.XamlContextsChanged();
        _projectXamlFiles = [];
        IsRunning = false;
        if (workspace is not null) WatchSources(workspace);
        Request();
    }

    private void WatchSources(WorkspaceSnapshot workspace)
    {
        var files = workspace.Projects.SelectMany(project => project.Files)
            .Where(file => !file.IsGenerated && Path.GetExtension(file.Path).ToLowerInvariant() is ".cs" or ".xaml")
            .Select(file => file.Path).Concat(workspace.Projects.Select(project => project.ProjectPath)).Append(workspace.Path);
        _sourceWatcher = _watchSources(files, change =>
        {
            // Invalidate before dispatching: a completed scan must not race a
            // queued UI notification about changed source bytes.
            if (_disposed || !ReferenceEquals(workspace, _host.Workspace)) return;
            _scheduler.Invalidate();
            Interlocked.Increment(ref _indexRevision);
            Interlocked.Increment(ref _sourceEpoch);
            if (change.RequiresReload || change.Path is null || Path.GetExtension(change.Path).Equals(".xaml", StringComparison.OrdinalIgnoreCase))
                _host.InvalidateXamlResources();
            _dispatcher.Post(() =>
            {
                if (_disposed || !ReferenceEquals(workspace, _host.Workspace)) return;
                if (change.Path is null) _indexDiagnosticsInvalid = true; else _invalidIndexPaths.Add(change.Path);
                if (change.RequiresReload) ProjectReloadRequired = true;
                if (change.Path is null || Path.GetExtension(change.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) ProjectTypesPending = true;
                if (change.Path is { } changedPath && Path.GetExtension(changedPath).Equals(".cs", StringComparison.OrdinalIgnoreCase)) _dirtyModelPaths.Add(changedPath);
                if (ProjectReloadRequired || ProjectTypesPending) _host.XamlContextsChanged();
                Request();
            });
        }, reconcile: () => _dispatcher.Post(() =>
        {
            if (!_disposed && ReferenceEquals(workspace, _host.Workspace) && !IsRunning) _ = _scheduler.Schedule();
        }), contextFiles: ProjectContextFiles(workspace), failed: exception =>
        {
            _logger.LogError(exception, "Source change notification failed");
            _dispatcher.Post(() => { if (!_disposed) _host.AppendOutput("Source watcher: " + exception.Message); });
        });
    }

    private static IEnumerable<string> ProjectContextFiles(WorkspaceSnapshot workspace)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in workspace.Projects.Select(project => project.ProjectPath).Append(workspace.Path))
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(project)!); directory is not null; directory = directory.Parent)
        {
            if (!seen.Add(directory.FullName)) break;
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config" })
                yield return Path.Combine(directory.FullName, name);
        }
    }

    // ------------------------------------------------------------------ Open documents

    /// <summary>Starts following an open document; its edits request a new analysis.</summary>
    public void TrackDocument(DocumentState document)
    {
        if (_trackedDocuments.Add(document)) document.ContentChanged += TrackedDocumentChanged;
        if (document.Extension == ".xaml") _host.OpenXamlDocumentsChanged();
        Request();
    }

    public void UntrackDocument(DocumentState document)
    {
        if (_trackedDocuments.Remove(document)) document.ContentChanged -= TrackedDocumentChanged;
        if (document.Extension == ".xaml") _host.OpenXamlDocumentsChanged();
        Request();
    }

    private void TrackedDocumentChanged(object? sender, EventArgs args)
    {
        if (sender is DocumentState { Extension: ".xaml" })
        {
            _host.OpenXamlDocumentsChanged();
            Request();
        }
        else if (sender is DocumentState { Extension: ".cs" })
        {
            // Project XAML depends on a C# file's declarations, and the worker reports a change to those once it has the
            // text. Other typing leaves the results standing, except that a scan under way is comparing against old text.
            Interlocked.Increment(ref _indexRevision);
            if (IsRunning && _scanDue) _ = _scheduler.Schedule();
        }
    }

    // XAML cannot see a method body, but a symbol operation under review can: the edit may add a use of what it is renaming.
    private void WorkerCSharpBodyChanged(object? sender, string path) => _host.SemanticStateChanged();

    private void WorkerSemanticStateChanged(object? sender, EventArgs args)
    {
        _host.SemanticStateChanged();
        if (Volatile.Read(ref _synchronizing) != 0 || _disposed) return;
        _scheduler.Invalidate();
        Interlocked.Increment(ref _sourceEpoch);
        _dispatcher.Post(() => { if (!_disposed) Request(); });
    }

    /// <summary>The WPF index was rebuilt from current file texts, so index diagnostics are trustworthy again.</summary>
    public void MarkIndexRefreshed() { _invalidIndexPaths.Clear(); _indexTextHashes.Clear(); _indexDiagnosticsInvalid = false; }
    /// <summary>The WPF index no longer describes this file, so its index diagnostics stop applying until the index is rebuilt.</summary>
    public void MarkIndexStale(string path) => _invalidIndexPaths.Add(path);
    /// <summary>Queues a scan after the debounce without discarding the current results first.</summary>
    public void Schedule()
    {
        _scanDue = true;
        _ = _scheduler.Schedule();
    }

    // ------------------------------------------------------------------ Scanning

    /// <summary>Discards the current results and queues a new scan.</summary>
    public void Request()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _indexRevision);
        Interlocked.Increment(ref _sourceEpoch);
        _retries = 0;
        _scanDue = true;
        _projectXamlFiles = [];
        _host.DiagnosticsChanged();
        Status = ProjectReloadRequired ? "Project files changed. Reload the workspace to check XAML against the current project items."
            : _host.Workspace is null ? NoWorkspaceStatus
            : !_workspace.IsConnected ? "Project XAML analysis unavailable: the language worker is disconnected."
            : "Project XAML analysis pending…";
        _ = _scheduler.Schedule();
    }

    /// <summary>Scans now and waits for the newest scan to finish, even when events supersede the manual one.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        Request();
        await _scheduler.Schedule(immediate: true);
        // Watcher/semantic events may supersede a manual scan while it awaits
        // the worker. Await the newest request instead of reporting a stale idle.
        while (!_disposed)
        {
            var completion = _scheduler.Completion;
            await completion;
            if (ReferenceEquals(completion, _scheduler.Completion)) break;
        }
    }

    private async Task ScanAsync(long generation, CancellationToken token)
    {
        if (_disposed || _host.IsLoading || ProjectReloadRequired || _host.Workspace is not { } workspace || !_workspace.IsConnected) return;
        var documents = _host.Documents.Where(editor => editor.State.Extension is ".cs" or ".xaml")
            .Select(editor => (State: editor.State, Text: editor.State.Content, Version: editor.State.Version)).ToArray();
        bool Current() => !_disposed && _scheduler.IsCurrent(generation) && ReferenceEquals(workspace, _host.Workspace)
            && documents.All(document => _store.Find(document.State.Path) == document.State
                && document.State.Version == document.Version && document.State.Content == document.Text);
        if (!Current()) return;
        IsRunning = true;
        if (_scanDue) Status = "Analyzing project XAML…";
        try
        {
            RefreshDiskDocumentsResult disk;
            var dirtyModels = _dirtyModelPaths.ToArray();
            Interlocked.Increment(ref _synchronizing);
            try
            {
                foreach (var document in documents.Where(document => document.State.Extension == ".cs"))
                {
                    token.ThrowIfCancellationRequested();
                    await _workspace.UpdateDocumentAsync(new(document.State.Path, document.Text, document.Version, Analyze: false), token);
                    if (!Current()) return;
                }
                disk = dirtyModels.Length == 0 ? await _workspace.RefreshAllDiskDocumentsAsync(token)
                    : await _workspace.RefreshDiskDocumentsAsync(new(dirtyModels), token);
            }
            finally { Interlocked.Decrement(ref _synchronizing); }
            if (!Current()) return;
            if (!disk.Accepted)
            {
                Status = disk.Status ?? "Project source changed during synchronization; refreshing…";
                if (++_retries <= 2) _ = _scheduler.Schedule();
                return;
            }
            foreach (var path in dirtyModels) _dirtyModelPaths.Remove(path);
            if (disk.Changed)
            {
                Interlocked.Increment(ref _sourceEpoch);
                _projectXamlFiles = [];
                _host.DiagnosticsChanged();
            }
            // Once the sweep is complete, the worker's project/dependency gates
            // own unavailable models. One locked project must not block healthy
            // editors elsewhere in the workspace indefinitely.
            if (ProjectTypesPending && disk.PendingFiles == 0) { ProjectTypesPending = false; _host.XamlContextsChanged(); }
            // The periodic check only looks for what the watcher missed. If it found nothing, the results still stand.
            if (!_scanDue && !disk.Changed && disk.PendingFiles == 0) return;
            var overlays = documents.Where(document => document.State.Extension == ".xaml")
                .Select(document => new XamlDocumentOverlay(document.State.Path, document.Text, document.Version)).ToArray();
            var result = await _workspace.AnalyzeXamlProjectAsync(new(generation, overlays), token);
            if (!Current() || result.Generation != generation) return;
            if (!result.Accepted)
            {
                Status = result.Status ?? "Project types changed during analysis; refreshing…";
                if (++_retries <= 2) _ = _scheduler.Schedule();
                return;
            }
            _projectXamlFiles = result.Files;
            _scanDue = false;
            foreach (var file in result.Files)
            foreach (var diagnostic in file.Diagnostics)
            {
                _diagnosticOrigins.Remove(diagnostic);
                _diagnosticOrigins.Add(diagnostic, new(file, workspace, Volatile.Read(ref _sourceEpoch)));
            }
            int analyzed = result.Files.Count(file => file.State == "Analyzed");
            int unavailable = result.Files.Count - analyzed;
            Status = $"XAML checked: {analyzed}/{result.TotalFiles} file contexts analyzed"
                + (unavailable > 0 ? $" · {unavailable} unavailable" : "")
                + (result.Truncated || disk.Truncated ? " · partial coverage" : "")
                + (result.Status is { Length: > 0 } status ? ". " + status : ".")
                + (disk.Status is { Length: > 0 } diskStatus ? " " + diskStatus : "")
                + (_sourceWatcher?.Status is { } watcherStatus ? " " + watcherStatus : "");
            _host.DiagnosticsChanged();
            if (disk.PendingFiles > 0) _ = _scheduler.Schedule();
        }
        finally
        {
            // The scheduler is serial; clear busy before the next queued scan.
            if (!_disposed) IsRunning = false;
        }
    }

    // ------------------------------------------------------------------ Which diagnostics still apply

    /// <summary>Project diagnostics whose file is unchanged since the analysis (or not open at all).</summary>
    public IEnumerable<WorkspaceDiagnostic> CurrentProjectDiagnostics() => _projectXamlFiles
        .Where(file => file.State == "Analyzed" && (_store.Find(file.Path) is not { } document
            || file.Version == document.Version && file.TextHash == DocumentHash(document)))
        .SelectMany(file => file.Diagnostics);

    /// <summary>True when the project analysis already covers this XAML editor's current text, so its own diagnostics are redundant.</summary>
    public bool CoversEditor(EditorViewModel editor) => _projectXamlFiles.Any(file => file.State == "Analyzed"
        && file.Path.Equals(editor.State.Path, StringComparison.OrdinalIgnoreCase)
        && file.ProjectPath.Equals(editor.XamlProjectPath, StringComparison.OrdinalIgnoreCase)
        && file.Version == editor.State.Version && file.TextHash == DocumentHash(editor.State));

    /// <summary>True when the WPF index diagnostic was computed from text that has since changed.</summary>
    public bool IsIndexDiagnosticStale(WorkspaceDiagnostic issue)
    {
        if (_indexDiagnosticsInvalid || issue.Path is { } path && _invalidIndexPaths.Contains(path)) return true;
        var matches = _projectXamlFiles.Where(file => file.Path.Equals(issue.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return false;
        if (matches.Any(file => file.State is "Missing" or "Changed")) return true;
        if (_host.IndexTexts?.TryGetValue(issue.Path!, out var indexed) == true)
        {
            if (!_indexTextHashes.TryGetValue(issue.Path!, out var indexedHash)) _indexTextHashes[issue.Path!] = indexedHash = TextHash(indexed);
            if (matches.Any(file => file.TextHash is { } hash && hash != indexedHash)) return true;
        }
        return issue.Id == "XAML001" && matches.Any(file => file.State == "Analyzed");
    }

    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string DocumentHash(DocumentState document)
    {
        var fingerprint = _documentHashes.GetValue(document, _ => new());
        if (fingerprint.Version != document.Version)
        { fingerprint.Hash = TextHash(document.Content); fingerprint.Version = document.Version; }
        return fingerprint.Hash;
    }

    /// <summary>False when the file a project diagnostic was computed from has changed since, so navigating to it would mislead.</summary>
    public async Task<bool> VerifyDiagnosticAsync(WorkspaceDiagnostic diagnostic)
    {
        if (!_diagnosticOrigins.TryGetValue(diagnostic, out var origin) || diagnostic.Path is null) return true;
        var file = origin.File;
        var workspace = origin.Workspace;
        if (!ReferenceEquals(workspace, _host.Workspace) || Volatile.Read(ref _sourceEpoch) != origin.SourceEpoch) return false;
        var revision = Volatile.Read(ref _indexRevision);
        string text;
        if (_store.Find(file.Path) is { } document)
        {
            if (file.Version.HasValue && file.Version != document.Version) return false;
            text = document.Content;
        }
        else
        {
            if (await ProjectXamlDiagnosticText.ReadAsync(file.Path, _lifetime) is not { } read) return false;
            text = read;
        }
        return ReferenceEquals(workspace, _host.Workspace) && revision == Volatile.Read(ref _indexRevision)
            && file.TextHash is not null && file.TextHash == TextHash(text);
    }

    public bool IsOpenedDiagnosticCurrent(WorkspaceDiagnostic diagnostic, DocumentState document) =>
        !_diagnosticOrigins.TryGetValue(diagnostic, out var origin)
        || ReferenceEquals(origin.Workspace, _host.Workspace) && document.Path.Equals(origin.File.Path, StringComparison.OrdinalIgnoreCase)
            && TextHash(document.Content) == origin.File.TextHash;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.SemanticStateChanged -= WorkerSemanticStateChanged;
        _workspace.CSharpBodyChanged -= WorkerCSharpBodyChanged;
        _sourceWatcher?.Dispose(); _scheduler.Dispose();
        foreach (var document in _trackedDocuments) document.ContentChanged -= TrackedDocumentChanged;
        _trackedDocuments.Clear();
    }
}
