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
            StopWorker();
            lock (_bufferGate) _buffers.Clear();
            _lastLoad = request;
            await StartWorkerAsync(request.Path, cancellationToken).ConfigureAwait(false);
            // RPC cancellation otherwise waits for the server to acknowledge it. User build tasks may ignore cancellation.
            return await Proxy.LoadAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
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
            return snapshot;
        }
        catch { StopWorker(); throw; }
        finally { _lifecycle.Release(); }
    }

    public async Task<DocumentUpdateResult> UpdateDocumentAsync(UpdateDocumentRequest request, CancellationToken cancellationToken = default)
    {
        lock (_bufferGate)
        {
            if (_buffers.TryGetValue(request.Path, out var existing) && existing.Version == request.Version && existing.Text != request.Text)
                throw new InvalidOperationException("A document version cannot identify different text. Increment its version before updating.");
            if (!_buffers.TryGetValue(request.Path, out var previous) || request.Version >= previous.Version) _buffers[request.Path] = request;
        }
        var result = await Proxy.UpdateDocumentAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Accepted && request.Analyze) DiagnosticsReceived?.Invoke(this, new WorkspaceDiagnosticEvent(request.Path, result.Version, result.Diagnostics));
        return result;
    }

    public async Task CloseDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        // Forget discarded text even if the worker is gone or crashes while processing close.
        lock (_bufferGate) _buffers.Remove(path);
        if (IsConnected) await Proxy.CloseDocumentAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task<CompletionResult> GetCompletionsAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetCompletionsAsync(request, cancellationToken);
    public Task<TextEdit?> GetCompletionEditAsync(CompletionEditRequest request, CancellationToken cancellationToken = default) => Proxy.GetCompletionEditAsync(request, cancellationToken);
    public Task<SignatureHelpResult> GetSignatureHelpAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetSignatureHelpAsync(request, cancellationToken);
    public Task<IReadOnlyList<SourceLocation>> GetDefinitionAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.GetDefinitionAsync(request, cancellationToken);
    public Task<IReadOnlyList<SourceLocation>> FindReferencesAsync(DocumentPositionRequest request, CancellationToken cancellationToken = default) => Proxy.FindReferencesAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> FormatDocumentAsync(DocumentRequest request, CancellationToken cancellationToken = default) => Proxy.FormatDocumentAsync(request, cancellationToken);
    public Task<WorkspaceEditResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken = default) => Proxy.RenameAsync(request, cancellationToken);

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
        process.Exited += (_, _) => { if (ReferenceEquals(_process, process) && !_disposed) WorkerExited?.Invoke(this, _workerError); };
        _process = process;
        try
        {
            process.Start(); process.BeginErrorReadLine(); process.BeginOutputReadLine();
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await _pipe.ConnectAsync(30_000, cancellationToken).ConfigureAwait(false);
            _rpc = new JsonRpc(_pipe);
            _proxy = _rpc.Attach<IWorkspaceRpc>();
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
