using System.IO;
using WpfStudio.Contracts;

namespace WpfStudio.Runtime.Design;

public sealed partial class PreviewClient
{
    private PreviewHostSession? _hostSession;
    private PreviewSurfaceIdentity? _surface;
    private SurfaceSequence? _surfaceSequence;
    private InteractionSession? _interaction;

    public IPreviewInteractionSession? CreateInteractionSession(PreviewSurfaceIdentity surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (_lifecycle)
        {
            var lease = _lease;
            if (_disposed || lease is not { IsAlive: true } || _proxy is null ||
                _hostSession is not { NativeInteractionAvailable: true } || _surface != surface ||
                surface.SessionId != lease.SessionId || _surfaceSequence is null) return null;
            if (_interaction is { IsAvailable: true } current && current.Surface == surface) return current;
            if (_interaction is { IsAttached: true }) return null;
            return _interaction = new InteractionSession(this, lease, _proxy, surface, _surfaceSequence);
        }
    }

    public Task<PreviewHostSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(proxy => proxy.GetSessionAsync(cancellationToken), null, cancellationToken);

    public Task<PreviewSurfaceResponse> UpdateSurfaceAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken = default)
    {
        var session = CreateInteractionSession(request.Surface);
        return session is null
            ? Task.FromResult(new PreviewSurfaceResponse(request, false, "This native preview surface is no longer current."))
            : session.UpdateAsync(request, cancellationToken);
    }

    public Task<PreviewSurfaceHeartbeat> HeartbeatSurfaceAsync(PreviewSurfaceHeartbeatRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(proxy => proxy.HeartbeatSurfaceAsync(request, cancellationToken), null, cancellationToken);

    private async Task ValidateSessionAsync(PreviewProcessLease lease, CancellationToken token)
    {
        var session = await _proxy!.GetSessionAsync(token).WaitAsync(token).ConfigureAwait(false);
        if (session.SessionId != lease.SessionId || session.ProcessId != lease.ProcessId ||
            session.ParentProcessId != Environment.ProcessId || session.ProtocolVersion != PreviewNativeNavigation.ProtocolVersion)
            throw new InvalidDataException("The preview handshake does not match the launched process and parent session.");
        lock (_lifecycle) _hostSession = session;
    }

    private void RememberSurface(PreviewSnapshot snapshot)
    {
        lock (_lifecycle)
        {
            if (snapshot.Surface is { } surface && (surface.SessionId != _lease?.SessionId ||
                surface.Version != snapshot.Version || string.IsNullOrEmpty(surface.SurfaceId) || surface.SurfaceId.Length > 128))
                throw new InvalidDataException("The preview response contains an invalid native surface identity.");
            // An ordinary failed/stale capture is not authority to retire a still-live surface.
            if (snapshot.Success)
            {
                if (_surface != snapshot.Surface) _surfaceSequence = snapshot.Surface is null ? null : new();
                _surface = snapshot.Surface;
            }
        }
    }

    private async Task PrepareRenderAsync(CancellationToken deadline, CancellationToken lifetime)
    {
        InteractionSession? interaction;
        lock (_lifecycle) { interaction = _interaction; _surface = null; }
        if (interaction is null) return;
        try
        {
            if (interaction.IsAttached && _proxy is { } proxy)
                await interaction.DeactivateDirectAsync(proxy, deadline).ConfigureAwait(false);
            interaction.Retire();
            lock (_lifecycle) { if (ReferenceEquals(_interaction, interaction)) { _interaction = null; _surface = null; } }
        }
        catch
        {
            // The watchdog can end a hung detach. A new render may recover with a
            // new launch, but user cancellation still cancels this render.
            await StopCoreAsync().ConfigureAwait(false);
            lifetime.ThrowIfCancellationRequested();
        }
    }

    private void RetireInteraction()
    {
        lock (_lifecycle)
        {
            _interaction?.Retire();
            _interaction = null;
            _surface = null;
            _surfaceSequence = null;
            _hostSession = null;
        }
    }

    private sealed class SurfaceSequence
    {
        private long _value;
        public long Next()
        {
            long value = Interlocked.Increment(ref _value);
            return value > 0 ? value : throw new InvalidOperationException("The native preview command sequence is exhausted.");
        }
    }

    private sealed class InteractionSession(PreviewClient owner, PreviewProcessLease lease, IPreviewRpc proxy,
        PreviewSurfaceIdentity surface, SurfaceSequence sequence) : IPreviewInteractionSession
    {
        private readonly object _state = new();
        private CancellationTokenSource? _watchdog;
        private PreviewSurfaceRequest? _lastRequest;
        private long _navigationSequence;
        private long _attachmentSequence, _focusSequence;
        private string? _status;
        private bool _attached;
        private bool _revoked;

        public PreviewSurfaceIdentity Surface { get; } = surface;
        public int NativeProcessId => lease.ProcessId;
        public bool IsAvailable { get { lock (_state) return !_revoked && lease.IsAlive; } }
        public bool IsAttached { get { lock (_state) return _attached && !lease.HasExited; } }
        public event EventHandler<PreviewInteractionEvent>? Changed;

        public Task<PreviewSurfaceResponse> UpdateAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Surface != Surface || request.ParentProcessId != Environment.ProcessId)
                return Task.FromResult(new PreviewSurfaceResponse(request, false, "The requested surface or parent process does not match this session."));
            return owner.ExecuteAsync(p => UpdateDirectAsync(p, request, cancellationToken), null, cancellationToken,
                lease, () => IsAvailable);
        }

        private async Task<PreviewSurfaceResponse> UpdateDirectAsync(IPreviewRpc currentProxy, PreviewSurfaceRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_state)
            {
                if (_revoked || !lease.IsAlive)
                    return new(request, false, "This native preview session has ended.");
                if (request.Action != PreviewSurfaceAction.Attach && !_attached)
                    return new(request, false, "Attach the native preview before updating it.");
                if (_lastRequest is { } previous && _attached &&
                    (request.BridgeToken != previous.BridgeToken || request.ParentHandle != previous.ParentHandle
                        || request.NavigationToken != previous.NavigationToken))
                    return new(request, false, "Detach the existing bridge before changing its identity.");
                request = request with { Sequence = sequence.Next() };
                _lastRequest = request;
                if (request.Action == PreviewSurfaceAction.Attach)
                {
                    _attachmentSequence = request.Sequence;
                    _attached = true; // Pending attach also needs protection before foreign parenting starts.
                    Arm();
                }
            }
            var result = await currentProxy.UpdateSurfaceAsync(request, token).ConfigureAwait(false);
            if (result.Request != request) throw new InvalidDataException("The native preview acknowledgement does not match its command.");
            if (!result.Success)
            {
                // A native call can invoke target callbacks before reporting failure.
                // Do not infer that parenting/capture remained unchanged.
                Abort(result.Status ?? "The native preview could not complete its surface operation.");
                return result;
            }
            lock (_state)
            {
                if (_revoked) throw new InvalidOperationException("The native preview session ended while processing this command.");
                // A failed attach may have partially parented a window. Keep the
                // watchdog and teardown obligation until a successful detach.
                if (result.Success && request.Action == PreviewSurfaceAction.Detach)
                {
                    _attached = false;
                    Disarm();
                }
            }
            return result;
        }

        public async Task DeactivateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAttached) return;
            await owner.ExecuteAsync(async p => { await DeactivateDirectAsync(p, cancellationToken).ConfigureAwait(false); return true; },
                null, cancellationToken, lease, () => IsAvailable).ConfigureAwait(false);
        }

        internal async Task DeactivateDirectAsync(IPreviewRpc currentProxy, CancellationToken token)
        {
            PreviewSurfaceRequest request;
            lock (_state)
            {
                if (!_attached || lease.HasExited) return;
                request = _lastRequest! with { Action = PreviewSurfaceAction.Detach, FocusToken = null };
            }
            var response = await UpdateDirectAsync(currentProxy, request, token).WaitAsync(token).ConfigureAwait(false);
            if (!response.Success) throw new InvalidOperationException(response.Status ?? "The native preview could not detach.");
        }

        private void Arm()
        {
            if (_watchdog is not null) return;
            var lifetime = _watchdog = new CancellationTokenSource();
            _ = Task.Run(() => WatchAsync(lifetime));
        }

        private void Disarm()
        {
            var lifetime = _watchdog;
            _watchdog = null;
            lifetime?.Cancel();
            // The loop disposes its own source after its pending probe ends.
        }

        private async Task WatchAsync(CancellationTokenSource lifetime)
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    // This direct proxy call deliberately bypasses the client's ordinary
                    // request gate. The host acknowledgement also bypasses its render gate.
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(3));
                    var heartbeat = await proxy.HeartbeatSurfaceAsync(new(Surface), probe.Token).WaitAsync(probe.Token).ConfigureAwait(false);
                    if (heartbeat.Surface != Surface || !heartbeat.Available)
                        throw new InvalidDataException(heartbeat.Status ?? "The native preview surface is no longer available.");
                    // A synchronous native send can run a nested message loop. Successful
                    // heartbeats during that loop do not prove the original handoff finished.
                    if (heartbeat.NavigationPendingSinceTick > 0 &&
                        Environment.TickCount64 - heartbeat.NavigationPendingSinceTick >= 3000)
                        throw new TimeoutException("The native preview keyboard handoff did not finish within three seconds.");
                    PreviewInteractionEvent? notification = null;
                    lock (_state)
                    {
                        if (!ReferenceEquals(_watchdog, lifetime) || _revoked) return;
                        var focus = heartbeat.Focus is { } observed && observed.Sequence > _focusSequence
                            && observed.AttachmentSequence == _attachmentSequence
                            && observed.BridgeToken == _lastRequest?.BridgeToken ? observed : null;
                        if (heartbeat.NavigationSequence > _navigationSequence || _status != heartbeat.Status || focus is not null)
                        {
                            var navigation = heartbeat.NavigationSequence > _navigationSequence ? heartbeat.Navigation : PreviewSurfaceNavigation.None;
                            _navigationSequence = Math.Max(_navigationSequence, heartbeat.NavigationSequence);
                            _status = heartbeat.Status;
                            if (focus is not null) _focusSequence = focus.Sequence;
                            notification = new(true, _status, navigation, _navigationSequence, focus);
                        }
                    }
                    if (notification is not null) Notify(notification);
                    await Task.Delay(250, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                try { AbortCore("Native preview stopped because its independent heartbeat or keyboard handoff check failed. " + ex.Message, lifetime); }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                { Notify(new(false, "Native preview termination could not be confirmed. Keep its native bridge alive. " + failure.Message)); }
            }
            finally
            {
                lock (_state) { if (ReferenceEquals(_watchdog, lifetime)) _watchdog = null; }
                lifetime.Dispose();
            }
        }

        public void Abort(string reason) => AbortCore(reason, null);

        private void AbortCore(string reason, CancellationTokenSource? watchdog)
        {
            lock (_state)
            {
                if (watchdog is not null && (!ReferenceEquals(_watchdog, watchdog) || watchdog.IsCancellationRequested || _revoked)) return;
                _revoked = true;
                if (_attached) lease.Terminate(reason);
                _attached = false;
                Disarm();
            }
            Notify(new(false, reason));
        }

        internal void Retire()
        {
            lock (_state)
            {
                if (_attached && !lease.HasExited)
                    throw new InvalidOperationException("A native preview cannot be retired before detach or process termination.");
                _revoked = true;
                _attached = false;
                Disarm();
            }
        }

        private void Notify(PreviewInteractionEvent value)
        {
            try { Changed?.Invoke(this, value); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }
}
