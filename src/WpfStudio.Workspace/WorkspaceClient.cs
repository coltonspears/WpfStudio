using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using StreamJsonRpc;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

/// <summary>A disposable connection to a dedicated worker. Reload/restart replays unsaved buffers.</summary>
public sealed class WorkspaceClient : IWorkspaceRpc, IAsyncDisposable
{
    private readonly string? _hostPath;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, UpdateDocumentRequest> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XamlNameProjectionPlan> _nameProjections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _bufferGate = new();
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private JsonRpc? _rpc;
    private IWorkspaceRpc? _proxy;
    private LoadWorkspaceRequest? _lastLoad;
    private bool _disposed;
    private string _workerError = "";

    public WorkspaceClient(string? hostPath = null) => _hostPath = hostPath;
    public event EventHandler<WorkspaceDiagnosticEvent>? DiagnosticsReceived;
    public event EventHandler<string>? WorkerExited;
    /// <summary>Project types or worker availability changed; subscribers must marshal to their UI dispatcher.</summary>
    public event EventHandler? SemanticStateChanged;
    /// <summary>Accepted generated-name projections changed; C# subscribers must invalidate same-version observations.</summary>
    public event EventHandler? NameProjectionChanged;
    public int? WorkerProcessId
    {
        get { try { return _process is { HasExited: false } process ? process.Id : null; } catch (InvalidOperationException) { return null; } }
    }
    public bool IsConnected
    {
        get
        {
            try { return _rpc is { IsDisposed: false } && _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; } // A concurrent restart may have disposed the process.
        }
    }

    public async Task<WorkspaceSnapshot> LoadAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = lifetime.Token;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool hadNameProjections = NameProjectionPlans().Length != 0;
            StopWorker();
            lock (_bufferGate) { _buffers.Clear(); _nameProjections.Clear(); }
            _lastLoad = request;
            await StartWorkerAsync(request.Path, cancellationToken).ConfigureAwait(false);
            // RPC cancellation otherwise waits for the server to acknowledge it. User build tasks may ignore cancellation.
            var snapshot = await Proxy.LoadAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            SemanticStateChanged?.Invoke(this, EventArgs.Empty);
            if (hadNameProjections) NameProjectionChanged?.Invoke(this, EventArgs.Empty);
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
            UpdateDocumentRequest[] buffers;
            lock (_bufferGate) buffers = _buffers.Values.ToArray();
            foreach (var buffer in buffers) await Proxy.UpdateDocumentAsync(buffer, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            var replayPlans = NameProjectionPlans();
            foreach (var plan in replayPlans)
            {
                var result = await Proxy.ApplyXamlNameProjectionAsync(new(plan, buffers, Replay: true), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Accepted) throw new InvalidOperationException("Generated-name projection could not be restored: " + result.Status);
                if (result.BaselineRefreshed) ForgetNameProjection(plan);
            }
            if (replayPlans.Length != 0) NameProjectionChanged?.Invoke(this, EventArgs.Empty);
            SemanticStateChanged?.Invoke(this, EventArgs.Empty);
            return snapshot;
        }
        catch { StopWorker(); throw; }
        finally { _lifecycle.Release(); }
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
        // Only C# synchronization invalidates dependent XAML here. XAML editors
        // already share resource invalidation; firing their own event would
        // cancel the synchronization at the start of every analysis.
        if (semanticChange) SemanticStateChanged?.Invoke(this, EventArgs.Empty);
        var result = await Proxy.UpdateDocumentAsync(request, cancellationToken).ConfigureAwait(false);
        if (semanticChange) SemanticStateChanged?.Invoke(this, EventArgs.Empty);
        if (changed && IsXaml(request.Path) && result.Accepted) NotifyNameProjectionChanged();
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

