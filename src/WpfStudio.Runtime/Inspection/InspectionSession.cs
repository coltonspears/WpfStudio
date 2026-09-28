using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Runtime.Inspection;

public interface IInspectionSession : IAsyncDisposable
{
    InspectionHello? Hello { get; }
    bool IsConnected { get; }
    bool IsDebuggerPaused { get; }
    string Status { get; }
    event Action? StateChanged;
    Task<InspectionHello> WaitForConnectionAsync(CancellationToken cancellationToken = default);
    Task<InspectionTree> SnapshotAsync(InspectionTreeRequest? request = null, CancellationToken cancellationToken = default);
    Task<InspectionElement> InspectAsync(InspectionNodeRequest request, CancellationToken cancellationToken = default);
    Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BindingSourceResponse(request, false, Status: "This inspection session does not provide binding source navigation."));
    Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppearanceResponse(request, AppearanceSnapshot.Unavailable("This inspection session does not provide appearance details.")));
    Task<InspectionModuleCatalog> GetModulesAsync(CancellationToken cancellationToken = default);
    Task<InspectionPickState> SetPickingAsync(InspectionPickRequest request, CancellationToken cancellationToken = default);
    Task<InspectionHighlightResult> HighlightAsync(InspectionHighlightRequest request, CancellationToken cancellationToken = default);
    Task<InspectionPropertyValidation> ValidatePropertyAsync(InspectionPropertyEdit request, CancellationToken cancellationToken = default);
    Task<InspectionSourcePropertyResult> ValidateSourcePropertyAsync(InspectionSourcePropertyRequest request, CancellationToken cancellationToken = default);
    Task<InspectionPropertyEditResult> SetPropertyAsync(InspectionPropertyEdit request, CancellationToken cancellationToken = default);
    Task<InspectionPropertyEditResult> GetEditStatusAsync(InspectionEditStatusRequest request, CancellationToken cancellationToken = default);
    void SetDebuggerPaused(bool paused);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns only an authenticated inspection connection. Disconnect, cancellation, timeout,
/// and disposal never terminate or otherwise control the application's process.
/// </summary>
public sealed class InspectionSession : IInspectionSession
{
    private sealed record Pending(string Kind, TaskCompletionSource<InspectionMessage> Completion);
    private readonly object _stateGate = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly TaskCompletionSource<InspectionHello> _connection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly Task _listener;
    private Task? _reader;
    private PauseAwareDeadline? _connectionDeadline;
    private CancellationTokenSource _running = new();
    private InspectionHello? _hello;
    private int? _expectedProcessId;
    private long _sequence;
    private bool _connected;
    private bool _paused;
    private bool _closed;
    private string _status = "Waiting for the application's inspection agent…";

    public InspectionSession(TimeSpan? connectionTimeout = null, TimeSpan? requestTimeout = null)
    {
        _connectionTimeout = ValidateTimeout(connectionTimeout ?? TimeSpan.FromSeconds(30), nameof(connectionTimeout));
        _requestTimeout = ValidateTimeout(requestTimeout ?? TimeSpan.FromSeconds(3), nameof(requestTimeout));
        _lifetimeToken = _lifetime.Token;
        PipeName = "WpfStudio.Inspection." + Guid.NewGuid().ToString("N");
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        SessionId = Guid.NewGuid().ToString("N");
        OwnerProcessId = Environment.ProcessId;
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
        _listener = ListenAsync();
    }

    public string PipeName { get; }
    public string Token { get; }
    public string SessionId { get; }
    public int OwnerProcessId { get; }
    public InspectionHello? Hello { get { lock (_stateGate) return _hello; } }
    public bool IsConnected { get { lock (_stateGate) return _connected && !_closed; } }
    public bool IsDebuggerPaused { get { lock (_stateGate) return _paused; } }
    public string Status { get { lock (_stateGate) return _status; } }
    /// <summary>Raised on a transport thread; callers marshal to their UI dispatcher.</summary>
    public event Action? StateChanged;

