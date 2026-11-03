using System.IO;
using System.Reflection;
using StreamJsonRpc;
using WpfStudio.Contracts;
using WpfStudio.Hosting;
using WpfStudio.Inspection.Protocol;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.Runtime.Design;

public interface IPreviewClient : IPreviewRpc, IAsyncDisposable
{
    event EventHandler<string>? Disconnected;
    IPreviewInteractionSession? CreateInteractionSession(PreviewSurfaceIdentity surface) => null;
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owns one isolated WPF process, with bounded requests and an independent native-interaction watchdog.</summary>
public sealed partial class PreviewClient(string? hostPath = null, TimeSpan? requestTimeout = null, TimeSpan? supersededRequestGrace = null) : IPreviewClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _requestEpoch = new();
    private Task _stopBarrier = Task.CompletedTask;
    private readonly TimeSpan _timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    /// <summary>How long a superseded render may keep the host busy before it is treated as hung.</summary>
    private readonly TimeSpan _supersededRequestGrace = supersededRequestGrace ?? TimeSpan.FromSeconds(3);
    private PreviewProcessLease? _lease;
    private PreviewShadowDirectory? _shadowDirectory;
    private JsonRpc? _rpc;
    private IPreviewRpc? _proxy;
    private string? _assembly;
    private string? _assemblyStamp;
    private string? _projectDirectory;
    private PreviewMode _mode;
    private string? _applicationResourcePath;
    private bool _scenarioActive;
    private bool _disposed;
    public event EventHandler<string>? Disconnected;
    public int? ProcessId { get { var lease = Volatile.Read(ref _lease); return lease is { IsAlive: true } ? lease.ProcessId : null; } }