    public async Task CloseDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Forget discarded text even if the worker is gone or crashes while processing close.
            lock (_bufferGate) _buffers.Remove(path);
            if (IsConnected) await Proxy.CloseDocumentAsync(path, lifetime.Token).ConfigureAwait(false);
            SemanticStateChanged?.Invoke(this, EventArgs.Empty);
            if (IsXaml(path)) NotifyNameProjectionChanged();
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
            var result = await Proxy.ApplyXamlNameProjectionAsync(request, lifetime.Token).ConfigureAwait(false);
            if (result.Accepted)
            {
                foreach (var document in request.Documents) RememberBuffer(document);
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
                var result = await Proxy.ApplyXamlNameProjectionAsync(new(plan, documents, Replay: true), lifetime.Token).ConfigureAwait(false);
                if (!result.Accepted) return result;
                if (result.BaselineRefreshed) ForgetNameProjection(plan);
                if (!string.IsNullOrWhiteSpace(result.Status)) statuses.Add(result.Status);
                foreach (var document in documents) RememberBuffer(document);
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
        if (NameProjectionPlans().Length != 0) NameProjectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private static bool IsXaml(string path) => Path.GetExtension(path).Equals(".xaml", StringComparison.OrdinalIgnoreCase);

    public Task<CompletionResult> GetCompletionsAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetCompletionsAsync(request, cancellationToken);
    public Task<XamlAnalysisResult> AnalyzeXamlAsync(XamlDocumentRequest request, CancellationToken cancellationToken = default) => Proxy.AnalyzeXamlAsync(request, cancellationToken);
    public Task<XamlProjectAnalysisResult> AnalyzeXamlProjectAsync(XamlProjectAnalysisRequest request, CancellationToken cancellationToken = default) => Proxy.AnalyzeXamlProjectAsync(request, cancellationToken);
    public async Task<RefreshDiskDocumentsResult> RefreshDiskDocumentsAsync(RefreshDiskDocumentsRequest request, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            var result = await Proxy.RefreshDiskDocumentsAsync(request, lifetime.Token).ConfigureAwait(false);
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
    public Task<SignatureHelpResult> GetSignatureHelpAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetSignatureHelpAsync(request, cancellationToken);
    public Task<IReadOnlyList<SourceLocation>> GetDefinitionAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetDefinitionAsync(request, cancellationToken);
    public Task<IReadOnlyList<SourceLocation>> FindReferencesAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.FindReferencesAsync(request, cancellationToken);
    public Task<SymbolReferenceResult> FindSymbolReferencesAsync(SymbolReferenceRequest request, CancellationToken cancellationToken = default) => Proxy.FindSymbolReferencesAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> FormatDocumentAsync(DocumentRequest request, CancellationToken cancellationToken = default) => Proxy.FormatDocumentAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> FormatXamlAsync(XamlFormattingRequest request, CancellationToken cancellationToken = default) => Proxy.FormatXamlAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken = default) => Proxy.RenameAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> RefactorAsync(RefactorRequest request, CancellationToken cancellationToken = default) => Proxy.RefactorAsync(request, cancellationToken);

    private IWorkspaceRpc Proxy => _proxy ?? throw new InvalidOperationException($"Workspace worker is not connected. Reopen or restart the workspace. {_workerError}");

    private async Task StartWorkerAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var hostPath = _hostPath ?? FindHost();
        if (!File.Exists(hostPath)) throw new FileNotFoundException("WorkspaceHost is missing. Build and package the WorkspaceHost output directory with the application.", hostPath);
        var pipeName = $"wpfstudio-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var start = new ProcessStartInfo(hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : hostPath)
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workspacePath))!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        ProcessRunner.ClearInheritedToolchain(start);
        if (hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(hostPath);
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(Path.GetFullPath(workspacePath));
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _workerError = "";
        process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) _workerError = line; };
        process.OutputDataReceived += (_, _) => { };
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process) && !_disposed)
            {
                SemanticStateChanged?.Invoke(this, EventArgs.Empty);
                NotifyNameProjectionChanged();
                WorkerExited?.Invoke(this, _workerError);
            }
        };
        _process = process;
        try
        {
            process.Start(); process.BeginErrorReadLine(); process.BeginOutputReadLine();
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await _pipe.ConnectAsync(30_000, cancellationToken).ConfigureAwait(false);
            _rpc = new JsonRpc(_pipe);
            _proxy = _rpc.Attach<IWorkspaceRpc>();
            var rpc = _rpc;
            _proxy.SemanticStateChanged += (_, _) =>
            {
                if (ReferenceEquals(_rpc, rpc) && ReferenceEquals(_process, process) && !_disposed)
                    SemanticStateChanged?.Invoke(this, EventArgs.Empty);
            };
            _rpc.StartListening();
        }
        catch
        {
            var error = _workerError;
            StopWorker();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"Workspace worker failed to connect. {error}");
        }
    }

    private static string FindHost()
    {
        foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "WorkspaceHost", "WpfStudio.WorkspaceHost.dll"), Path.Combine(AppContext.BaseDirectory, "WpfStudio.WorkspaceHost.dll") })
            if (File.Exists(candidate)) return candidate;
        // Development/test launches can locate the separately-built worker without changing runtime packaging.
        var currentConfiguration = typeof(WorkspaceClient).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            foreach (var configuration in new[] { currentConfiguration, "Debug", "Release" }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(directory.FullName, "src", "WpfStudio.WorkspaceHost", "bin", configuration, "net10.0", "WpfStudio.WorkspaceHost.dll");
                if (File.Exists(candidate)) return candidate;
            }
        return Path.Combine(AppContext.BaseDirectory, "WorkspaceHost", "WpfStudio.WorkspaceHost.dll");
    }

    private void StopWorker()
    {
        var process = _process; _process = null;
        _proxy = null;
        _rpc?.Dispose(); _rpc = null;
        _pipe?.Dispose(); _pipe = null;
        if (process is not null) { SemanticStateChanged?.Invoke(this, EventArgs.Empty); NotifyNameProjectionChanged(); }
        if (process is not null) { ProcessRunner.Kill(process); process.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; StopWorker(); }
        finally { _lifecycle.Release(); }
    }
}
