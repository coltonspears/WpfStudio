using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

[assembly: InternalsVisibleTo("WpfStudio.NativeView.Tests")]

namespace WpfStudio.App.Features.Designer;

public enum PreviewLayoutGesturePhase { Begin, Update, Commit, Cancel }
public sealed record PreviewLayoutGesture(PreviewLayoutGesturePhase Phase, XamlLayoutHandle Handle,
    double DeltaX = 0, double DeltaY = 0, bool BypassSnap = false, double Zoom = 1);

public sealed partial class PreviewSurface
{
    public static readonly DependencyProperty LayoutEditingProperty = DependencyProperty.Register(nameof(LayoutEditing), typeof(PreviewLayoutEditContext), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, GestureContextChanged));
    public static readonly DependencyProperty LayoutDraftProperty = DependencyProperty.Register(nameof(LayoutDraft), typeof(XamlLayoutGestureResult), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LayoutGestureCommandProperty = DependencyProperty.Register(nameof(LayoutGestureCommand), typeof(ICommand), typeof(PreviewSurface), new PropertyMetadata(null, GestureContextChanged));
    public static readonly DependencyProperty IsLayoutEditingEnabledProperty = DependencyProperty.Register(nameof(IsLayoutEditingEnabled), typeof(bool), typeof(PreviewSurface), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, GestureContextChanged));
    public PreviewLayoutEditContext? LayoutEditing { get => (PreviewLayoutEditContext?)GetValue(LayoutEditingProperty); set => SetValue(LayoutEditingProperty, value); }
    public XamlLayoutGestureResult? LayoutDraft { get => (XamlLayoutGestureResult?)GetValue(LayoutDraftProperty); set => SetValue(LayoutDraftProperty, value); }
    public ICommand? LayoutGestureCommand { get => (ICommand?)GetValue(LayoutGestureCommandProperty); set => SetValue(LayoutGestureCommandProperty, value); }
    public bool IsLayoutEditingEnabled { get => (bool)GetValue(IsLayoutEditingEnabledProperty); set => SetValue(IsLayoutEditingEnabledProperty, value); }

    private const double HandleSize = 8;
    private ICommand? _gestureCommand;
    private XamlLayoutHandle _handle;
    private Point _pointerOrigin;
    private Vector _delta;
    private bool _pointerArmed, _gestureStarted, _keyboardGesture, _bypassSnap;

    public PreviewSurface()
    {
        Focusable = true;
        Unloaded += (_, _) => CancelLayoutGesture();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelLayoutGesture(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelLayoutGesture(); };
    }

    private static void GestureContextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((PreviewSurface)owner).CancelLayoutGesture();
    private bool CanEditLayout => IsEnabled && IsLayoutEditingEnabled && _bitmap is not null && Selection is not null
        && LayoutEditing is { Available: true, Bounds: { } bounds } && FiniteBounds(bounds);
    private static bool FiniteBounds(PreviewBounds bounds) => bounds.Width >= 0 && bounds.Height >= 0
        && new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height, bounds.X + bounds.Width, bounds.Y + bounds.Height }
            .All(value => double.IsFinite(value) && Math.Abs(value) <= 1_000_000);
    private Rect ScaledBounds(PreviewBounds bounds) => new(bounds.X * SafeScale, bounds.Y * SafeScale, bounds.Width * SafeScale, bounds.Height * SafeScale);
    private static Point HandlePoint(Rect rectangle, XamlLayoutHandle handle) => handle switch
    {
        XamlLayoutHandle.TopLeft => rectangle.TopLeft,
        XamlLayoutHandle.Top => new(rectangle.X + rectangle.Width / 2, rectangle.Top),
        XamlLayoutHandle.TopRight => rectangle.TopRight,
        XamlLayoutHandle.Right => new(rectangle.Right, rectangle.Y + rectangle.Height / 2),
        XamlLayoutHandle.BottomRight => rectangle.BottomRight,
        XamlLayoutHandle.Bottom => new(rectangle.X + rectangle.Width / 2, rectangle.Bottom),
        XamlLayoutHandle.BottomLeft => rectangle.BottomLeft,
        _ => new(rectangle.Left, rectangle.Y + rectangle.Height / 2)
    };
    private static Rect HandleRect(Point point, double padding = 0) => new(point.X - HandleSize / 2 - padding,
        point.Y - HandleSize / 2 - padding, HandleSize + padding * 2, HandleSize + padding * 2);
    private static readonly XamlLayoutHandle[] ResizeHandles = [XamlLayoutHandle.TopLeft, XamlLayoutHandle.TopRight,
        XamlLayoutHandle.BottomRight, XamlLayoutHandle.BottomLeft, XamlLayoutHandle.Top, XamlLayoutHandle.Right,
        XamlLayoutHandle.Bottom, XamlLayoutHandle.Left];

    internal XamlLayoutHandle? LayoutHandleAt(Point point)
    {
        if (!CanEditLayout) return null;
        var rectangle = ScaledBounds(LayoutEditing!.Bounds!);
        var handles = ResizeHandles.Where(handle => HandleRect(HandlePoint(rectangle, handle), 2).Contains(point))
            .OrderBy(handle => (HandlePoint(rectangle, handle) - point).LengthSquared).ToArray();
        if (handles.Length != 0) return handles[0];
        return rectangle.Contains(point) ? XamlLayoutHandle.Move : null;
    }

    private void DrawLayoutEditing(DrawingContext drawing)
    {
        if (!CanEditLayout) return;
        var original = ScaledBounds(LayoutEditing!.Bounds!);
        var frame = original;
        if (LayoutDraft is { Success: true, Bounds: { } draft } && FiniteBounds(draft))
        {
            drawing.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1) { DashStyle = DashStyles.Dot }, original);
            frame = ScaledBounds(draft);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 56, 189, 248)), null, frame);
            foreach (var guide in LayoutDraft.Guides.Take(64))
            {
                if (new[] { guide.Position, guide.Start, guide.End }.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1_000_000)) continue;
                var first = guide.Vertical ? new Point(guide.Position * SafeScale, guide.Start * SafeScale) : new Point(guide.Start * SafeScale, guide.Position * SafeScale);
                var last = guide.Vertical ? new Point(guide.Position * SafeScale, guide.End * SafeScale) : new Point(guide.End * SafeScale, guide.Position * SafeScale);
                drawing.DrawLine(new Pen(Brushes.DeepPink, 1) { DashStyle = DashStyles.Dash }, first, last);
            }
        }
        var outline = new Pen(Brushes.DodgerBlue, 2);
        drawing.DrawRectangle(null, outline, frame);
        foreach (var handle in ResizeHandles) drawing.DrawRectangle(Brushes.White, outline, HandleRect(HandlePoint(frame, handle)));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        if (HandlePointerDown(args.GetPosition(this), Keyboard.Modifiers)) args.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (HandlePointerMove(args.GetPosition(this), Keyboard.Modifiers)) args.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonUp(args);
        if (HandlePointerUp(args.GetPosition(this), Keyboard.Modifiers)) args.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs args) { base.OnLostMouseCapture(args); if (_pointerArmed) CancelLayoutGesture(); }
    protected override void OnMouseLeave(MouseEventArgs args) { base.OnMouseLeave(args); if (!_pointerArmed) Cursor = null; }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args) { base.OnLostKeyboardFocus(args); CancelLayoutGesture(); }

    // View-only input adapters are shared by routed input and STA regressions.
    // They never mutate source or runtime controls; the command owns validation.
    internal bool HandlePointerDown(Point point, ModifierKeys modifiers)
    {
        if (!IsEnabled || _bitmap is null) return false;
        Focus();
        CancelLayoutGesture();
        if (LayoutHandleAt(point) is { } handle && CanSend(LayoutGestureCommand, new(PreviewLayoutGesturePhase.Begin, handle, Zoom: SafeScale)))
        {
            _gestureCommand = LayoutGestureCommand; _handle = handle; _pointerOrigin = point;
            _bypassSnap = modifiers.HasFlag(ModifierKeys.Alt);
            _pointerArmed = true;
            if (CaptureMouse()) return true;
            ClearGesture();
        }
        return Pick(point);
    }

    internal bool HandlePointerMove(Point point, ModifierKeys modifiers)
    {
        if (!_pointerArmed)
        {
            Cursor = LayoutHandleAt(point) switch
            {
                XamlLayoutHandle.Move => Cursors.SizeAll,
                XamlLayoutHandle.TopLeft or XamlLayoutHandle.BottomRight => Cursors.SizeNWSE,
                XamlLayoutHandle.TopRight or XamlLayoutHandle.BottomLeft => Cursors.SizeNESW,
                XamlLayoutHandle.Top or XamlLayoutHandle.Bottom => Cursors.SizeNS,
                XamlLayoutHandle.Left or XamlLayoutHandle.Right => Cursors.SizeWE,
                _ => null
            };
            return false;
        }
        var distance = point - _pointerOrigin;
        if (!_gestureStarted && Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
        if (!_gestureStarted && !BeginGesture()) { CancelLayoutGesture(); return true; }
        _delta = distance / SafeScale; _bypassSnap = modifiers.HasFlag(ModifierKeys.Alt);
        if (!Send(PreviewLayoutGesturePhase.Update)) CancelLayoutGesture();
        return true;
    }

    internal bool HandlePointerUp(Point point, ModifierKeys modifiers)
    {
        if (!_pointerArmed) return false;
        if (_gestureStarted)
        {
            _delta = (point - _pointerOrigin) / SafeScale;
            _bypassSnap = modifiers.HasFlag(ModifierKeys.Alt);
            FinishGesture(PreviewLayoutGesturePhase.Commit);
        }
        else
        {
            bool pick = _handle == XamlLayoutHandle.Move;
            ClearGesture();
            if (pick) Pick(point);
        }
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (HandleLayoutKey(args.Key == Key.System ? args.SystemKey : args.Key, Keyboard.Modifiers)) args.Handled = true;
    }

    internal bool HandleLayoutKey(Key key, ModifierKeys modifiers)
    {
        if (!IsKeyboardFocused || !IsEnabled) return false;
        if (key == Key.Escape && (_gestureStarted || _pointerArmed)) { CancelLayoutGesture(); return true; }
        if (key == Key.Enter && _keyboardGesture) { FinishGesture(PreviewLayoutGesturePhase.Commit); return true; }
        if (!CanEditLayout || _pointerArmed || key is not (Key.Left or Key.Right or Key.Up or Key.Down)
            || modifiers.HasFlag(ModifierKeys.Windows)) return false;
        var handle = modifiers.HasFlag(ModifierKeys.Control) ? XamlLayoutHandle.BottomRight : XamlLayoutHandle.Move;
        if (_keyboardGesture && _handle != handle) CancelLayoutGesture();
        if (!_gestureStarted)
        {
            _gestureCommand = LayoutGestureCommand; _handle = handle; _keyboardGesture = true; _bypassSnap = true;
            if (!BeginGesture()) { ClearGesture(); return false; }
        }
        double step = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        _delta += key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) };
        if (!Send(PreviewLayoutGesturePhase.Update)) CancelLayoutGesture();
        return true;
    }

    private bool Pick(Point point)
    {
        var request = new PreviewPoint(point.X / SafeScale, point.Y / SafeScale);
        if (PickCommand?.CanExecute(request) != true) return false;
        PickCommand.Execute(request); return true;
    }
    private PreviewLayoutGesture Packet(PreviewLayoutGesturePhase phase) => new(phase, _handle, _delta.X, _delta.Y, _bypassSnap, SafeScale);
    private static bool CanSend(ICommand? command, PreviewLayoutGesture packet) => command?.CanExecute(packet) == true;
    private bool Send(PreviewLayoutGesturePhase phase)
    {
        var packet = Packet(phase);
        if (!CanSend(_gestureCommand, packet)) return false;
        _gestureCommand!.Execute(packet); return true;
    }
    private bool BeginGesture()
    {
        _gestureStarted = true;
        return Send(PreviewLayoutGesturePhase.Begin) && _gestureStarted;
    }
    private void FinishGesture(PreviewLayoutGesturePhase phase)
    {
        var command = _gestureCommand; var packet = Packet(phase);
        ClearGesture();
        if (CanSend(command, packet)) command!.Execute(packet);
    }
    internal void CancelLayoutGesture()
    {
        if (_gestureStarted) FinishGesture(PreviewLayoutGesturePhase.Cancel);
        else ClearGesture();
    }
    private void ClearGesture()
    {
        _pointerArmed = _gestureStarted = _keyboardGesture = false;
        _delta = default; _bypassSnap = false; _gestureCommand = null; Cursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new PreviewSurfaceAutomationPeer(this);
    private sealed class PreviewSurfaceAutomationPeer(PreviewSurface owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PreviewSurface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetNameCore() => "Preview canvas";
        protected override string GetHelpTextCore() => "Enable Edit layout to move or resize the selected element. Arrow keys nudge, Shift uses ten DIPs, Control resizes, Enter reviews, Escape cancels. Keyboard nudges bypass snapping.";
    }
}
