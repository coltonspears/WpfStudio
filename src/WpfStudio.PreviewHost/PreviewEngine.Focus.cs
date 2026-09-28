using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

public sealed partial class PreviewEngine
{
    private DispatcherTimer? _focusObserver;
    private WeakReference<IInputElement>? _observedFocus;
    private bool _hadObservedFocus;
    private bool _focusCaptureQueued;
    private long _focusObservationSequence, _focusEpoch, _focusChangedAtTick;
    private PreviewSurfaceFocus? _focusObservation;

    private void StartFocusObservation()
    {
        StopFocusObservation();
        InputManager.Current.PostProcessInput += OnNativeFocusInput;
        if (_viewport is not null) _viewport.LayoutUpdated += OnNativeFocusLayout;
        // Sampling catches cached transform changes without invoking layout;
        // animated transforms themselves are withheld. This work is deliberately
        // separate from the heartbeat callback.
        _focusObserver = new DispatcherTimer(TimeSpan.FromMilliseconds(125), DispatcherPriority.Background,
            (_, _) => CaptureNativeFocus(), _dispatcher);
        _focusObserver.Start();
        CaptureNativeFocus();
    }

    private void StopFocusObservation()
    {
        _focusObserver?.Stop();
        _focusObserver = null;
        InputManager.Current.PostProcessInput -= OnNativeFocusInput;
        if (_viewport is not null) _viewport.LayoutUpdated -= OnNativeFocusLayout;
        _focusObservation = null;
        _focusCaptureQueued = false;
    }

    private void OnNativeFocusInput(object sender, ProcessInputEventArgs args)
    {
        if (args.StagingItem.Input is KeyboardFocusChangedEventArgs)
        {
            // Invalidate the old rectangle immediately, then capture after the
            // current focus event has finished routing and layout can settle.
            ObserveFocusIdentity();
            PublishNativeFocus(null, "Keyboard focus changed; its layout box is pending.");
            QueueNativeFocusCapture();
        }
    }

    private void OnNativeFocusLayout(object? sender, EventArgs args) => QueueNativeFocusCapture();

    private void QueueNativeFocusCapture()
    {
        if (_focusCaptureQueued || _attachment is not { } attachment || _bridgeHandle == 0) return;
        _focusCaptureQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_attachment != attachment) return;
            _focusCaptureQueued = false;
            CaptureNativeFocus();
        }));
    }

    private IInputElement? ObserveFocusIdentity()
    {
        var focused = Keyboard.FocusedElement;
        IInputElement? previous = null;
        _observedFocus?.TryGetTarget(out previous);
        if (!ReferenceEquals(focused, previous) || focused is null && _hadObservedFocus)
        {
            _focusEpoch++;
            _focusChangedAtTick = Environment.TickCount64;
            _hadObservedFocus = focused is not null;
            _observedFocus = focused is null ? null : new(focused);
        }
        return focused;
    }

    private void CaptureNativeFocus()
    {
        if (_attachment is not { } attachment || _bridgeHandle == 0 || !CurrentSurface(attachment.Surface)) return;
        var focused = ObserveFocusIdentity();
        if (focused is not UIElement element || _surface!.RootVisual is not { } root ||
            !ReferenceEquals(PresentationSource.FromVisual(element), _surface))
        {
            PublishNativeFocus(null, focused is null ? "No keyboard-focused element." :
                "The focused object has no layout box in this native presentation root.");
            return;
        }
        if (PreviewNativeMethods.GetFocus() != _surface.Handle)
        {
            PublishNativeFocus(null, "The native presentation root does not own keyboard focus.");
            return;
        }
        var bounds = NativeFocusGeometry.Capture(element, root, out string? status);
        if (!ReferenceEquals(focused, Keyboard.FocusedElement) || !CurrentSurface(attachment.Surface)) return;
        PublishNativeFocus(bounds, status);
    }

    private void PublishNativeFocus(PreviewBounds? bounds, string? status)
    {
        if (_attachment is not { } attachment || _bridgeHandle == 0 || !CurrentSurface(attachment.Surface)) return;
        long nativeFocus = PreviewNativeMethods.GetFocus().ToInt64();
        uint dpi = PreviewNativeMethods.GetDpiForWindow(_surface!.Handle);
        var sample = new PreviewSurfaceFocus(attachment.BridgeToken, attachment.Sequence, 0,
            _focusEpoch, _focusChangedAtTick, nativeFocus, dpi, bounds, status);
        if (_focusObservation is { } previous && (previous with { Sequence = 0 }) == sample) return;
        _focusObservation = sample with { Sequence = ++_focusObservationSequence };
    }
}
