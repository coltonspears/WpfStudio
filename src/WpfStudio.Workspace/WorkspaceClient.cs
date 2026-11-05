using System.Reflection;
using StreamJsonRpc;
using WpfStudio.Contracts;
using WpfStudio.Hosting;

namespace WpfStudio.Workspace;

/// <summary>A disposable connection to a dedicated worker. Reload/restart replays unsaved buffers.</summary>
public sealed class WorkspaceClient : IWorkspaceRpc, IAsyncDisposable
{
    private readonly string? _hostPath;
    private readonly TimeSpan _disposeTimeout;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, UpdateDocumentRequest> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _acceptedDocumentTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XamlNameProjectionPlan> _nameProjections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _bufferGate = new();
    private WorkerProcessHost? _worker;
    private JsonRpc? _rpc;
    private IWorkspaceRpc? _proxy;
    private LoadWorkspaceRequest? _lastLoad;
    private bool _disposed;
    // The worker listens before resolving the SDK, so this only has to cover its start.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private string _stoppedWorkerError = "";
    private string WorkerError => _worker?.ErrorTail ?? _stoppedWorkerError;

    /// <param name="disposeTimeout">How long shutdown waits for an in-flight call to let go of the lifecycle lock before the worker is stopped regardless.</param>
    public WorkspaceClient(string? hostPath = null, TimeSpan? disposeTimeout = null)
    {
        _hostPath = hostPath;
        _disposeTimeout = disposeTimeout ?? TimeSpan.FromSeconds(5);
    }
    public event EventHandler<WorkspaceDiagnosticEvent>? DiagnosticsReceived;
    public event EventHandler<string>? WorkerExited;
    /// <summary>Project types or worker availability changed; subscribers must marshal to their UI dispatcher.</summary>
    public event EventHandler? SemanticStateChanged;
    /// <summary>Current XAML field semantics or worker availability changed; C# subscribers must invalidate same-version observations.</summary>
    public event EventHandler? NameProjectionChanged;
    /// <summary>An accepted C# model change affects other editors. The canonical changed path lets its own editor retain the analysis that synchronized it.</summary>
    public event EventHandler<string>? CSharpModelChanged;
    /// <summary>A C# file changed only where nothing outside it can depend on, although reference counts elsewhere may differ.</summary>
    public event EventHandler<string>? CSharpBodyChanged;
    /// <summary>A whole-project compilation contradicted an analysis reply. Nothing semantic changed, so same-version observations stay valid; subscribers must marshal to their UI dispatcher.</summary>
    public event EventHandler? ProjectDiagnosticsChanged;
    public int? WorkerProcessId => _worker is { HasExited: false } worker ? worker.ProcessId : null;
    public bool IsConnected => _rpc is { IsDisposed: false } && _worker is { HasExited: false };

