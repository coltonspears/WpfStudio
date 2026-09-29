using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StreamJsonRpc;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Runtime.Design;

public interface IPreviewClient : IPreviewRpc, IAsyncDisposable
{
    event EventHandler<string>? Disconnected;
    IPreviewInteractionSession? CreateInteractionSession(PreviewSurfaceIdentity surface) => null;
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owns one isolated WPF process, with bounded requests and an independent native-interaction watchdog.</summary>
public sealed partial class PreviewClient(string? hostPath = null, TimeSpan? requestTimeout = null) : IPreviewClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _requestEpoch = new();
    private Task _stopBarrier = Task.CompletedTask;
    private readonly TimeSpan _timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    private PreviewProcessLease? _lease;
    private PreviewShadowDirectory? _shadowDirectory;
    private NamedPipeClientStream? _pipe;
    private JsonRpc? _rpc;
    private IPreviewRpc? _proxy;
    private string? _assembly;
    private (long Length, long LastWriteTicks)? _assemblyStamp;
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
        bool releaseGate = true;
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
                if (render != null && (_proxy == null || ProcessId == null || render.Mode == PreviewMode.Compiled || render.Scenario is not null || _scenarioActive ||
                    !string.Equals(_assembly, render.AssemblyPath, StringComparison.OrdinalIgnoreCase) ||
                    _assemblyStamp != GetAssemblyStamp(render.AssemblyPath) ||
                    _mode != render.Mode || !string.Equals(_applicationResourcePath, render.ApplicationResourcePath, StringComparison.Ordinal) ||
                    !string.Equals(_projectDirectory, render.ProjectDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    await StartAsync(deadline.Token).ConfigureAwait(false);
                    _assembly = render.AssemblyPath; _assemblyStamp = GetAssemblyStamp(render.AssemblyPath); _projectDirectory = render.ProjectDirectory;
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
                    _ = DrainSupersededAsync(call);
                    throw new SupersededRequestException(token);
                }
                lifetime.Token.ThrowIfCancellationRequested();
                if (result is PreviewSnapshot snapshot) RememberSurface(snapshot);
                else if (result is PreviewEditResult edit) RememberSurface(edit.Snapshot);
                return result;
            }
            catch (SupersededRequestException) { throw; }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw new TimeoutException("The preview did not respond in time. Refresh to start a new preview process.");
            }
            catch { await StopCoreAsync().ConfigureAwait(false); throw; }
        }
        finally { if (releaseGate) _gate.Release(); }
    }

    /// <summary>How long a superseded render may keep the host busy before it is treated as hung.</summary>
    internal static TimeSpan SupersededRequestGrace { get; set; } = TimeSpan.FromSeconds(3);

    private async Task DrainSupersededAsync(Task call)
    {
        try
        {
            try { await call.WaitAsync(SupersededRequestGrace, _shutdown.Token).ConfigureAwait(false); }
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
        var pipeName = "WpfStudio.Preview." + Guid.NewGuid().ToString("N");
        string sessionId = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(host.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : host)
        {
            WorkingDirectory = Path.GetDirectoryName(host)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.Environment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"] = "1";
        if (host.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(host);
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--session"); start.ArgumentList.Add(sessionId);
        start.ArgumentList.Add("--parent-process"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var shadow = PreviewShadowDirectory.Create();
        _shadowDirectory = shadow;
        start.ArgumentList.Add("--shadow-directory"); start.ArgumentList.Add(shadow.Path);
        start.ArgumentList.Add("--shadow-token"); start.ArgumentList.Add(shadow.Token);
        // Output arriving from an older process must never replace the current
        // host's disconnect or connection-failure explanation.
        string error = "";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        PreviewProcessLease? lease = null;
        process.ErrorDataReceived += (_, args) => { if (args.Data != null) Volatile.Write(ref error, args.Data); };
        process.OutputDataReceived += (_, _) => { };
        process.Exited += (_, _) =>
        {
            // HasExited can raise this event synchronously while the process lease
            // holds its ownership lock. Never invoke subscribers under that lock.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                _ = shadow.CleanupAsync();
                if (lease is not null && ReferenceEquals(Volatile.Read(ref _lease), lease) && _proxy is not null && !_disposed)
                {
                    string reason = lease.Reason ?? Volatile.Read(ref error);
                    Disconnected?.Invoke(this, string.IsNullOrEmpty(reason) ? "Preview process exited. Refresh to reconnect." : reason);
                }
            });
        };
        try
        {
            process.Start();
            lease = new PreviewProcessLease(process, sessionId);
            Volatile.Write(ref _lease, lease);
        }
        catch
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
            throw;
        }
        process.BeginErrorReadLine(); process.BeginOutputReadLine();
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await _pipe.ConnectAsync(15_000, token).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new InvalidOperationException("Preview process could not connect. " + Volatile.Read(ref error), ex); }
        if (!GetNamedPipeServerProcessId(_pipe.SafePipeHandle, out uint serverPid) || serverPid != lease.ProcessId)
            throw new InvalidDataException("The preview pipe server is not the process launched for this session.");
        _rpc = new JsonRpc(_pipe);
        _proxy = _rpc.Attach<IPreviewRpc>();
        _rpc.StartListening();
        await ValidateSessionAsync(lease, token).ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    private static string FindHost()
    {
        var configuration = typeof(PreviewClient).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var folders = new List<string> { Path.Combine(AppContext.BaseDirectory, "PreviewHost"), AppContext.BaseDirectory };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            folders.Add(Path.Combine(directory.FullName, "src", "WpfStudio.PreviewHost", "bin", configuration, "net10.0-windows"));
        foreach (var folder in folders)
            foreach (var extension in new[] { ".exe", ".dll" })
            {
                var candidate = Path.Combine(folder, "WpfStudio.PreviewHost" + extension);
                // Transitive project references can copy an apphost executable
                // without its managed entry point. Skip those incomplete outputs
                // and continue to the packaged or development host directory.
                if (File.Exists(candidate) &&
                    File.Exists(Path.Combine(folder, "WpfStudio.PreviewHost.dll")) &&
                    File.Exists(Path.Combine(folder, "WpfStudio.PreviewHost.runtimeconfig.json")) &&
                    File.Exists(Path.Combine(folder, "WpfStudio.Wpf.PropertyEditing.dll")) &&
                    File.Exists(Path.Combine(folder, "WpfStudio.Wpf.Diagnostics.dll")) &&
                    File.Exists(Path.Combine(folder, "WpfStudio.Inspection.Protocol.dll")) &&
                    File.Exists(Path.Combine(folder, "WpfStudio.PreviewHost.deps.json"))) return candidate;
            }
        return Path.Combine(AppContext.BaseDirectory, "PreviewHost", "WpfStudio.PreviewHost.exe");
    }

    private static (long Length, long LastWriteTicks)? GetAssemblyStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var file = new FileInfo(path);
        return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : null;
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
        _proxy = null; _rpc?.Dispose(); _rpc = null; _pipe?.Dispose(); _pipe = null;
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
