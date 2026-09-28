using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

public sealed partial class PreviewEngine
{
    private readonly PreviewHostSession _session;
    private PreviewSurfaceIdentity? _surfaceIdentity;
    private long _surfaceSequence;
    private long _navigationSequence;
    private PreviewSurfaceNavigation _navigation;
    private long _detachedStyle;
    private nint _bridgeHandle;
    private string? _bridgeToken;
    private string? _nativeNavigationToken;
    private PreviewSurfaceRequest? _attachment;
    private long _nativeCallSequence;
    private long _navigationPendingSinceTick;
    private object? _nativeHandoff;
    private string? _nativeFailure;
    private IKeyboardInputSite? _previousKeyboardSite;
    private NativeKeyboardSite? _nativeKeyboardSite;
    private bool _nativeInputSubscribed;

    public Task<PreviewHostSession> GetSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_session);
    }

    public Task<PreviewSurfaceHeartbeat> HeartbeatSurfaceAsync(PreviewSurfaceHeartbeatRequest request, CancellationToken cancellationToken) =>
        // This acknowledgement deliberately bypasses _requests and runs no application
        // getters, layout or rendering. The client can observe a dispatcher blocked by an
        // input handler even while the ordinary RPC semaphore is occupied.
        _dispatcher.InvokeAsync(() => new PreviewSurfaceHeartbeat(request.Surface, CurrentSurface(request.Surface) && _nativeFailure is null,
            _navigationSequence, _navigation, CurrentSurface(request.Surface) ? _nativeFailure : "The native preview surface changed or closed.",
            CurrentSurface(request.Surface) ? _focusObservation : null, _navigationPendingSinceTick),
            DispatcherPriority.Normal, cancellationToken).Task;

    public Task<PreviewSurfaceResponse> UpdateSurfaceAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() =>
        {
            try
            {
                ValidateSurfaceRequest(request);
                _surfaceSequence = request.Sequence;
                switch (request.Action)
                {
                    case PreviewSurfaceAction.Attach:
                        AttachNativeSurface(request);
                        break;
                    case PreviewSurfaceAction.Update:
                        RequireAttachedBridge(request);
                        PositionNativeSurface(request);
                        break;
                    case PreviewSurfaceAction.Detach:
                        RequireAttachedBridge(request);
                        DetachNativeSurface();
                        break;
                    case PreviewSurfaceAction.FocusFirst:
                    case PreviewSurfaceAction.FocusLast:
                        RequireAttachedBridge(request);
                        if (!Guid.TryParseExact(request.FocusToken, "N", out _) ||
                            PreviewNativeMethods.GetProp(new nint(request.ParentHandle), "WpfStudio.PreviewFocus." + request.FocusToken) != new nint(1))
                            return Respond(true, "Keyboard entry was superseded before the preview received focus.");
                        bool focused = ((IKeyboardInputSink)_surface!).TabInto(new TraversalRequest(
                            request.Action == PreviewSurfaceAction.FocusLast ? FocusNavigationDirection.Last : FocusNavigationDirection.First));
                        if (!CurrentSurface(request.Surface)) return Respond(false, "The preview closed while entering keyboard focus.", focused);
                        return Respond(true, focused ? null : "The preview has no available keyboard tab stop.", focused);
                }
                if (!CurrentSurface(request.Surface)) return Respond(false, "The preview closed while applying the native surface change.");
                return Respond(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                return Respond(false, exception.GetBaseException().Message);
            }

            Task<PreviewSurfaceResponse> Respond(bool success, string? status = null, bool? focused = null) =>
                Task.FromResult(new PreviewSurfaceResponse(request, success, status, focused,
                    success && CurrentSurface(request.Surface) && _bridgeHandle != 0 ? _surface!.Handle.ToInt64() : 0,
                    success ? _focusObservation : null));
        }, cancellationToken);

    private bool CurrentSurface(PreviewSurfaceIdentity? identity) => !_disposed && identity is not null &&
        identity == _surfaceIdentity && identity.SessionId == _session.SessionId && identity.Version == _version &&
        _surface is { IsDisposed: false } && _surface.Handle != 0 && _viewport is not null && _root is not null;

    private void InitializeNativeSurface()
    {
        if (_surface is not { IsDisposed: false } || _surface.Handle == 0) return;
        _surfaceIdentity = new(_session.SessionId, _version, Guid.NewGuid().ToString("N"));
        _surfaceSequence = 0;
        _navigationSequence = 0;
        _navigation = PreviewSurfaceNavigation.None;
        _nativeCallSequence = 0;
        _navigationPendingSinceTick = 0;
        _nativeHandoff = null;
        _nativeFailure = null;
        _detachedStyle = PreviewNativeMethods.Style(_surface.Handle) & ~PreviewNativeMethods.Visible;
    }

    private void ValidateSurfaceRequest(PreviewSurfaceRequest request)
    {
        if (!_session.NativeInteractionAvailable) throw new InvalidOperationException("This host was not launched with an authenticated native preview session.");
        if (_nativeFailure is not null) throw new InvalidOperationException(_nativeFailure);
        if (!CurrentSurface(request.Surface)) throw new InvalidOperationException("The native preview surface changed or closed.");
        if (request.Sequence <= _surfaceSequence || request.Sequence <= 0) throw new InvalidOperationException("The native surface command was superseded.");
        if (!Enum.IsDefined(request.Action)) throw new InvalidOperationException("Unknown native surface action.");
        if (!Guid.TryParseExact(request.NavigationToken, "N", out _)) throw new InvalidOperationException("The native keyboard navigation token is invalid.");
        if (request.PixelWidth is < 1 or > 32768 || request.PixelHeight is < 1 or > 32768 ||
            request.OffsetX is < 0 or > 65536 || request.OffsetY is < 0 or > 65536)
            throw new InvalidOperationException("Native preview dimensions or scroll offsets are outside the supported range.");
        ValidateBridge(request);
        RequireOwnedSurface(request.Surface);
    }

    private void ValidateBridge(PreviewSurfaceRequest request)
    {
        if (request.ParentProcessId != _session.ParentProcessId || request.ParentHandle == 0 ||
            !Guid.TryParseExact(request.BridgeToken, "N", out _))
            throw new InvalidOperationException("The native preview bridge identity is invalid.");
        nint parent = new(request.ParentHandle);
        if (!PreviewNativeMethods.OwnedWindow(parent, _session.ParentProcessId) ||
            PreviewNativeMethods.GetProp(parent, "WpfStudio.PreviewBridge." + request.BridgeToken) != new nint(1))
            throw new InvalidOperationException("The native preview bridge closed or its lease changed.");
        nint parentContext = PreviewNativeMethods.GetWindowDpiAwarenessContext(parent);
        nint childContext = PreviewNativeMethods.GetWindowDpiAwarenessContext(_surface!.Handle);
        if (!PreviewNativeMethods.AreDpiAwarenessContextsEqual(parentContext, PreviewNativeMethods.PerMonitorV2) ||
            !PreviewNativeMethods.AreDpiAwarenessContextsEqual(parentContext, childContext))
            throw new InvalidOperationException("Native interaction requires matching PerMonitorV2 DPI awareness in the editor and preview.");
    }

    private nint RequireOwnedSurface(PreviewSurfaceIdentity identity)
    {
        if (!CurrentSurface(identity) || !PreviewNativeMethods.OwnedWindow(_surface!.Handle, _session.ProcessId))
            throw new InvalidOperationException("The preview no longer owns its native surface.");
        return _surface.Handle;
    }

    private void RequireAttachedBridge(PreviewSurfaceRequest request)
    {
        if (_bridgeHandle != new nint(request.ParentHandle) || _bridgeToken != request.BridgeToken ||
            _nativeNavigationToken != request.NavigationToken ||
            PreviewNativeMethods.GetParent(RequireOwnedSurface(request.Surface)) != _bridgeHandle)
            throw new InvalidOperationException("This surface is not attached to the requested bridge.");
    }

    private void AttachNativeSurface(PreviewSurfaceRequest request)
    {
        nint hwnd = RequireOwnedSurface(request.Surface);
        if (_bridgeHandle != 0 && (_bridgeHandle != new nint(request.ParentHandle) || _bridgeToken != request.BridgeToken))
            throw new InvalidOperationException("Detach the previous native bridge before attaching another one.");
        ValidateBridge(request);
        ShowWindow(hwnd, 0);
        RequireOwnedSurface(request.Surface);
        PreviewNativeMethods.SetStyle(hwnd, (_detachedStyle & ~(PreviewNativeMethods.Popup | PreviewNativeMethods.Caption | PreviewNativeMethods.ThickFrame)) |
            PreviewNativeMethods.Child | PreviewNativeMethods.ClipChildren | PreviewNativeMethods.ClipSiblings);
        ValidateBridge(request);
        RequireOwnedSurface(request.Surface);
        PreviewNativeMethods.Reparent(hwnd, new nint(request.ParentHandle));
        RequireOwnedSurface(request.Surface);
        _bridgeHandle = new nint(request.ParentHandle);
        _bridgeToken = request.BridgeToken;
        _nativeNavigationToken = request.NavigationToken;
        _attachment = request;
        _nativeHandoff = null;
        _navigationPendingSinceTick = 0;
        if (PreviewNativeMethods.GetParent(hwnd) != _bridgeHandle) throw new InvalidOperationException("The native preview parent was not applied.");
        InstallNativeInput();
        StartFocusObservation();
        PositionNativeSurface(request);
        ValidateBridge(request);
        ShowWindow(RequireOwnedSurface(request.Surface), 4); // SW_SHOWNOACTIVATE; input activation stays with the user.
        SetNavigation(PreviewSurfaceNavigation.None);
    }

    private void PositionNativeSurface(PreviewSurfaceRequest request)
    {
        RequireAttachedBridge(request);
        ValidateBridge(request);
        uint dpi = PreviewNativeMethods.GetDpiForWindow(_bridgeHandle);
        if (dpi is < 48 or > 768) throw new InvalidOperationException("The native preview bridge DPI is unavailable.");
        int width = Math.Clamp((int)Math.Ceiling(_width * dpi / 96d), 1, 32768);
        int height = Math.Clamp((int)Math.Ceiling(_height * dpi / 96d), 1, 32768);
        int x = -Math.Clamp(request.OffsetX, 0, Math.Max(0, width - request.PixelWidth));
        int y = -Math.Clamp(request.OffsetY, 0, Math.Max(0, height - request.PixelHeight));
        PreviewNativeMethods.Position(RequireOwnedSurface(request.Surface), x, y, width, height);
    }

    private void InstallNativeInput()
    {
        if (_nativeInputSubscribed) return;
        var sink = (IKeyboardInputSink)_surface!;
        _previousKeyboardSite = sink.KeyboardInputSite;
        _nativeKeyboardSite = new NativeKeyboardSite(sink, this);
        sink.KeyboardInputSite = _nativeKeyboardSite;
        InputManager.Current.PreProcessInput += OnNativeInput;
        _nativeInputSubscribed = true;
    }

    private void RemoveNativeInput()
    {
        if (_nativeInputSubscribed)
        {
            InputManager.Current.PreProcessInput -= OnNativeInput;
            _nativeInputSubscribed = false;
        }
        if (_surface is { IsDisposed: false } && ((IKeyboardInputSink)_surface).KeyboardInputSite == _nativeKeyboardSite)
            ((IKeyboardInputSink)_surface).KeyboardInputSite = _previousKeyboardSite;
        _nativeKeyboardSite = null;
        _previousKeyboardSite = null;
        StopFocusObservation();
    }

    private void OnNativeInput(object sender, PreProcessInputEventArgs args)
    {
        if (_bridgeHandle == 0 || args.StagingItem.Input is not KeyEventArgs { Key: Key.F8 } key ||
            Keyboard.FocusedElement is not DependencyObject focused || !BelongsToNativePreview(focused)) return;
        if (key.IsDown && !key.IsRepeat) SetNavigation(PreviewSurfaceNavigation.Inspect);
        key.Handled = true;
        args.Cancel();
    }

    private void SetNavigation(PreviewSurfaceNavigation navigation)
    {
        _navigation = navigation;
        _navigationSequence++;
    }

    private bool BelongsToNativePreview(DependencyObject target)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        for (DependencyObject? node = target; node is not null && visited.Count < 512 && visited.Add(node);)
        {
            if (ReferenceEquals(node, _viewport)) return true;
            node = node is Popup popup ? popup.PlacementTarget ?? LogicalTreeHelper.GetParent(popup) :
                VisualParent(node) ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void CloseNativePopups()
    {
        // PopupRoot's public logical Parent is the owning Popup. This also covers
        // ComboBox/ContextMenu/ToolTip templates without reflecting internal fields.
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().Take(512).ToArray())
        {
            if (source.IsDisposed || !ReferenceEquals(source.Dispatcher, _dispatcher) || source.RootVisual is not FrameworkElement { Parent: Popup popup } ||
                !BelongsToNativePreview(popup)) continue;
            if (popup.Child is ContextMenu menu) menu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            else if (popup.Child is ToolTip toolTip) toolTip.SetCurrentValue(ToolTip.IsOpenProperty, false);
            else if (popup.TemplatedParent is ComboBox combo) combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            popup.SetCurrentValue(Popup.IsOpenProperty, false);
        }
    }

    private void DetachNativeSurface()
    {
        var identity = _surfaceIdentity;
        _nativeHandoff = null;
        _navigationPendingSinceTick = 0;
        RemoveNativeInput();
        CloseNativePopups();
        if (identity is not null && CurrentSurface(identity))
        {
            nint hwnd = RequireOwnedSurface(identity);
            ShowWindow(hwnd, 0);
            PreviewNativeMethods.Reparent(RequireOwnedSurface(identity), 0);
            RequireOwnedSurface(identity);
            PreviewNativeMethods.SetStyle(RequireOwnedSurface(identity), _detachedStyle & ~PreviewNativeMethods.Visible);
            // While WS_CHILD remains set, GetParent can report the desktop after
            // SetParent(NULL). Check the restored top-level window relationship.
            if (PreviewNativeMethods.GetParent(RequireOwnedSurface(identity)) != 0)
                throw new InvalidOperationException("The native preview could not detach from its bridge.");
            uint dpi = PreviewNativeMethods.GetDpiForWindow(RequireOwnedSurface(identity));
            double scale = dpi is >= 48 and <= 768 ? dpi / 96d : 1d;
            PreviewNativeMethods.Position(RequireOwnedSurface(identity), -32000, -32000, (int)Math.Ceiling(_width * scale), (int)Math.Ceiling(_height * scale));
            RequireOwnedSurface(identity);
        }
        _bridgeHandle = 0;
        _bridgeToken = null;
        _nativeNavigationToken = null;
        _attachment = null;
        SetNavigation(PreviewSurfaceNavigation.None);
    }

    private void ClearNativeSurface()
    {
        try
        {
            if (_bridgeHandle != 0) DetachNativeSurface();
            else RemoveNativeInput();
        }
        finally
        {
            _surfaceIdentity = null;
            _bridgeHandle = 0;
            _bridgeToken = null;
            _nativeNavigationToken = null;
            _attachment = null;
        }
    }

    private sealed class NativeKeyboardSite(IKeyboardInputSink sink, PreviewEngine owner) : IKeyboardInputSite
    {
        public IKeyboardInputSink Sink => sink;
        public bool OnNoMoreTabStops(TraversalRequest request)
        {
            // WPF invokes the site for directional navigation too. Arrow keys
            // retain the target application's behavior; only Tab traversal exits.
            if (owner._bridgeHandle == 0 || request.FocusNavigationDirection is not
                (FocusNavigationDirection.Next or FocusNavigationDirection.Previous)) return false;
            return owner.NavigateNativeBoundary(request.FocusNavigationDirection);
        }
        public void Unregister() { }
    }
}