    public async Task<WorkspaceSnapshot> LoadAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = lifetime.Token;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopWorker();
            lock (_bufferGate) { _buffers.Clear(); _nameProjections.Clear(); }
            // A restart reloads from MSBuild; only the first start of a session uses the saved model.
            _lastLoad = request with { UseCache = false };
            await StartWorkerAsync(request.Path, cancellationToken).ConfigureAwait(false);
            // RPC cancellation otherwise waits for the server to acknowledge it. User build tasks may ignore cancellation.
            var snapshot = await Proxy.LoadAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            SemanticStateChanged?.Invoke(this, EventArgs.Empty);
            NotifyNameProjectionChanged();
            return snapshot;
        }
        catch { StopWorker(); throw; }
        finally { _lifecycle.Release(); }
    }

    public async Task<WorkspaceSnapshot> RestartAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = lifetime.Token;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var request = _lastLoad ?? throw new InvalidOperationException("No workspace has been opened.");
            StopWorker();
            await StartWorkerAsync(request.Path, cancellationToken).ConfigureAwait(false);
            var snapshot = await Proxy.LoadAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            await ReplayBuffersAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        catch { StopWorker(); throw; }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Stops the language worker and forgets the loaded workspace, as when its solution is closed.</summary>
    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await StopWorkerAndWaitAsync().ConfigureAwait(false);
            lock (_bufferGate) { _buffers.Clear(); _nameProjections.Clear(); }
            _lastLoad = null;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Replaces a cached start with a full MSBuild load in the same worker. The load runs outside the lifecycle lock,
    /// so typing keeps updating the current solution; open buffers are replayed into the new one afterwards.
    /// </summary>
    public async Task<WorkspaceSnapshot> RefreshAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = lifetime.Token;
        IWorkspaceRpc proxy;
        WorkerProcessHost? worker;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); proxy = Proxy; worker = _worker; }
        finally { _lifecycle.Release(); }
        request = request with { UseCache = false };
        await proxy.PrepareRefreshAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(worker, _worker)) throw new OperationCanceledException("The language worker restarted while the project model was refreshing.");
            var snapshot = await Proxy.CommitRefreshAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            await ReplayBuffersAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally { _lifecycle.Release(); }
    }

    // RefreshAsync is the supported entry point; these satisfy the shared contract.
    Task<bool> IWorkspaceRpc.PrepareRefreshAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken) => Proxy.PrepareRefreshAsync(request, cancellationToken);
    Task<WorkspaceSnapshot> IWorkspaceRpc.CommitRefreshAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken) => Proxy.CommitRefreshAsync(request, cancellationToken);

    // Called with the lifecycle lock held, after the worker replaced its solution.
    private async Task ReplayBuffersAsync(CancellationToken cancellationToken)
    {
        UpdateDocumentRequest[] buffers;
        lock (_bufferGate) buffers = _buffers.Values.ToArray();
        foreach (var buffer in buffers)
        {
            var synchronized = await Proxy.UpdateDocumentAsync(buffer, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (synchronized.Accepted && synchronized.Version == buffer.Version) RememberAcceptedDocument(buffer);
        }
        var replayPlans = NameProjectionPlans();
        foreach (var plan in replayPlans)
        {
            var result = await Proxy.ApplyXamlNameProjectionAsync(new(plan, buffers, Replay: true), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Accepted) throw new InvalidOperationException("Generated-name projection could not be restored: " + result.Status);
            if (result.BaselineRefreshed) ForgetNameProjection(plan);
        }
        NotifyNameProjectionChanged();
        SemanticStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<DocumentUpdateResult> UpdateDocumentAsync(UpdateDocumentRequest request, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { return await UpdateDocumentCoreAsync(request, lifetime.Token).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task<DocumentUpdateResult> UpdateDocumentCoreAsync(UpdateDocumentRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool changed = RememberBuffer(request);
        bool semanticChange = changed && !IsXaml(request.Path);
        // With no acknowledged text there is no telling what the worker compared this edit with.
        bool uncertain = !HasAcceptedDocument(request.Path);
        var proxy = Proxy;
        cancellationToken.ThrowIfCancellationRequested();
        DocumentUpdateResult result;
        // WaitAsync bounds the call by our token even when the worker never acknowledges the cancellation; this runs
        // under the lifecycle lock, so an unbounded wait here would also block close, reload and shutdown.
        try { result = await proxy.UpdateDocumentAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            // The worker can commit authored text before cancellable projection
            // work completes. A failed reply therefore invalidates our last
            // acknowledged identity, even if the next edit restores that text.
            ForgetAcceptedDocument(request.Path);
            if (semanticChange) SemanticStateChanged?.Invoke(this, EventArgs.Empty);
            throw;
        }
        bool declarationsChanged = result.DeclarationsChanged || uncertain;
        // Only C# synchronization invalidates dependent XAML here, and only a change to what
        // it declares can matter to XAML. XAML editors already share resource invalidation;
        // firing their own event would cancel the synchronization at the start of every analysis.
        if (semanticChange && declarationsChanged) SemanticStateChanged?.Invoke(this, EventArgs.Empty);
        // Replay buffers remember the latest requested text even if a worker
        // fails. Notification identity must instead follow accepted text, so a
        // successful retry after cancellation still refreshes unchanged C#.
        if (result.Accepted && result.Version == request.Version && RememberAcceptedDocument(request))
        {
            if (IsXaml(request.Path)) NotifyNameProjectionChanged();
            else if (IsCSharp(request.Path))
                (declarationsChanged ? CSharpModelChanged : CSharpBodyChanged)?.Invoke(this, Path.GetFullPath(request.Path));
        }
        if (result.Accepted && request.Analyze) DiagnosticsReceived?.Invoke(this, new WorkspaceDiagnosticEvent(request.Path, result.Version, result.Diagnostics));
        return result;
    }

    private bool RememberBuffer(UpdateDocumentRequest request)
    {
        lock (_bufferGate)
        {
            if (_buffers.TryGetValue(request.Path, out var existing) && existing.Version == request.Version && existing.Text != request.Text)
                throw new InvalidOperationException("A document version cannot identify different text. Increment its version before updating.");
            bool changed = !_buffers.TryGetValue(request.Path, out var previous) || (request.Version >= previous.Version && request.Text != previous.Text);
            if (previous is null || request.Version >= previous.Version) _buffers[request.Path] = request;
            return changed;
        }
    }

    private bool RememberAcceptedDocument(UpdateDocumentRequest request)
    {
        if (!IsXaml(request.Path) && !IsCSharp(request.Path)) return false;
        string path = Path.GetFullPath(request.Path);
        lock (_bufferGate)
        {
            bool changed = !_acceptedDocumentTexts.TryGetValue(path, out var previous) || previous != request.Text;
            _acceptedDocumentTexts[path] = request.Text;
            return changed;
        }
    }

    private bool HasAcceptedDocument(string path)
    {
        lock (_bufferGate) return _acceptedDocumentTexts.ContainsKey(Path.GetFullPath(path));
    }

    private void ForgetAcceptedDocument(string path)
    {
        lock (_bufferGate) _acceptedDocumentTexts.Remove(Path.GetFullPath(path));
    }

    public async Task CloseDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Forget discarded text even if the worker is gone or crashes while processing close.
            lock (_bufferGate) _buffers.Remove(path);
            if (IsConnected)
            {
                var proxy = Proxy;
                lifetime.Token.ThrowIfCancellationRequested();
                try { await proxy.CloseDocumentAsync(path, lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false); }
                catch { ForgetAcceptedDocument(path); throw; }
            }
            ForgetAcceptedDocument(path);
            SemanticStateChanged?.Invoke(this, EventArgs.Empty);
            if (IsXaml(path)) NotifyNameProjectionChanged();
            else if (IsCSharp(path)) CSharpModelChanged?.Invoke(this, Path.GetFullPath(path));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<XamlNameProjectionResult> ApplyXamlNameProjectionAsync(XamlNameProjectionRequest request, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_bufferGate)
                if (!_nameProjections.ContainsKey(request.Plan.XamlPath) && _nameProjections.Count >= 32)
                    return new(false, "The generated-name projection limit was reached. Save, build and reload before another page rename.");
            ValidateProjectionBuffers(request.Documents);
            var result = await Proxy.ApplyXamlNameProjectionAsync(request, lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
            if (result.Accepted)
            {
                foreach (var document in request.Documents) { RememberBuffer(document); RememberAcceptedDocument(document); }
                if (result.BaselineRefreshed) ForgetNameProjection(request.Plan);
                else lock (_bufferGate) _nameProjections[request.Plan.XamlPath] = request.Plan;
                SemanticStateChanged?.Invoke(this, EventArgs.Empty);
                NameProjectionChanged?.Invoke(this, EventArgs.Empty);
            }
            return result;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Reconciles an entire completed editor transaction, without observing its per-file intermediate states.</summary>
    public async Task<XamlNameProjectionResult> ReconcileNameProjectionsAsync(IReadOnlyList<UpdateDocumentRequest> documents, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateProjectionBuffers(documents);
            var plans = NameProjectionPlans();
            if (plans.Length == 0)
            {
                foreach (var document in documents)
                {
                    var synchronized = await UpdateDocumentCoreAsync(document, lifetime.Token).ConfigureAwait(false);
                    // Unowned scratch files have no worker document. They must
                    // not prevent operations on a separate, evaluated page.
                    if (!synchronized.Accepted && synchronized.Version != document.Version)
                        return new(false, "An editor buffer could not be synchronized.");
                }
                return new(true);
            }
            var statuses = new List<string>();
            foreach (var plan in plans)
            {
                var result = await Proxy.ApplyXamlNameProjectionAsync(new(plan, documents, Replay: true), lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
                if (!result.Accepted) return result;
                if (result.BaselineRefreshed) ForgetNameProjection(plan);
                if (!string.IsNullOrWhiteSpace(result.Status)) statuses.Add(result.Status);
                foreach (var document in documents) { RememberBuffer(document); RememberAcceptedDocument(document); }
            }
            SemanticStateChanged?.Invoke(this, EventArgs.Empty);
            NameProjectionChanged?.Invoke(this, EventArgs.Empty);
            return new(true, statuses.Count == 0 ? null : string.Join(" ", statuses.Distinct()));
        }
        finally { _lifecycle.Release(); }
    }

    private void ValidateProjectionBuffers(IReadOnlyList<UpdateDocumentRequest> documents)
    {
        lock (_bufferGate)
            foreach (var document in documents)
                if (_buffers.TryGetValue(document.Path, out var current) &&
                    (current.Version > document.Version || current.Version == document.Version && current.Text != document.Text))
                    throw new InvalidOperationException("An editor buffer changed before generated-name synchronization. Retry with current buffers.");
    }
    private XamlNameProjectionPlan[] NameProjectionPlans() { lock (_bufferGate) return _nameProjections.Values.ToArray(); }
    private void ForgetNameProjection(XamlNameProjectionPlan plan) { lock (_bufferGate) _nameProjections.Remove(plan.XamlPath); }
    private void NotifyNameProjectionChanged()
    {
        NameProjectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private static bool IsXaml(string path) => Path.GetExtension(path).Equals(".xaml", StringComparison.OrdinalIgnoreCase);
    private static bool IsCSharp(string path) => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    public Task<CompletionResult> GetCompletionsAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetCompletionsAsync(request, cancellationToken);
    public Task<XamlAnalysisResult> AnalyzeXamlAsync(XamlDocumentRequest request, CancellationToken cancellationToken = default) => Proxy.AnalyzeXamlAsync(request, cancellationToken);
    public Task<XamlProjectAnalysisResult> AnalyzeXamlProjectAsync(XamlProjectAnalysisRequest request, CancellationToken cancellationToken = default) => Proxy.AnalyzeXamlProjectAsync(request, cancellationToken);
    public async Task<RefreshDiskDocumentsResult> RefreshDiskDocumentsAsync(RefreshDiskDocumentsRequest request, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            var result = await Proxy.RefreshDiskDocumentsAsync(request, lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
            if (result.Changed) { SemanticStateChanged?.Invoke(this, EventArgs.Empty); NotifyNameProjectionChanged(); }
            return result;
        }
        finally { _lifecycle.Release(); }
    }
    public Task<XamlCompletionResult> GetXamlCompletionsAsync(XamlCompletionRequest request, CancellationToken cancellationToken = default) => Proxy.GetXamlCompletionsAsync(request, cancellationToken);
    public Task<IReadOnlyList<SourceLocation>> GetXamlDefinitionAsync(XamlCompletionRequest request, CancellationToken cancellationToken = default) => Proxy.GetXamlDefinitionAsync(request, cancellationToken);
    public Task<XamlHoverInfo?> GetXamlHoverAsync(XamlCompletionRequest request, CancellationToken cancellationToken = default) => Proxy.GetXamlHoverAsync(request, cancellationToken);
    public Task<IReadOnlyList<XamlCodeAction>> GetXamlCodeActionsAsync(XamlCompletionRequest request, CancellationToken cancellationToken = default) => Proxy.GetXamlCodeActionsAsync(request, cancellationToken);
    public Task<TextEdit?> GetCompletionEditAsync(CompletionEditRequest request, CancellationToken cancellationToken = default) => Proxy.GetCompletionEditAsync(request, cancellationToken);
    public Task<QuickInfoResult?> GetCompletionDetailsAsync(CompletionEditRequest request, CancellationToken cancellationToken = default) => Proxy.GetCompletionDetailsAsync(request, cancellationToken);
    public Task<SignatureHelpResult> GetSignatureHelpAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetSignatureHelpAsync(request, cancellationToken);
    public Task<QuickInfoResult?> GetQuickInfoAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetQuickInfoAsync(request, cancellationToken);
    public Task<IReadOnlyList<SourceLocation>> GetDefinitionAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetDefinitionAsync(request, cancellationToken);
    public Task<IReadOnlyList<SourceLocation>> FindReferencesAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.FindReferencesAsync(request, cancellationToken);
    public Task<IReadOnlyList<SourceLocation>> FindImplementationsAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.FindImplementationsAsync(request, cancellationToken);
    public Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(SymbolSearchRequest request, CancellationToken cancellationToken = default) => Proxy.SearchSymbolsAsync(request, cancellationToken);
    public Task<SymbolReferenceResult> FindSymbolReferencesAsync(SymbolReferenceRequest request, CancellationToken cancellationToken = default) => Proxy.FindSymbolReferencesAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> FormatDocumentAsync(DocumentRequest request, CancellationToken cancellationToken = default) => Proxy.FormatDocumentAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> FormatXamlAsync(XamlFormattingRequest request, CancellationToken cancellationToken = default) => Proxy.FormatXamlAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken = default) => Proxy.RenameAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> RefactorAsync(RefactorRequest request, CancellationToken cancellationToken = default) => Proxy.RefactorAsync(request, cancellationToken);
    public Task<EditorDecorationsResult> GetEditorDecorationsAsync(DocumentRequest request, CancellationToken cancellationToken = default) => Proxy.GetEditorDecorationsAsync(request, cancellationToken);
    public Task<CodeLensResult> GetCodeLensAsync(DocumentRequest request, CancellationToken cancellationToken = default) => Proxy.GetCodeLensAsync(request, cancellationToken);
    public Task<CodeActionsResult> GetCodeActionsAsync(CodeActionsRequest request, CancellationToken cancellationToken = default) => Proxy.GetCodeActionsAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> ApplyCodeActionAsync(ApplyCodeActionRequest request, CancellationToken cancellationToken = default) => Proxy.ApplyCodeActionAsync(request, cancellationToken);

    private IWorkspaceRpc Proxy => _proxy ?? throw new InvalidOperationException($"Workspace worker is not connected. Reopen or restart the workspace. {WorkerError}");

    private async Task StartWorkerAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var hostPath = _hostPath ?? FindHost();
        if (!File.Exists(hostPath)) throw new FileNotFoundException("WorkspaceHost is missing. Build and package the WorkspaceHost output directory with the application.", hostPath);
        var worker = new WorkerProcessHost(new(hostPath, $"wpfstudio-{Environment.ProcessId}-{Guid.NewGuid():N}", ConnectTimeout)
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workspacePath))!,
            Arguments = ["--workspace", Path.GetFullPath(workspacePath)],
            KillProcessTree = true,
            ConfigureStart = ProcessRunner.ClearInheritedToolchain,
            // Launched through whatever dotnet is on PATH, which may be a shim that starts the real host as a child.
            VerifyServerProcess = false
        });
        worker.Exited += () =>
        {
            if (ReferenceEquals(_worker, worker) && !_disposed)
            {
                SemanticStateChanged?.Invoke(this, EventArgs.Empty);
                NotifyNameProjectionChanged();
                WorkerExited?.Invoke(this, worker.ErrorTail);
            }
        };
        _stoppedWorkerError = "";
        _worker = worker;
        try
        {
            worker.Start();
            await worker.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _rpc = new JsonRpc(worker.Pipe);
            _proxy = _rpc.Attach<IWorkspaceRpc>();
            var rpc = _rpc;
            _proxy.SemanticStateChanged += (_, _) =>
            {
                if (ReferenceEquals(_rpc, rpc) && ReferenceEquals(_worker, worker) && !_disposed)
                {
                    SemanticStateChanged?.Invoke(this, EventArgs.Empty);
                    NotifyNameProjectionChanged();
                }
            };
            _proxy.ProjectDiagnosticsChanged += (_, _) =>
            {
                if (ReferenceEquals(_rpc, rpc) && ReferenceEquals(_worker, worker) && !_disposed)
                    ProjectDiagnosticsChanged?.Invoke(this, EventArgs.Empty);
            };
            _rpc.StartListening();
        }
        catch
        {
            var error = WorkerError;
            StopWorker();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"Workspace worker failed to connect. {error}");
        }
    }

    private static string FindHost()
    {
        // Development and test launches can locate the separately-built worker without changing runtime packaging.
        var current = typeof(WorkspaceClient).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        return WorkerHostLocator.Find("WpfStudio.WorkspaceHost", "WorkspaceHost", "net10.0", [current, "Debug", "Release"], [".dll"], []);
    }

    private void StopWorker() => DetachWorker()?.Dispose();

    // The worker's working directory is the workspace, and a killed process takes a moment to exit, longer with Server GC.
    // Waiting for it means the folder is free once a solution is closed.
    private async Task StopWorkerAndWaitAsync()
    {
        if (DetachWorker() is { } worker) await worker.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
    }

    private WorkerProcessHost? DetachWorker()
    {
        var worker = _worker;
        if (worker is not null) _stoppedWorkerError = worker.ErrorTail;
        _worker = null;
        _proxy = null;
        _rpc?.Dispose(); _rpc = null;
        lock (_bufferGate) _acceptedDocumentTexts.Clear();
        if (worker is not null) { SemanticStateChanged?.Invoke(this, EventArgs.Empty); NotifyNameProjectionChanged(); }
        return worker;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        // Cancelling the shutdown token ends every bounded call. A caller that still holds the lock after that is
        // stuck in the worker; killing the worker is what releases it, so do not wait forever to do so.
        bool acquired = await _lifecycle.WaitAsync(_disposeTimeout).ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await StopWorkerAndWaitAsync().ConfigureAwait(false); }
        finally { if (acquired) _lifecycle.Release(); }
    }
}