    public Task<PreviewSnapshot> RenderAsync(PreviewRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(proxy => proxy.RenderAsync(request, cancellationToken), request, cancellationToken);
    public Task<PreviewSnapshot> CaptureAsync(PreviewCaptureRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(proxy => proxy.CaptureAsync(request, cancellationToken), null, cancellationToken);
    public Task<PreviewInspection> InspectAsync(PreviewNodeRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(proxy => proxy.InspectAsync(request, cancellationToken), null, cancellationToken);
    public Task<PreviewInspection> PickAsync(PreviewPickRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(proxy => proxy.PickAsync(request, cancellationToken), null, cancellationToken);
    public Task<PreviewEditResult> SetPropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(proxy => proxy.SetPropertyAsync(request, cancellationToken), null, cancellationToken);
    public Task<PreviewPropertyValidation> ValidatePropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(proxy => proxy.ValidatePropertyAsync(request, cancellationToken), null, cancellationToken);
    public async Task<PreviewLayoutValidationResult> ValidateLayoutEditAsync(PreviewLayoutValidationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ExecuteAsync(proxy => proxy.ValidateLayoutEditAsync(request, cancellationToken), null, cancellationToken).ConfigureAwait(false);
        if (result.Request != request) throw new InvalidDataException("The layout validation response does not match the selected preview observation.");
        return result;
    }

    public async Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ExecuteAsync(proxy => proxy.GetAppearanceAsync(request, cancellationToken), null, cancellationToken).ConfigureAwait(false);
        if (result.Request != request || result.Snapshot is null)
            throw new InvalidDataException("The appearance response does not match the selected preview property.");
        return result;
    }

    public async Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ExecuteAsync(proxy => proxy.GetBindingSourceAsync(request, cancellationToken), null, cancellationToken).ConfigureAwait(false);
        Inspection.InspectionSession.ValidateBindingSourceResponse(request, result);
        return result;
    }

    private async Task<T> ExecuteAsync<T>(Func<IPreviewRpc, Task<T>> action, PreviewRequest? render, CancellationToken token,
        PreviewProcessLease? expectedLease = null, Func<bool>? stillCurrent = null)
    {
        CancellationTokenSource lifetimeSource;
        CancellationToken epoch;
        Task stopBarrier;
        lock (_lifecycle)
        {
            lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token, _requestEpoch.Token);
            epoch = _requestEpoch.Token;
            stopBarrier = _stopBarrier;
        }
        using var lifetime = lifetimeSource;
        // A request made after Stop uses a fresh epoch, but must not start a new
        // host until that stop has finished cleaning up its predecessor.
        await stopBarrier.WaitAsync(lifetime.Token).ConfigureAwait(false);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        bool releaseGate = true, abandoned = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A delayed native operation is not permission to touch a replacement process.
            // Reject it outside the failure cleanup for ordinary calls.
            if (expectedLease is not null && (!ReferenceEquals(Volatile.Read(ref _lease), expectedLease) ||
                !expectedLease.IsAlive || stillCurrent?.Invoke() == false))
                throw new InvalidOperationException("This native preview session is no longer current.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(_timeout);
            try
            {
                if (render is not null) await PrepareRenderAsync(deadline.Token, lifetime.Token).ConfigureAwait(false);
                string? assemblyStamp = render is null ? null : await Task.Run(() => PreviewDependencyCatalog.Read(render).Fingerprint(), deadline.Token).ConfigureAwait(false);
                if (render != null && (_proxy == null || ProcessId == null || render.Mode == PreviewMode.Compiled || render.Scenario is not null || _scenarioActive ||
                    !string.Equals(_assembly, render.AssemblyPath, StringComparison.OrdinalIgnoreCase) ||
                    _assemblyStamp != assemblyStamp ||
                    _mode != render.Mode || !string.Equals(_applicationResourcePath, render.ApplicationResourcePath, StringComparison.Ordinal) ||
                    !string.Equals(_projectDirectory, render.ProjectDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    await StartAsync(deadline.Token).ConfigureAwait(false);
                    _assembly = render.AssemblyPath; _assemblyStamp = assemblyStamp; _projectDirectory = render.ProjectDirectory;
                    _mode = render.Mode; _applicationResourcePath = render.ApplicationResourcePath;
                    _scenarioActive = render.Scenario is not null;
                }
                var proxy = _proxy ?? throw new InvalidOperationException("Refresh the preview before inspecting it.");
                // WaitAsync bounds RPCs even when project constructors ignore cancellation.
                var call = action(proxy);
                T result;
                try { result = await call.WaitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (render is not null && token.IsCancellationRequested
                    && !_shutdown.IsCancellationRequested && !epoch.IsCancellationRequested)
                {
                    // A newer render superseded this one (live preview typing). The host
                    // renders on its dispatcher and cannot abandon that work midway, but it
                    // is still healthy: let it finish in the background, holding the gate so
                    // requests stay ordered, instead of killing and cold-starting a process.
                    releaseGate = false;
                    DrainSupersededAsync(call).Forget();
                    throw new SupersededRequestException(token);
                }
                catch (OperationCanceledException) when (render is null && token.IsCancellationRequested
                    && !_shutdown.IsCancellationRequested && !epoch.IsCancellationRequested)
                {
                    // The caller lost interest in a query (a pick while the pointer moved on). The host is healthy:
                    // hold the gate until it acknowledges, as for a superseded render, rather than kill and cold-start it.
                    releaseGate = false; abandoned = true;
                    DrainSupersededAsync(call).Forget();
                    throw new OperationCanceledException("The preview request was cancelled.", token);
                }
                lifetime.Token.ThrowIfCancellationRequested();
                if (result is PreviewSnapshot snapshot) RememberSurface(snapshot);
                else if (result is PreviewEditResult edit) RememberSurface(edit.Snapshot);
                return result;
            }
            catch (SupersededRequestException) { throw; }
            catch (OperationCanceledException) when (abandoned) { throw; }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw new TimeoutException("The preview did not respond in time. Refresh to start a new preview process.");
            }
            catch { await StopCoreAsync().ConfigureAwait(false); throw; }
        }
        finally { if (releaseGate) _gate.Release(); }
    }

    private async Task DrainSupersededAsync(Task call)
    {
        try
        {
            try { await call.WaitAsync(_supersededRequestGrace, _shutdown.Token).ConfigureAwait(false); }
            // The host acknowledged the cancellation before starting the work.
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested) { }
            // A host that is still busy after the grace period, disconnected, or failed
            // is not trusted with the next request.
            catch (Exception) { await StopCoreAsync().ConfigureAwait(false); }
        }
        finally { _gate.Release(); }
    }

    private sealed class SupersededRequestException(CancellationToken token)
        : OperationCanceledException("A newer preview request superseded this one.", token);

    private async Task StartAsync(CancellationToken token)
    {
        var host = hostPath ?? FindHost();
        if (!File.Exists(host)) throw new FileNotFoundException("Build WpfStudio.PreviewHost before opening the designer.", host);
        string sessionId = Guid.NewGuid().ToString("N");
        var shadow = PreviewShadowDirectory.Create();
        _shadowDirectory = shadow;
        var worker = new WorkerProcessHost(new(host, "WpfStudio.Preview." + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(15))
        {
            Arguments = ["--session", sessionId, "--parent-process", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--shadow-directory", shadow.Path, "--shadow-token", shadow.Token],
            ConfigureStart = start => start.Environment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"] = "1"
        });
        PreviewProcessLease? lease = null;
        worker.Exited += () =>
        {
            // HasExited can raise this event synchronously while the process lease
            // holds its ownership lock. Never invoke subscribers under that lock.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                shadow.CleanupAsync().Forget();
                if (lease is not null && ReferenceEquals(Volatile.Read(ref _lease), lease) && _proxy is not null && !_disposed)
                {
                    string reason = lease.Reason ?? worker.ErrorTail;
                    Disconnected?.Invoke(this, string.IsNullOrEmpty(reason) ? "Preview process exited. Refresh to reconnect." : reason);
                }
            });
        };
        worker.Start();
        lease = new PreviewProcessLease(worker, sessionId);
        Volatile.Write(ref _lease, lease);
        try { await worker.ConnectAsync(token).ConfigureAwait(false); }
        catch (WorkerConnectionException ex) { throw new InvalidOperationException("Preview process could not connect. " + ex.ErrorTail, ex); }
        _rpc = new JsonRpc(worker.Pipe);
        _proxy = _rpc.Attach<IPreviewRpc>();
        _rpc.StartListening();
        await ValidateSessionAsync(lease, token).ConfigureAwait(false);
    }

    private static string FindHost()
    {
        var configuration = typeof(PreviewClient).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        // Transitive project references can copy an apphost executable without its managed entry point. Skip those
        // incomplete outputs and continue to the packaged or development host directory.
        return WorkerHostLocator.Find("WpfStudio.PreviewHost", "PreviewHost", "net10.0-windows", [configuration], [".exe", ".dll"],
            ["WpfStudio.PreviewHost.dll", "WpfStudio.PreviewHost.runtimeconfig.json", "WpfStudio.Wpf.PropertyEditing.dll", "WpfStudio.Wpf.Diagnostics.dll",
             "WpfStudio.Inspection.Protocol.dll", "WpfStudio.PreviewHost.deps.json"]);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource previousEpoch;
        Task previousStop;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            if (_disposed) return Task.CompletedTask;
            previousEpoch = _requestEpoch;
            _requestEpoch = new();
            previousStop = _stopBarrier;
            _stopBarrier = completion.Task;
        }
        // Revocation does not wait for the RPC gate: an unresponsive constructor
        // or getter cannot hold Stop behind the ordinary request deadline.
        previousEpoch.Cancel();
        previousEpoch.Dispose();
        var stopping = FinishStopAsync(previousStop, completion);
        // Once requested, cleanup continues even if its caller stops waiting.
        return stopping.WaitAsync(cancellationToken);
    }

    private async Task FinishStopAsync(Task previousStop, TaskCompletionSource completion)
    {
        try
        {
            await previousStop.ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync().ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        finally { completion.TrySetResult(); }
    }
    private async Task StopCoreAsync()
    {
        var lease = Volatile.Read(ref _lease);
        var shadow = _shadowDirectory; _shadowDirectory = null;
        _proxy = null; _rpc?.Dispose(); _rpc = null;
        if (lease is not null)
        {
            // Run independently of the editor dispatcher. Native parenting can couple
            // input queues, so cleanup may not depend on a continuation on that thread.
            await Task.Run(() => { lease.Terminate("Preview stopped."); lease.Dispose(); }).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _lease, null, lease);
        }
        RetireInteraction();
        if (shadow is not null) await shadow.CleanupAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;
            }
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