    public void ExpectProcess(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        bool mismatch;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_expectedProcessId is { } expected && expected != processId)
                throw new InvalidOperationException("The expected application process is already set for this inspection session.");
            _expectedProcessId = processId;
            mismatch = _hello is { } hello && hello.ProcessId != processId;
        }
        if (mismatch)
        {
            var failure = new InvalidDataException("The inspection agent is not the launched application process.");
            Close(failure.Message, failure);
            throw failure;
        }
    }

    public Task<InspectionHello> WaitForConnectionAsync(CancellationToken cancellationToken = default) =>
        _connection.Task.WaitAsync(cancellationToken);

    public Task<InspectionTree> SnapshotAsync(InspectionTreeRequest? request = null, CancellationToken cancellationToken = default) =>
        RequestAsync<InspectionTree>("tree", request ?? new InspectionTreeRequest(), cancellationToken);

    public Task<InspectionModuleCatalog> GetModulesAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<InspectionModuleCatalog>("modules", new { }, cancellationToken);

    public Task<InspectionElement> InspectAsync(InspectionNodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<InspectionElement>("inspect", request, cancellationToken);
    }

    public async Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Hello is { } hello && !hello.Capabilities.Contains("appearance"))
            return new(request, AppearanceSnapshot.Unavailable("This inspection agent does not provide appearance details."));
        var result = await RequestAsync<AppearanceResponse>("appearance", request, cancellationToken).ConfigureAwait(false);
        if (result.Request != request || result.Snapshot is null)
            throw new InvalidDataException("The appearance response does not match the selected runtime property.");
        return result;
    }

    public async Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Hello is { } hello && !hello.Capabilities.Contains("binding-source"))
            return new(request, false, Status: "This inspection agent does not provide binding source navigation.");
        var result = await RequestAsync<BindingSourceResponse>("binding-source", request, cancellationToken).ConfigureAwait(false);
        ValidateBindingSourceResponse(request, result);
        return result;
    }

    internal static void ValidateBindingSourceResponse(BindingSourceRequest request, BindingSourceResponse result)
    {
        if (result.Request != request || result.Available &&
            (result.Declaration is not { Source: not null } declaration || declaration.ExpressionId != request.ExpressionId ||
             declaration.DeclarationId != request.DeclarationId))
            throw new InvalidDataException("The binding source response does not match the selected binding declaration.");
    }

    public Task<InspectionPickState> SetPickingAsync(InspectionPickRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<InspectionPickState>("pick", request, cancellationToken);
    }

    public Task<InspectionHighlightResult> HighlightAsync(InspectionHighlightRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<InspectionHighlightResult>("highlight", request, cancellationToken);
    }

    public Task<InspectionPropertyValidation> ValidatePropertyAsync(InspectionPropertyEdit request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<InspectionPropertyValidation>("validate-property", request, cancellationToken);
    }

    public async Task<InspectionSourcePropertyResult> ValidateSourcePropertyAsync(InspectionSourcePropertyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await RequestAsync<InspectionSourcePropertyResult>("validate-source-property", request, cancellationToken).ConfigureAwait(false);
        if (result.Revision != request.Revision || result.NodeId != request.NodeId || result.PropertyId != request.PropertyId || result.SourceEditToken != request.SourceEditToken
            || result.Success && (result.Property is null || (!request.VerifyOnly && !request.Remove && (result.IsNull != request.IsNull || !result.IsNull && result.Literal is null))))
            throw new InvalidDataException("The source validation acknowledgement does not match the observed property or proposal.");
        return result;
    }

    public async Task<InspectionPropertyEditResult> SetPropertyAsync(InspectionPropertyEdit request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool sending = false;
        try
        {
            var result = await RequestAsync<InspectionPropertyEditResult>("set-property", request, cancellationToken,
                () => sending = true).ConfigureAwait(false);
            ValidateEditResult(result, request.OperationId);
            if (result.Element is { } element && (element.NodeId != request.NodeId || element.Revision != request.Revision))
                throw new InvalidDataException("The property edit acknowledgement refers to a different element observation.");
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!sending) return new(request.OperationId, "Rejected", Error: "The edit was not sent. " + exception.Message);
            // A cancelled wait cannot establish whether a setter already ran. EOF
            // cancels the agent's queued mutations and restores its owned overrides.
            Close("Property edit interrupted; its outcome is unknown. Inspector cleanup was requested; the application keeps running.", exception);
            return new(request.OperationId, "Unknown", Error: "The edit outcome is unknown because the inspection connection ended. " + exception.Message);
        }
    }

    public async Task<InspectionPropertyEditResult> GetEditStatusAsync(InspectionEditStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await RequestAsync<InspectionPropertyEditResult>("edit-status", request, cancellationToken).ConfigureAwait(false);
        ValidateEditResult(result, request.OperationId);
        return result;
    }

    private static void ValidateEditResult(InspectionPropertyEditResult result, string operationId)
    {
        if (result.OperationId != operationId || result.Outcome is not ("Applied" or "Reset" or "Rejected" or "Conflict" or "Unknown"))
            throw new InvalidDataException("The property edit acknowledgement has an invalid operation identity or outcome.");
    }

    public void SetDebuggerPaused(bool paused)
    {
        CancellationTokenSource? cancel = null;
        PauseAwareDeadline? connectionDeadline;
        lock (_stateGate)
        {
            if (_closed || _paused == paused) return;
            _paused = paused;
            if (paused) cancel = _running;
            else _running = new CancellationTokenSource();
            _status = paused ? "Inspection paused while the debugger has stopped managed threads."
                : _connected ? "Connected to application process " + _hello!.ProcessId + "."
                : "Waiting for the application's inspection agent…";
            connectionDeadline = _connectionDeadline;
        }
        // Cancel an in-flight read-only request, but retain the pipe and ignore its eventual
        // response. Managed agents cannot respond while their debugger has stopped them.
        cancel?.Cancel();
        connectionDeadline?.SetPaused(paused);
        NotifyStateChanged();
    }

    private async Task ListenAsync()
    {
        using var deadline = new PauseAwareDeadline(_connectionTimeout, _lifetimeToken);
        lock (_stateGate) { _connectionDeadline = deadline; deadline.SetPaused(_paused); }
        try
        {
            await _pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            var message = await InspectionWire.ReadAsync(_pipe, deadline.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The inspection agent disconnected before its handshake.");
            if (message.Kind != "hello" || message.Id != 0 || message.Error is not null)
                throw new InvalidDataException("The inspection agent sent an invalid handshake.");
            var hello = message.GetPayload<InspectionHello>();
            if (hello.ProtocolVersion != InspectionProtocol.Version)
                throw new InvalidDataException("The application's inspection agent uses an incompatible protocol version.");
            if (hello.Token is null || hello.Token.Length != Token.Length
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Token), Encoding.UTF8.GetBytes(Token)))
                throw new InvalidDataException("The inspection agent could not authenticate this session.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Running WPF application inspection requires Windows.");
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var actualProcessId))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The inspection pipe client process could not be verified.");
            if (hello.ProcessId <= 0 || actualProcessId != (uint)hello.ProcessId)
                throw new InvalidDataException("The inspection handshake does not match the operating system's pipe client process.");
            if (string.IsNullOrWhiteSpace(hello.RuntimeVersion) || hello.RuntimeVersion.Length > 128
                || string.IsNullOrWhiteSpace(hello.AgentVersion) || hello.AgentVersion.Length > 128
                || hello.Capabilities is null || hello.Capabilities.Count > 64 || hello.Capabilities.Any(c => c is null || c.Length > 128))
                throw new InvalidDataException("The inspection agent did not report its runtime, version, and capabilities.");
            lock (_stateGate)
            {
                if (_closed) return;
                if (_expectedProcessId is { } expected && hello.ProcessId != expected)
                    throw new InvalidDataException("The inspection agent is not the launched application process.");
                _hello = hello;
            }
            await InspectionWire.WriteAsync(_pipe, InspectionMessage.Create("hello", 0, new InspectionWelcome(InspectionProtocol.Version, SessionId)), deadline.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (_closed) return;
                _connected = true;
                _status = _paused ? "Inspection paused while the debugger has stopped managed threads."
                    : "Connected to application process " + hello.ProcessId + ".";
            }
            _reader = ReadResponsesAsync();
            _connection.TrySetResult(hello);
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (!_lifetimeToken.IsCancellationRequested)
        {
            var failure = new TimeoutException("The application did not connect to the inspector in time. It may use an unsupported runtime or have startup hooks disabled.");
            Close(failure.Message, failure);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or OperationCanceledException
            or Win32Exception or NotSupportedException)
        {
            if (!_lifetimeToken.IsCancellationRequested) Close("Inspection unavailable: " + exception.Message, exception);
        }
        finally { lock (_stateGate) _connectionDeadline = null; }
    }

    private async Task ReadResponsesAsync()
    {
        try
        {
            while (await InspectionWire.ReadAsync(_pipe, _lifetimeToken).ConfigureAwait(false) is { } message)
            {
                if (message.Id <= 0 || message.Id > Interlocked.Read(ref _sequence))
                    throw new InvalidDataException("The inspection agent sent an unexpected response ID.");
                if (!_pending.TryRemove(message.Id, out var pending)) continue; // Cancelled/paused request's late response.
                if (message.Kind != pending.Kind)
                {
                    var error = new InvalidDataException("The inspection response does not match its request kind.");
                    pending.Completion.TrySetException(error);
                    throw error;
                }
                pending.Completion.TrySetResult(message);
            }
            if (!_lifetimeToken.IsCancellationRequested) Close("The application disconnected from the inspector.", new EndOfStreamException("The inspection connection closed."));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (!_lifetimeToken.IsCancellationRequested) Close("Inspection disconnected: " + exception.Message, exception);
        }
    }

    private async Task<T> RequestAsync<T>(string kind, object payload, CancellationToken cancellationToken, Action? onSending = null)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        await _requests.WaitAsync(wait.Token).ConfigureAwait(false);
        long id = 0;
        try
        {
            CancellationToken running;
            lock (_stateGate)
            {
                if (_closed || !_connected) throw new InvalidOperationException("Connect to an application before inspecting it.");
                if (_paused) throw new InvalidOperationException("Inspection is paused while the debugger has stopped managed threads.");
                running = _running.Token;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(wait.Token, running);
            deadline.CancelAfter(_requestTimeout);
            deadline.Token.ThrowIfCancellationRequested();
            id = Interlocked.Increment(ref _sequence);
            var completion = new TaskCompletionSource<InspectionMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = new Pending(kind, completion);
            try
            {
                // Once a frame starts, finish it independently of caller/pause cancellation.
                // A failed/partial write closes this inspection pipe instead of corrupting it.
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                writeDeadline.CancelAfter(_requestTimeout);
                try
                {
                    onSending?.Invoke();
                    await InspectionWire.WriteAsync(_pipe, InspectionMessage.Create(kind, id, payload), writeDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException)
                {
                    Close("The inspector could not send its request. The application is still running.", exception);
                    throw;
                }
                var response = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                if (response.Error is { } error) throw new InvalidOperationException(error.Length <= 2000 ? error : error[..2000] + "…");
                return response.GetPayload<T>();
            }
            catch (OperationCanceledException) when (!wait.IsCancellationRequested && running.IsCancellationRequested)
            {
                throw new InvalidOperationException("Inspection is paused while the debugger has stopped managed threads.");
            }
            catch (OperationCanceledException) when (!wait.IsCancellationRequested)
            {
                var failure = new TimeoutException("The application did not answer the inspection request in time. The inspection connection was closed; the application is still running.");
                Close(failure.Message, failure);
                throw failure;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                Close("The inspection agent sent an invalid response payload.", exception);
                throw;
            }
        }
        finally
        {
            if (id != 0) _pending.TryRemove(id, out _);
            _requests.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
            bool acquired = false;
            try
            {
                await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
                acquired = true;
                if (IsConnected)
                    await InspectionWire.WriteAsync(_pipe, InspectionMessage.Create("detach", Interlocked.Increment(ref _sequence), new { }), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException) { }
            finally { if (acquired) _requests.Release(); }
        }
        Close("Inspector disconnected. The application's lifetime is unchanged.", new OperationCanceledException("The inspector disconnected."));
        await _listener.ConfigureAwait(false);
        if (_reader is { } reader) await reader.ConfigureAwait(false);
    }

    private void Close(string status, Exception failure)
    {
        lock (_stateGate)
        {
            if (_closed) return;
            _closed = true;
            _connected = false;
            _status = status;
        }
        _connection.TrySetException(failure);
        foreach (var pending in _pending.Values) pending.Completion.TrySetException(failure);
        _pending.Clear();
        _lifetime.Cancel();
        _pipe.Dispose();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (StateChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* UI listeners cannot interrupt transport cleanup. */ }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string name) => timeout > TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(10)
        ? timeout : throw new ArgumentOutOfRangeException(name, "Use a positive timeout no longer than ten minutes.");

    /// <summary>Debugger stops do not consume the agent's connection/handshake budget.</summary>
    private sealed class PauseAwareDeadline : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source;
        private readonly Timer _timer;
        private TimeSpan _remaining;
        private long _started;
        private bool _paused;
        private bool _expired;
        private bool _disposed;
        public CancellationToken Token => _source.Token;

        public PauseAwareDeadline(TimeSpan timeout, CancellationToken lifetime)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _remaining = timeout;
            _started = Stopwatch.GetTimestamp();
            _timer = new Timer(_ => Expire(), null, timeout, Timeout.InfiniteTimeSpan);
        }
        public void SetPaused(bool paused)
        {
            lock (_gate)
            {
                if (_disposed || _expired || paused == _paused) return;
                if (paused)
                {
                    _remaining -= Stopwatch.GetElapsedTime(_started);
                    _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _started = Stopwatch.GetTimestamp();
                    _timer.Change(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
                _paused = paused;
            }
        }
        private void Expire()
        {
            lock (_gate)
            {
                if (_disposed || _paused || _expired) return;
                var remaining = _remaining - Stopwatch.GetElapsedTime(_started);
                if (remaining > TimeSpan.Zero) { _timer.Change(remaining, Timeout.InfiniteTimeSpan); return; }
                _expired = true;
            }
            try { _source.Cancel(); } catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; }
            _timer.Dispose();
            _source.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
