using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.App.Features.Designer;

public sealed partial class NativePreviewSurface
{
    private static readonly int NavigationMessage = unchecked((int)NavigationNative.RegisterWindowMessage(PreviewNativeNavigation.MessageName));
    private string? _navigationToken;
    private nint _nativeHandle;
    private long _attachmentSequence, _boundarySequence, _focusObservationSequence;
    private bool _attaching, _inNativeNavigation;
    private PreviewSurfaceFocus? _pendingFocusObservation;

    /// <summary>Verified root-DIP geometry for view-only viewport scrolling.</summary>
    public event EventHandler<PreviewSurfaceFocus>? FocusObserved;

    private void ResetAttachment()
    {
        _navigationToken = null;
        _nativeHandle = 0;
        _attachmentSequence = _boundarySequence = _focusObservationSequence = 0;
        _attaching = false;
        _pendingFocusObservation = null;
    }

    private void BeginAttachment()
    {
        ResetAttachment();
        _navigationToken = Guid.NewGuid().ToString("N");
        _attaching = true;
    }

    private void AcceptAttachment(PreviewSurfaceResponse response, IPreviewInteractionSession session)
    {
        var handle = new nint(response.NativeHandle);
        if (session.NativeProcessId != 0 && (!OwnedNativeWindow(handle, session.NativeProcessId)
            || NavigationNative.GetParent(handle) != _bridge))
            throw new InvalidOperationException("The preview's native window does not belong to the attached process and container.");
        _nativeHandle = handle;
        _attachmentSequence = response.Request.Sequence;
        _attaching = false;
    }

    private nint ReceiveNativeNavigation(nint sender, nint encoded)
    {
        long value = encoded.ToInt64(), sequence = value / 4, direction = value & 3;
        var session = _session;
        long bridgeGeneration = _bridgeGeneration, stateGeneration = _stateGeneration;
        if (NavigationMessage == 0 || _inNativeNavigation || value <= 0 || sequence <= _boundarySequence
            || direction is not (1 or 2) || !WantsAttachment || !IsAttached || _attaching
            || session is null || !ReferenceEquals(session, _leasedSession) || session.NativeProcessId <= 0
            || _nativeHandle == 0 || sender != _nativeHandle || _navigationToken is null
            || !OwnedNativeWindow(sender, session.NativeProcessId) || NavigationNative.GetParent(sender) != _bridge
            || Native.GetProp(sender, PreviewNativeNavigation.PropertyPrefix + _navigationToken) != encoded
            || !((IKeyboardInputSink)this).HasFocusWithin()) return PreviewNativeNavigation.Rejected;

        // Consume the sequence before invoking framework traversal: focus events
        // can reenter WndProc. Never await, pump, or make RPC calls in this scope.
        _boundarySequence = sequence;
        _inNativeNavigation = true;
        RevokeFocus();
        try
        {
            ((IKeyboardInputSink)this).KeyboardInputSite?.OnNoMoreTabStops(new TraversalRequest(
                direction == 2 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            nint focus = Native.GetFocus();
            return bridgeGeneration == _bridgeGeneration && stateGeneration == _stateGeneration
                && ReferenceEquals(session, _session) && WantsAttachment && IsAttached && focus != 0
                && focus != _bridge && !NavigationNative.IsChild(_bridge, focus)
                ? PreviewNativeNavigation.Moved : PreviewNativeNavigation.Rejected;
        }
        catch (Exception)
        {
            // Native callbacks must never unwind through user32. A callback
            // may have moved focus before throwing; do not claim a known denial.
            return PreviewNativeNavigation.Unknown;
        }
        finally
        {
            _inNativeNavigation = false;
            if (_pending && !_destroyed) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(QueueUpdate));
        }
    }

    private void ReceiveFocusObservation(PreviewSurfaceFocus? observation)
    {
        if (observation is null || !WantsAttachment || observation.BridgeToken != _bridgeToken
            || observation.Sequence <= 0 || observation.AttachmentSequence <= 0) return;
        if (_attaching)
        {
            if (_pendingFocusObservation is null || observation.AttachmentSequence > _pendingFocusObservation.AttachmentSequence
                || observation.AttachmentSequence == _pendingFocusObservation.AttachmentSequence && observation.Sequence > _pendingFocusObservation.Sequence)
                _pendingFocusObservation = observation;
            return;
        }
        if (!IsAttached || observation.AttachmentSequence != _attachmentSequence
            || observation.Sequence <= _focusObservationSequence) return;
        _focusObservationSequence = observation.Sequence;
        if (observation.Bounds is not { } bounds || observation.FocusEpoch <= 0 || observation.FocusChangedAtTick < 0
            || observation.FocusChangedAtTick > Environment.TickCount64 || observation.Dpi != Native.GetDpiForWindow(_bridge)
            || observation.NativeFocusHandle == 0 || new nint(observation.NativeFocusHandle) != Native.GetFocus()
            || _session is null || !OwnedNativeWindow(new nint(observation.NativeFocusHandle), _session.NativeProcessId)
            || !((IKeyboardInputSink)this).HasFocusWithin()
            || !FiniteBounds(bounds)) return;
        FocusObserved?.Invoke(this, observation);
    }

    private static bool FiniteBounds(PreviewBounds bounds) => double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0
        && double.IsFinite(bounds.X + bounds.Width) && double.IsFinite(bounds.Y + bounds.Height);

    private static bool OwnedNativeWindow(nint handle, int processId)
    {
        if (processId <= 0 || handle == 0 || !Native.IsWindow(handle)) return false;
        NavigationNative.GetWindowThreadProcessId(handle, out uint owner);
        return owner == (uint)processId;
    }

    private static class NavigationNative
    {
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32")] public static extern nint GetParent(nint hwnd);
        [DllImport("user32")] public static extern bool IsChild(nint parent, nint child);
        [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    }
}
