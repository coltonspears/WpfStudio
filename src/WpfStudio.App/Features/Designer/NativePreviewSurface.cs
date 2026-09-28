using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.App.Features.Designer;

/// <summary>
/// View-only HWND bridge. The IDE owns this window; all foreign-window operations
/// go through the preview session and execute in the process that owns the view.
/// </summary>
public sealed partial class NativePreviewSurface : HwndHost
{
    private string _bridgeToken = Guid.NewGuid().ToString("N");
    private IPreviewInteractionSession? _session, _leasedSession;
    private IPreviewInteractionSession? _detachingSession;
    private Task? _deactivation;
    private bool _active, _pending, _updating, _destroyed, _dpiCompatible, _releasing;
    private long _bridgeGeneration, _stateGeneration, _navigationSequence;
    private double _offsetX, _offsetY;
    private FocusEntry? _focus;
    private DispatcherTimer? _focusTimer;
    private nint _bridge;

    public event EventHandler? StateChanged;
    public event EventHandler? ViewportChanged;
    public event EventHandler? InspectRequested;
    public string Status { get; private set; } = "";
    public bool IsAttached { get; private set; }
    public nint BridgeHandle => _bridge;
    public string BridgeToken => _bridgeToken;
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }

    public NativePreviewSurface()
    {
        Focusable = true;
        Visibility = Visibility.Hidden;
        Loaded += (_, _) => QueueUpdate();
        Unloaded += (_, _) => ReleaseForRemoval();
        IsVisibleChanged += (_, _) => { if (!IsVisible) RevokeFocus(); QueueUpdate(); };
    }

    public void SetSession(IPreviewInteractionSession? session)
    {
        if (ReferenceEquals(_session, session)) return;
        RevokeFocus();
        ResetAttachment();
        _stateGeneration++;
        if (_session is not null) _session.Changed -= SessionChanged;
        _session = session;
        _navigationSequence = 0;
        if (_session is not null) _session.Changed += SessionChanged;
        SetState(false, _bridge != 0 && !_dpiCompatible ? DpiStatus : "");
        UpdateVisibility();
        QueueUpdate();
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        RevokeFocus();
        _stateGeneration++;
        _active = active;
        UpdateVisibility();
        QueueUpdate();
    }

    public void SetScrollOffsets(double x, double y)
    {
        x = FiniteOffset(x); y = FiniteOffset(y);
        if (_offsetX == x && _offsetY == y) return;
        _offsetX = x; _offsetY = y;
        QueueUpdate();
    }

    private void UpdateVisibility() => Visibility = _active && _session?.IsAvailable == true
        ? Visibility.Visible : Visibility.Hidden;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        RevokeFocus();
        ResetAttachment();
        _destroyed = false;
        _bridgeGeneration++;
        _bridgeToken = Guid.NewGuid().ToString("N");
        if (_session is not null) { _session.Changed -= SessionChanged; _session.Changed += SessionChanged; }
        nint parentContext = Native.GetWindowDpiAwarenessContext(hwndParent.Handle);
        _dpiCompatible = Native.AreDpiAwarenessContextsEqual(parentContext, Native.PerMonitorV2)
            && Native.AreDpiAwarenessContextsEqual(parentContext, Native.GetThreadDpiAwarenessContext());
        _bridge = Native.CreateWindowEx(0, "STATIC", "WpfStudio preview bridge",
            Native.Child | Native.ClipChildren | Native.ClipSiblings,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        if (_bridge == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the preview container.");
        if (!Native.SetProp(_bridge, LeaseProperty, new nint(1)))
        {
            int error = Marshal.GetLastWin32Error();
            Native.DestroyWindow(_bridge); _bridge = 0;
            throw new Win32Exception(error, "Could not identify the preview container.");
        }
        if (!_dpiCompatible) SetState(false, DpiStatus);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(QueueUpdate));
        return new HandleRef(this, _bridge);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        RevokeFocus();
        ResetAttachment();
        _destroyed = true;
        _bridgeGeneration++;
        _pending = false;
        if (_session is not null) _session.Changed -= SessionChanged;
        if (!ReleaseForRemoval()) return;
        if (Native.IsWindow(hwnd.Handle))
        {
            Native.RemoveProp(hwnd.Handle, LeaseProperty);
            Native.DestroyWindow(hwnd.Handle);
        }
        _bridge = 0;
        SetState(false, "");
    }

    internal bool ReleaseForRemoval(bool hide = true)
    {
        if (_releasing) return false;
        _releasing = true;
        RevokeFocus();
        ResetAttachment();
        _stateGeneration++;
        _active = false; _pending = false;
        if (hide) Visibility = Visibility.Hidden;
        // The session may already be reused by another bridge while this view's
        // dispatcher continuation is still queued after a completed detach.
        if (_deactivation?.IsCompletedSuccessfully == true && ReferenceEquals(_detachingSession, _leasedSession))
            _leasedSession = null;
        // Abort bypasses ordinary RPC gates and confirms that no remote child can
        // remain attached. If that cannot be proven, do not destroy its parent.
        try
        {
            _leasedSession?.Abort("The native preview container was removed. Refresh the preview after docking to interact again.");
            _leasedSession = null;
            SetState(false, "");
            return true;
        }
        catch (Exception exception)
        {
            SetState(false, "The preview container is retained until its process exits: " + exception.Message);
            return false;
        }
        finally { _releasing = false; }
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ReadViewport();
        QueueUpdate();
    }

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (NavigationMessage != 0 && message == NavigationMessage && hwnd == _bridge)
        {
            handled = true;
            return ReceiveNativeNavigation(wParam, lParam);
        }
        // Parent destruction can bypass DestroyWindowCore: HwndHost clears its
        // cached handle on WM_NCDESTROY. Revoke before native child teardown too.
        if (message == 0x0002 && hwnd == _bridge) ReleaseForRemoval(hide: false);
        if (message == 0x0082 && hwnd == _bridge)
        {
            RevokeFocus();
            ResetAttachment();
            _destroyed = true; _bridgeGeneration++; _pending = false; _bridge = 0;
            if (_session is not null) _session.Changed -= SessionChanged;
            SetState(false, "");
        }
        // DPI may change without a DIP-size change when moving a floating pane.
        if (message is 0x02E0 or 0x02E3) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_destroyed || hwnd != _bridge) return;
            ReadViewport(); QueueUpdate();
        }));
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        if (_inNativeNavigation || !WantsAttachment || request.FocusNavigationDirection is not (FocusNavigationDirection.Next
            or FocusNavigationDirection.Previous or FocusNavigationDirection.First or FocusNavigationDirection.Last)) return false;
        RevokeFocus();
        var action = request.FocusNavigationDirection is FocusNavigationDirection.Last or FocusNavigationDirection.Previous
            ? PreviewSurfaceAction.FocusLast : PreviewSurfaceAction.FocusFirst;
        var entry = new FocusEntry(_session!, action, _bridge, _bridgeGeneration, _stateGeneration);
        if (!Native.SetProp(entry.Bridge, entry.Property, new nint(1))) return false;
        _focus = entry;
        InputManager.Current.PreProcessInput += PendingFocusInput;
        _focusTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input,
            (_, _) => { if (_focus is { } current && !FocusIsCurrent(current)) RevokeFocus(current); }, Dispatcher);
        _focusTimer.Start();
        // Complete WPF's current navigation before delivering a possible
        // no-tab-stop continuation, including synchronously completed sessions.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(QueueUpdate));
        return true;
    }

    private void PendingFocusInput(object sender, PreProcessInputEventArgs args)
    {
        // The initiating Tab has already passed PreProcessInput when WPF calls
        // TabInto. Any subsequent local input or focus transition supersedes it.
        var input = args.StagingItem.Input;
        if (input is KeyboardFocusChangedEventArgs or TextCompositionEventArgs or MouseWheelEventArgs
            || input.RoutedEvent == Keyboard.PreviewKeyDownEvent || input.RoutedEvent == Keyboard.KeyDownEvent
            || input.RoutedEvent == Mouse.PreviewMouseDownEvent || input.RoutedEvent == Mouse.MouseDownEvent
            || input.RoutedEvent == Stylus.PreviewStylusDownEvent || input.RoutedEvent == Stylus.StylusDownEvent)
            RevokeFocus();
    }

    private bool FocusIsCurrent(FocusEntry entry)
    {
        if (!ReferenceEquals(_focus, entry) || !ReferenceEquals(_session, entry.Session) || !WantsAttachment
            || entry.Bridge != _bridge || entry.BridgeGeneration != _bridgeGeneration || entry.StateGeneration != _stateGeneration
            || Environment.TickCount64 - entry.StartedAt >= 2000 || Native.GetFocus() != entry.NativeFocus
            || Native.GetProp(entry.Bridge, entry.Property) != new nint(1)) return false;
        if (entry.ManagedFocus is null) return Keyboard.FocusedElement is null;
        return entry.ManagedFocus.TryGetTarget(out var previous) && ReferenceEquals(previous, Keyboard.FocusedElement);
    }

    private void RevokeFocus(FocusEntry? entry = null)
    {
        entry ??= _focus;
        if (entry is null) return;
        // Remove properties only from the locally owned bridge in its original
        // lifetime. A recycled HWND is not authority to mutate a new window.
        if (entry.Bridge == _bridge && entry.BridgeGeneration == _bridgeGeneration && Native.IsWindow(entry.Bridge))
            Native.RemoveProp(entry.Bridge, entry.Property);
        if (!ReferenceEquals(_focus, entry)) return;
        _focus = null;
        InputManager.Current.PreProcessInput -= PendingFocusInput;
        _focusTimer?.Stop();
    }

    private sealed class FocusEntry(IPreviewInteractionSession session, PreviewSurfaceAction action, nint bridge,
        long bridgeGeneration, long stateGeneration)
    {
        public IPreviewInteractionSession Session { get; } = session;
        public PreviewSurfaceAction Action { get; } = action;
        public nint Bridge { get; } = bridge;
        public long BridgeGeneration { get; } = bridgeGeneration;
        public long StateGeneration { get; } = stateGeneration;
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public string Property => "WpfStudio.PreviewFocus." + Token;
        public long StartedAt { get; } = Environment.TickCount64;
        public nint NativeFocus { get; } = Native.GetFocus();
        public WeakReference<IInputElement>? ManagedFocus { get; } = Keyboard.FocusedElement is { } focused ? new(focused) : null;
    }

    private string LeaseProperty => "WpfStudio.PreviewBridge." + _bridgeToken;
    private const string DpiStatus = "Interactive preview requires matching Per-Monitor V2 DPI awareness. Restart the IDE to retry.";
    private bool WantsAttachment => !_destroyed && _active && IsLoaded && IsVisible && _dpiCompatible
        && _bridge != 0 && _session?.IsAvailable == true;

    private void QueueUpdate()
    {
        if (_destroyed || _releasing) return;
        _pending = true;
        if (_inNativeNavigation) return;
        if (!_updating) _ = UpdateAsync();
    }

    private async Task UpdateAsync()
    {
        _updating = true;
        IPreviewInteractionSession? operationSession = null;
        long operationBridge = _bridgeGeneration, operationState = _stateGeneration;
        try
        {
            while (_pending && !_destroyed)
            {
                _pending = false;
                var session = _session;
                long generation = _bridgeGeneration;
                long stateGeneration = _stateGeneration;
                operationBridge = generation; operationState = stateGeneration;
                operationSession = session;
                // An attach that completed under an older activation generation
                // still owns a physical lease, even though its reply was ignored.
                // Release that lease before creating another attachment token.
                if (_leasedSession is { } previous && (!WantsAttachment || !ReferenceEquals(previous, session) || _attaching || !IsAttached))
                {
                    operationSession = previous;
                    _detachingSession = previous;
                    _deactivation = previous.DeactivateAsync();
                    await _deactivation;
                    if (ReferenceEquals(_leasedSession, previous)) _leasedSession = null;
                    ResetAttachment();
                    SetState(false, "");
                    if (_destroyed || generation != _bridgeGeneration) break;
                    if (stateGeneration != _stateGeneration || !ReferenceEquals(session, _session)) { _pending = true; continue; }
                }
                if (!WantsAttachment || session is null || !ReferenceEquals(_session, session)) continue;
                if (!ReadViewport(out int width, out int height, out double scale)) continue;
                if (_focus is { } pendingFocus && !FocusIsCurrent(pendingFocus)) RevokeFocus(pendingFocus);
                bool attached = ReferenceEquals(_leasedSession, session) && IsAttached && session.IsAttached;
                var focus = attached ? _focus : null;
                var action = attached ? focus?.Action ?? PreviewSurfaceAction.Update : PreviewSurfaceAction.Attach;
                if (!attached) BeginAttachment();
                var request = new PreviewSurfaceRequest(session.Surface, _bridgeToken, _bridge.ToInt64(),
                    Environment.ProcessId, width, height, PixelOffset(_offsetX, scale), PixelOffset(_offsetY, scale),
                    0, action, focus?.Token, _navigationToken);
                // Publish the lease before awaiting attach, including a not-yet-acknowledged child.
                _deactivation = null; _detachingSession = null;
                _leasedSession = session;
                operationSession = session;
                var response = await session.UpdateAsync(request);
                if (_destroyed || generation != _bridgeGeneration) break;
                if (ReferenceEquals(session, _session) && stateGeneration == _stateGeneration && WantsAttachment)
                {
                    if (response.Success && action == PreviewSurfaceAction.Attach) AcceptAttachment(response, session);
                    SetState(response.Success, response.Success ? "" : response.Status ?? "Interactive preview is unavailable.");
                    if (response.Success)
                    {
                        ReceiveFocusObservation(response.Focus);
                        var pendingObservation = _pendingFocusObservation;
                        _pendingFocusObservation = null;
                        ReceiveFocusObservation(pendingObservation);
                    }
                    if (focus is not null)
                    {
                        bool continueNavigation = response.Success && response.Focused == false && FocusIsCurrent(focus);
                        RevokeFocus(focus);
                        if (continueNavigation)
                            ((IKeyboardInputSink)this).KeyboardInputSite?.OnNoMoreTabStops(new TraversalRequest(
                                action == PreviewSurfaceAction.FocusLast ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                    }
                    if (!response.Success) { RevokeFocus(); ResetAttachment(); }
                    if (response.Success && _focus is not null) _pending = true;
                }
                else _pending = true;
            }
        }
        catch (Exception exception)
        {
            string? cleanupFailure = null;
            // Clean up only the lease used by this failed operation. A late
            // exception from a removed bridge is not authority over a new one.
            if (operationBridge == _bridgeGeneration && operationSession is not null && ReferenceEquals(operationSession, _leasedSession))
            {
                try
                {
                    if (_deactivation?.IsCompletedSuccessfully != true || !ReferenceEquals(_detachingSession, operationSession))
                        operationSession.Abort("Interactive preview communication failed.");
                    _leasedSession = null;
                }
                catch (Exception cleanup) { cleanupFailure = cleanup.Message; }
            }
            bool current = !_destroyed && operationBridge == _bridgeGeneration && operationState == _stateGeneration
                && ReferenceEquals(operationSession, _session);
            if (current || cleanupFailure is not null)
            {
                RevokeFocus();
                ResetAttachment();
                SetState(false, "Interactive preview stopped: " + exception.Message + (cleanupFailure is null ? "" : " " + cleanupFailure));
            }
            // A newly assigned session or bridge may have queued its attach while
            // the old request was failing. Keep that work unless the old lease
            // still prevents safe reuse of this native parent.
            _pending = !current && cleanupFailure is null && !_destroyed;
        }
        finally
        {
            _updating = false;
            if (_pending && !_destroyed) QueueUpdate();
        }
    }

    private void SessionChanged(object? sender, PreviewInteractionEvent args)
    {
        if (Dispatcher.HasShutdownStarted) return;
        long generation = _stateGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_destroyed || !ReferenceEquals(sender, _session)) return;
            if (!args.Available)
            {
                RevokeFocus();
                ResetAttachment();
                SetState(false, args.Status ?? "Interactive preview is unavailable.");
                UpdateVisibility(); QueueUpdate();
                return;
            }
            if (!_active || generation != _stateGeneration) return;
            ReceiveFocusObservation(args.Focus);
            if (args.NavigationSequence <= _navigationSequence) return;
            _navigationSequence = args.NavigationSequence;
            if (args.Navigation == PreviewSurfaceNavigation.Inspect) InspectRequested?.Invoke(this, EventArgs.Empty);
        }));
    }

    private void SetState(bool attached, string status)
    {
        if (IsAttached == attached && Status == status) return;
        IsAttached = attached; Status = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReadViewport() => ReadViewport(out _, out _, out _);
    private bool ReadViewport(out int width, out int height, out double scale)
    {
        width = height = 0; scale = 1;
        if (_bridge == 0 || !Native.GetClientRect(_bridge, out var rect)) return false;
        width = Math.Clamp(rect.Right - rect.Left, 0, 32768);
        height = Math.Clamp(rect.Bottom - rect.Top, 0, 32768);
        uint dpi = Native.GetDpiForWindow(_bridge);
        if (dpi == 0) return false;
        scale = dpi / 96d;
        double logicalWidth = width / scale, logicalHeight = height / scale;
        if (ViewportWidth != logicalWidth || ViewportHeight != logicalHeight)
        {
            ViewportWidth = logicalWidth; ViewportHeight = logicalHeight;
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }
        return width > 0 && height > 0;
    }

    private static double FiniteOffset(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 32768) : 0;
    private static int PixelOffset(double value, double scale) => (int)Math.Clamp(Math.Round(value * scale), 0, 32768);

    private static class Native
    {
        public const int Child = 0x40000000, ClipChildren = 0x02000000, ClipSiblings = 0x04000000;
        public static readonly nint PerMonitorV2 = new(-4);
        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateWindowEx(int exStyle, string className, string name, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32", SetLastError = true)] public static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32")] public static extern bool IsWindow(nint hwnd);
        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetProp(nint hwnd, string name, nint value);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint RemoveProp(nint hwnd, string name);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint GetProp(nint hwnd, string name);
        [DllImport("user32")] public static extern nint GetFocus();
        [DllImport("user32")] public static extern nint GetWindowDpiAwarenessContext(nint hwnd);
        [DllImport("user32")] public static extern nint GetThreadDpiAwarenessContext();
        [DllImport("user32")] public static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
        [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32")] public static extern bool GetClientRect(nint hwnd, out Rect rect);
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    }
}
