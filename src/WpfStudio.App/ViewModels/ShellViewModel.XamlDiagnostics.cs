using System.IO;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private ProjectXamlAnalysisScheduler? _projectAnalysis;
    private KnownSourceFileWatcher? _sourceWatcher;
    private WorkspaceSnapshot? _diagnosticWorkspace;
    private IReadOnlyList<XamlFileAnalysisResult> _projectXamlFiles = [];
    private readonly List<WorkspaceDiagnostic> _buildDiagnostics = [];
    private readonly HashSet<DocumentState> _diagnosticDocuments = [];
    private readonly HashSet<string> _invalidIndexPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyModelPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexDiagnosticsInvalid;
    private bool _projectReloadRequired;
    private bool _projectTypesPending;
    private sealed record DiagnosticOrigin(XamlFileAnalysisResult File, WorkspaceSnapshot Workspace, long SourceEpoch);
    private readonly ConditionalWeakTable<WorkspaceDiagnostic, DiagnosticOrigin> _projectDiagnosticOrigins = new();
    private sealed class DocumentFingerprint { public long Version; public string Hash = ""; }
    private readonly ConditionalWeakTable<DocumentState, DocumentFingerprint> _diagnosticTextHashes = new();
    private readonly Dictionary<string, string> _indexTextHashes = new(StringComparer.OrdinalIgnoreCase);
    private int _synchronizingProjectAnalysis;
    private int _projectAnalysisRetries;
    private long _wpfIndexRevision;
    private long _diagnosticSourceEpoch;
    [ObservableProperty] public partial string XamlAnalysisStatus { get; set; } = "Open a workspace to analyze project XAML.";
    [ObservableProperty] public partial bool IsProjectXamlAnalysisRunning { get; set; }

    private void InitializeProjectXamlAnalysis()
    {
        _projectAnalysis = new(async (generation, token) =>
        {
            Task work = Task.CompletedTask;
            await _dispatcher.InvokeAsync(() => work = ScanProjectXamlAsync(generation, token), token);
            await work;
        }, (generation, exception) => _dispatcher.Post(() =>
        {
            if (_disposed || _projectAnalysis?.IsCurrent(generation) != true) return;
            IsProjectXamlAnalysisRunning = false;
            XamlAnalysisStatus = "Project XAML analysis unavailable: " + exception.Message;
        }));
        _workspace.SemanticStateChanged += ProjectSemanticStateChanged;
    }

    private void ResetProjectXamlAnalysis(WorkspaceSnapshot? workspace)
    {
        _projectAnalysis?.Invalidate();
        Interlocked.Increment(ref _wpfIndexRevision);
        _sourceWatcher?.Dispose(); _sourceWatcher = null;
        if (!string.Equals(_diagnosticWorkspace?.Path, workspace?.Path, StringComparison.OrdinalIgnoreCase))
        { _buildDiagnostics.Clear(); _wpfIndex = null; _xaml.Index = null; }
        _diagnosticWorkspace = workspace;
        _projectReloadRequired = false;
        _projectTypesPending = false;
        _dirtyModelPaths.Clear();
        _indexTextHashes.Clear();
        _xamlResources.Invalidate();
        RefreshXamlContexts();
        _projectXamlFiles = [];
        IsProjectXamlAnalysisRunning = false;
        if (workspace is not null)
        {
            var files = workspace.Projects.SelectMany(project => project.Files)
                .Where(file => !file.IsGenerated && Path.GetExtension(file.Path).ToLowerInvariant() is ".cs" or ".xaml")
                .Select(file => file.Path).Concat(workspace.Projects.Select(project => project.ProjectPath)).Append(workspace.Path);
            _sourceWatcher = new(files, change =>
            {
                // Invalidate before dispatching: a completed scan must not race a
                // queued UI notification about changed source bytes.
                if (_disposed || !ReferenceEquals(workspace, Workspace)) return;
                _projectAnalysis?.Invalidate();
                Interlocked.Increment(ref _wpfIndexRevision);
                Interlocked.Increment(ref _diagnosticSourceEpoch);
                if (change.RequiresReload || change.Path is null || Path.GetExtension(change.Path).Equals(".xaml", StringComparison.OrdinalIgnoreCase))
                    _xamlResources.Invalidate();
                _dispatcher.Post(() =>
                {
                    if (_disposed || !ReferenceEquals(workspace, Workspace)) return;
                    if (change.Path is null) _indexDiagnosticsInvalid = true; else _invalidIndexPaths.Add(change.Path);
                    if (change.RequiresReload)
                    {
                        _projectReloadRequired = true;
                    }
                    if (change.Path is null || Path.GetExtension(change.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) _projectTypesPending = true;
                    if (change.Path is { } changedPath && Path.GetExtension(changedPath).Equals(".cs", StringComparison.OrdinalIgnoreCase)) _dirtyModelPaths.Add(changedPath);
                    if (_projectReloadRequired || _projectTypesPending) RefreshXamlContexts();
                    RequestProjectXamlAnalysis();
                });
            }, reconcile: () => _dispatcher.Post(() =>
            {
                if (!_disposed && ReferenceEquals(workspace, Workspace) && !IsProjectXamlAnalysisRunning)
                    _ = _projectAnalysis?.Schedule();
            }), contextFiles: ProjectContextFiles(workspace));
        }
        RequestProjectXamlAnalysis();
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

    private void TrackDiagnosticDocument(DocumentState document)
    {
        if (_diagnosticDocuments.Add(document)) document.ContentChanged += DiagnosticDocumentChanged;
        if (document.Extension == ".xaml") RefreshXamlResourceOverlays();
        RequestProjectXamlAnalysis();
    }

    private void UntrackDiagnosticDocument(DocumentState document)
    {
        if (_diagnosticDocuments.Remove(document)) document.ContentChanged -= DiagnosticDocumentChanged;
        if (document.Extension == ".xaml") RefreshXamlResourceOverlays();
        RequestProjectXamlAnalysis();
    }

    private void DiagnosticDocumentChanged(object? sender, EventArgs args)
    {
        if (sender is DocumentState { Extension: ".xaml" }) RefreshXamlResourceOverlays();
        if (sender is DocumentState { Extension: ".cs" or ".xaml" }) RequestProjectXamlAnalysis();
    }

    private void RefreshXamlResourceOverlays() => _xamlResources.SetOpenDocuments(Documents.Select(editor => editor.State));

    private void ProjectSemanticStateChanged(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref _symbolSemanticRevision);
        if (Volatile.Read(ref _synchronizingProjectAnalysis) != 0 || _disposed) return;
        _projectAnalysis?.Invalidate();
        Interlocked.Increment(ref _diagnosticSourceEpoch);
        _dispatcher.Post(() => { if (!_disposed) RequestProjectXamlAnalysis(); });
    }

    private void RequestProjectXamlAnalysis()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _wpfIndexRevision);
        Interlocked.Increment(ref _diagnosticSourceEpoch);
        _projectAnalysisRetries = 0;
        _projectXamlFiles = [];
        RefreshEditorDiagnostics();
        XamlAnalysisStatus = _projectReloadRequired ? "Project files changed. Reload the workspace to check XAML against the current project items."
            : Workspace is null ? "Open a workspace to analyze project XAML."
            : !_workspace.IsConnected ? "Project XAML analysis unavailable: the language worker is disconnected."
            : "Project XAML analysis pending…";
        _ = _projectAnalysis?.Schedule();
    }

    [RelayCommand]
    public async Task RefreshProjectXamlAnalysisAsync()
    {
        RequestProjectXamlAnalysis();
        if (_projectAnalysis is not { } scheduler) return;
        await scheduler.Schedule(immediate: true);
        // Watcher/semantic events may supersede a manual scan while it awaits
        // the worker. Await the newest request instead of reporting a stale idle.
        while (!_disposed)
        {
            var completion = scheduler.Completion;
            await completion;
            if (ReferenceEquals(completion, scheduler.Completion)) break;
        }
    }

    private async Task ScanProjectXamlAsync(long generation, CancellationToken token)
    {
        if (_disposed || _loading || _projectReloadRequired || Workspace is not { } workspace || !_workspace.IsConnected) return;
        var documents = Documents.Where(editor => editor.State.Extension is ".cs" or ".xaml")
            .Select(editor => (State: editor.State, Text: editor.State.Content, Version: editor.State.Version)).ToArray();
        bool Current() => !_disposed && _projectAnalysis?.IsCurrent(generation) == true && ReferenceEquals(workspace, Workspace)
            && documents.All(document => _store.Find(document.State.Path) == document.State
                && document.State.Version == document.Version && document.State.Content == document.Text);
        if (!Current()) return;
        IsProjectXamlAnalysisRunning = true;
        XamlAnalysisStatus = "Analyzing project XAML…";
        try
        {
            RefreshDiskDocumentsResult disk;
            var dirtyModels = _dirtyModelPaths.ToArray();
            Interlocked.Increment(ref _synchronizingProjectAnalysis);
            try
            {
                foreach (var document in documents.Where(document => document.State.Extension == ".cs"))
                {
                    token.ThrowIfCancellationRequested();
                    await _workspace.UpdateDocumentAsync(new(document.State.Path, document.Text, document.Version, Analyze: false), token);
                    if (!Current()) return;
                }
                disk = await _workspace.RefreshDiskDocumentsAsync(new(dirtyModels.Length == 0 ? null : dirtyModels), token);
            }
            finally { Interlocked.Decrement(ref _synchronizingProjectAnalysis); }
            if (!Current()) return;
            if (!disk.Accepted)
            {
                XamlAnalysisStatus = disk.Status ?? "Project source changed during synchronization; refreshing…";
                if (++_projectAnalysisRetries <= 2) _ = _projectAnalysis!.Schedule();
                return;
            }
            foreach (var path in dirtyModels) _dirtyModelPaths.Remove(path);
            if (disk.Changed)
            {
                Interlocked.Increment(ref _diagnosticSourceEpoch);
                _projectXamlFiles = [];
                RefreshEditorDiagnostics();
            }
            // Once the sweep is complete, the worker's project/dependency gates
            // own unavailable models. One locked project must not block healthy
            // editors elsewhere in the workspace indefinitely.
            if (_projectTypesPending && disk.PendingFiles == 0) { _projectTypesPending = false; RefreshXamlContexts(); }
            var overlays = documents.Where(document => document.State.Extension == ".xaml")
                .Select(document => new XamlDocumentOverlay(document.State.Path, document.Text, document.Version)).ToArray();
            var result = await _workspace.AnalyzeXamlProjectAsync(new(generation, overlays), token);
            if (!Current() || result.Generation != generation) return;
            if (!result.Accepted)
            {
                XamlAnalysisStatus = result.Status ?? "Project types changed during analysis; refreshing…";
                if (++_projectAnalysisRetries <= 2) _ = _projectAnalysis!.Schedule();
                return;
            }
            _projectXamlFiles = result.Files;
            foreach (var file in result.Files)
            foreach (var diagnostic in file.Diagnostics)
            {
                _projectDiagnosticOrigins.Remove(diagnostic);
                _projectDiagnosticOrigins.Add(diagnostic, new(file, workspace, Volatile.Read(ref _diagnosticSourceEpoch)));
            }
            int analyzed = result.Files.Count(file => file.State == "Analyzed");
            int unavailable = result.Files.Count - analyzed;
            XamlAnalysisStatus = $"XAML checked: {analyzed}/{result.TotalFiles} file contexts analyzed"
                + (unavailable > 0 ? $" · {unavailable} unavailable" : "")
                + (result.Truncated || disk.Truncated ? " · partial coverage" : "")
                + (result.Status is { Length: > 0 } status ? ". " + status : ".")
                + (disk.Status is { Length: > 0 } diskStatus ? " " + diskStatus : "")
                + (_sourceWatcher?.Status is { } watcherStatus ? " " + watcherStatus : "");
            RefreshEditorDiagnostics();
            if (disk.PendingFiles > 0) _ = _projectAnalysis!.Schedule();
        }
        finally
        {
            // The scheduler is serial; clear busy before the next queued scan.
            if (!_disposed) IsProjectXamlAnalysisRunning = false;
        }
    }

    private IEnumerable<WorkspaceDiagnostic> CurrentProjectXamlDiagnostics() => _projectXamlFiles
        .Where(file => file.State == "Analyzed" && (_store.Find(file.Path) is not { } document
            || file.Version == document.Version && file.TextHash == DocumentHash(document)))
        .SelectMany(file => file.Diagnostics);

    private IEnumerable<WorkspaceDiagnostic> CurrentEditorDiagnostics(bool xamlOnly = false) => Documents
        .Where(editor => (!xamlOnly || editor.IsXaml) && ((!_projectReloadRequired && !_projectTypesPending) || !editor.IsXaml))
        .Where(editor => !editor.IsXaml || !_projectXamlFiles.Any(file => file.State == "Analyzed"
            && file.Path.Equals(editor.State.Path, StringComparison.OrdinalIgnoreCase)
            && file.ProjectPath.Equals(editor.XamlProjectPath, StringComparison.OrdinalIgnoreCase)
            && file.Version == editor.State.Version && file.TextHash == DocumentHash(editor.State)))
        .SelectMany(editor => editor.Diagnostics);

    private bool IsIndexDiagnosticStale(WorkspaceDiagnostic issue)
    {
        if (_indexDiagnosticsInvalid || issue.Path is { } path && _invalidIndexPaths.Contains(path)) return true;
        var matches = _projectXamlFiles.Where(file => file.Path.Equals(issue.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return false;
        if (matches.Any(file => file.State is "Missing" or "Changed")) return true;
        if (_wpfIndex?.Texts.TryGetValue(issue.Path!, out var indexed) == true)
        {
            if (!_indexTextHashes.TryGetValue(issue.Path!, out var indexedHash)) _indexTextHashes[issue.Path!] = indexedHash = TextHash(indexed);
            if (matches.Any(file => file.TextHash is { } hash && hash != indexedHash)) return true;
        }
        return issue.Id == "XAML001" && matches.Any(file => file.State == "Analyzed");
    }

    private static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string DocumentHash(DocumentState document)
    {
        var fingerprint = _diagnosticTextHashes.GetValue(document, _ => new());
        if (fingerprint.Version != document.Version)
        { fingerprint.Hash = TextHash(document.Content); fingerprint.Version = document.Version; }
        return fingerprint.Hash;
    }

    private async Task<bool> VerifyProjectDiagnosticAsync(WorkspaceDiagnostic diagnostic)
    {
        if (!_projectDiagnosticOrigins.TryGetValue(diagnostic, out var origin) || diagnostic.Path is null) return true;
        var file = origin.File;
        var workspace = origin.Workspace;
        if (!ReferenceEquals(workspace, Workspace) || Volatile.Read(ref _diagnosticSourceEpoch) != origin.SourceEpoch) return false;
        var revision = Volatile.Read(ref _wpfIndexRevision);
        string text;
        if (_store.Find(file.Path) is { } document)
        {
            if (file.Version.HasValue && file.Version != document.Version) return false;
            text = document.Content;
        }
        else
        {
            if (await ProjectXamlDiagnosticText.ReadAsync(file.Path, _lifetime.Token) is not { } read) return false;
            text = read;
        }
        return ReferenceEquals(workspace, Workspace) && revision == Volatile.Read(ref _wpfIndexRevision)
            && file.TextHash is not null && file.TextHash == TextHash(text);
    }

    private bool IsOpenedProjectDiagnosticCurrent(WorkspaceDiagnostic diagnostic, DocumentState document) =>
        !_projectDiagnosticOrigins.TryGetValue(diagnostic, out var origin)
        || ReferenceEquals(origin.Workspace, Workspace) && document.Path.Equals(origin.File.Path, StringComparison.OrdinalIgnoreCase)
            && TextHash(document.Content) == origin.File.TextHash;

    private void DisposeProjectXamlAnalysis()
    {
        _workspace.SemanticStateChanged -= ProjectSemanticStateChanged;
        _sourceWatcher?.Dispose(); _projectAnalysis?.Dispose();
        foreach (var document in _diagnosticDocuments) document.ContentChanged -= DiagnosticDocumentChanged;
        _diagnosticDocuments.Clear();
    }
}
