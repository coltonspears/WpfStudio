using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Controls;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.App.Features.Designer;

/// <summary>
/// Reusable view-only native viewport. WPF scrollbars remain outside the HWND;
/// scrolling moves the remote child within its native clipping parent at 100%.
/// </summary>
public sealed class NativePreviewPane : Grid
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(nameof(Session), typeof(IPreviewInteractionSession), typeof(NativePreviewPane), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(NativePreviewPane), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty IsSuppressedProperty = DependencyProperty.Register(nameof(IsSuppressed), typeof(bool), typeof(NativePreviewPane), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ContentWidthProperty = DependencyProperty.Register(nameof(ContentWidth), typeof(double), typeof(NativePreviewPane), new PropertyMetadata(0d, Changed));
    public static readonly DependencyProperty ContentHeightProperty = DependencyProperty.Register(nameof(ContentHeight), typeof(double), typeof(NativePreviewPane), new PropertyMetadata(0d, Changed));
    public static readonly DependencyProperty ExitInteractionCommandProperty = DependencyProperty.Register(nameof(ExitInteractionCommand), typeof(ICommand), typeof(NativePreviewPane));
    private static readonly DependencyProperty DockSuppressedProperty = DependencyProperty.Register("DockSuppressed", typeof(bool), typeof(NativePreviewPane), new PropertyMetadata(false, Changed));
    private static readonly DependencyPropertyKey IsAttachedPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsAttached), typeof(bool), typeof(NativePreviewPane), new PropertyMetadata(false));
    public static readonly DependencyProperty IsAttachedProperty = IsAttachedPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey StatusPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Status), typeof(string), typeof(NativePreviewPane), new PropertyMetadata(""));
    public static readonly DependencyProperty StatusProperty = StatusPropertyKey.DependencyProperty;

    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, SmallChange = 24, Minimum = 0 };
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical, SmallChange = 24, Minimum = 0 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4), Visibility = Visibility.Collapsed };
    private Window? _window;
    private bool _deactivated;
    private bool _applyingFocusScroll, _updatingScrollbars;
    private long _manualScrollAt = -1, _manualFocusEpoch, _observedFocusEpoch, _focusAttachment;
    private string? _focusBridge;
    private IPreviewInteractionSession? _focusSession;
    public NativePreviewSurface Surface { get; } = new();
    public IPreviewInteractionSession? Session { get => (IPreviewInteractionSession?)GetValue(SessionProperty); set => SetValue(SessionProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool IsSuppressed { get => (bool)GetValue(IsSuppressedProperty); set => SetValue(IsSuppressedProperty, value); }
    public double ContentWidth { get => (double)GetValue(ContentWidthProperty); set => SetValue(ContentWidthProperty, value); }
    public double ContentHeight { get => (double)GetValue(ContentHeightProperty); set => SetValue(ContentHeightProperty, value); }
    public ICommand? ExitInteractionCommand { get => (ICommand?)GetValue(ExitInteractionCommandProperty); set => SetValue(ExitInteractionCommandProperty, value); }
    public bool IsAttached => (bool)GetValue(IsAttachedProperty);
    public string Status => (string)GetValue(StatusProperty);

    public NativePreviewPane()
    {
        // Treat this composite viewport as one ordered group in its parent's
        // traversal. The pane's TabIndex then applies to its native surface too.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Local);
        AutomationProperties.SetName(this, "Interactive preview");
        AutomationProperties.SetName(Surface, "Native preview viewport");
        AutomationProperties.SetName(_horizontal, "Preview horizontal scroll");
        AutomationProperties.SetName(_vertical, "Preview vertical scroll");
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_vertical, 1); SetRow(_horizontal, 1); SetRow(_status, 2); SetColumnSpan(_status, 2);
        Children.Add(Surface); Children.Add(_vertical); Children.Add(_horizontal); Children.Add(_status);
        _horizontal.ValueChanged += ScrollValueChanged;
        _vertical.ValueChanged += ScrollValueChanged;
        Surface.ViewportChanged += (_, _) => UpdateScrollbars();
        Surface.FocusObserved += FocusObserved;
        Surface.StateChanged += (_, _) => UpdateStatus();
        Surface.InspectRequested += (_, _) =>
        {
            if (ExitInteractionCommand?.CanExecute(null) == true) ExitInteractionCommand.Execute(null);
        };
        Loaded += (_, _) =>
        {
            AttachWindow(); BindDockingState(); Update();
        };
        Unloaded += (_, _) =>
        {
            DetachWindow(); BindingOperations.ClearBinding(this, DockSuppressedProperty); Surface.SetActive(false);
        };
        IsVisibleChanged += (_, _) => Update();
    }

    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((NativePreviewPane)owner).Update();
    private void Update()
    {
        if (!ReferenceEquals(_focusSession, Session))
        {
            _focusSession = Session;
            _focusBridge = null;
            _focusAttachment = _observedFocusEpoch = _manualFocusEpoch = 0;
            _manualScrollAt = -1;
        }
        Surface.SetSession(Session);
        Surface.SetActive(IsActive && !IsSuppressed && !(bool)GetValue(DockSuppressedProperty) && !_deactivated && IsLoaded && IsVisible);
        UpdateScrollbars();
        UpdateStatus();
    }

    private void UpdateScrollbars()
    {
        _updatingScrollbars = true;
        try
        {
            double width = SafeSize(ContentWidth), height = SafeSize(ContentHeight);
            _horizontal.ViewportSize = Surface.ViewportWidth;
            _horizontal.LargeChange = Math.Max(1, Surface.ViewportWidth * .9);
            _horizontal.Maximum = Math.Max(0, width - Surface.ViewportWidth);
            _horizontal.IsEnabled = _horizontal.Maximum > 0;
            _vertical.ViewportSize = Surface.ViewportHeight;
            _vertical.LargeChange = Math.Max(1, Surface.ViewportHeight * .9);
            _vertical.Maximum = Math.Max(0, height - Surface.ViewportHeight);
            _vertical.IsEnabled = _vertical.Maximum > 0;
        }
        finally { _updatingScrollbars = false; }
        // Range coercion also clamps offsets after resizing or changing the view.
        UpdateOffsets();
    }

    private void UpdateOffsets() => Surface.SetScrollOffsets(_horizontal.Value, _vertical.Value);

    private void ScrollValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (_applyingFocusScroll || _updatingScrollbars) return;
        _manualScrollAt = Environment.TickCount64;
        _manualFocusEpoch = _observedFocusEpoch;
        UpdateOffsets();
    }

    private void FocusObserved(object? sender, PreviewSurfaceFocus observation)
    {
        if (observation.Bounds is not { } bounds) return;
        if (_focusBridge != observation.BridgeToken || _focusAttachment != observation.AttachmentSequence)
        {
            _focusBridge = observation.BridgeToken;
            _focusAttachment = observation.AttachmentSequence;
            _observedFocusEpoch = _manualFocusEpoch = 0;
        }
        if (observation.FocusEpoch < _observedFocusEpoch) return;
        _observedFocusEpoch = observation.FocusEpoch;
        if (observation.FocusEpoch <= _manualFocusEpoch || observation.FocusChangedAtTick <= _manualScrollAt) return;
        double x = FocusOffset(_horizontal.Value, Surface.ViewportWidth, _horizontal.Maximum, bounds.X, bounds.Width);
        double y = FocusOffset(_vertical.Value, Surface.ViewportHeight, _vertical.Maximum, bounds.Y, bounds.Height);
        _applyingFocusScroll = true;
        try { _horizontal.Value = x; _vertical.Value = y; }
        finally { _applyingFocusScroll = false; }
        UpdateOffsets();
    }

    private static double FocusOffset(double offset, double viewport, double maximum, double start, double size)
    {
        double end = start + size;
        // A larger target cannot fit. Keep an already visible portion stable;
        // otherwise reveal the nearest edge, with the ordinary scroll limits.
        if (size > viewport)
        {
            if (start < offset + viewport && end > offset) return offset;
            return Math.Clamp(start >= offset + viewport ? start : end - viewport, 0, maximum);
        }
        if (start < offset) offset = start;
        else if (end > offset + viewport) offset = end - viewport;
        return Math.Clamp(offset, 0, maximum);
    }
    private void UpdateStatus()
    {
        SetValue(IsAttachedPropertyKey, Surface.IsAttached);
        string status = IsActive && (IsSuppressed || (bool)GetValue(DockSuppressedProperty)) ? "Interaction is hidden while an IDE overlay or auto-hide pane is open."
            : IsActive && _deactivated ? "Interaction resumes when this IDE window is active."
            : Surface.Status;
        SetValue(StatusPropertyKey, status);
        _status.Text = status;
        _status.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AttachWindow()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, _window)) return;
        DetachWindow();
        _window = window;
        if (_window is null) return;
        // Do not require activation for offscreen/test windows; only suppress a
        // window after it actually loses activation, closing remote popup state.
        _window.Deactivated += Deactivated;
        _window.Activated += Activated;
        _window.Closing += Closing;
    }

    private void BindDockingState()
    {
        // Resolve optional docking ancestors once loaded, so an ordinary host
        // window does not emit failed RelativeSource bindings.
        var binding = new MultiBinding { Converter = new NativePreviewSuppressionConverter() };
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not LayoutAnchorableControl { Model: { } model }) continue;
            binding.Bindings.Add(new Binding("IsAutoHidden") { Source = model });
            binding.Bindings.Add(new Binding("Root.Manager.AutoHideWindow.IsVisible") { Source = model, FallbackValue = false, TargetNullValue = false });
            break;
        }
        if (_window is LayoutFloatingWindowControl floating)
            binding.Bindings.Add(new Binding("IsDragging") { Source = floating });
        if (binding.Bindings.Count > 0) BindingOperations.SetBinding(this, DockSuppressedProperty, binding);
        else ClearValue(DockSuppressedProperty);
    }

    private void DetachWindow()
    {
        if (_window is not null)
        {
            _window.Deactivated -= Deactivated;
            _window.Activated -= Activated;
            _window.Closing -= Closing;
        }
        _window = null; _deactivated = false;
    }

    private void Activated(object? sender, EventArgs args) { _deactivated = false; Update(); }
    private void Deactivated(object? sender, EventArgs args) { _deactivated = true; Update(); }
    private void Closing(object? sender, CancelEventArgs args)
    {
        // Do not dispose the control: another Closing handler may cancel, and
        // a fresh preview must remain possible in that still-open window.
        if (!args.Cancel && !Surface.ReleaseForRemoval()) args.Cancel = true;
    }
    private static double SafeSize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 32768) : 0;
}

/// <summary>Combines view overlay visibility without coupling the native view to a shell model.</summary>
public sealed class NativePreviewSuppressionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Any(value => value is true);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
