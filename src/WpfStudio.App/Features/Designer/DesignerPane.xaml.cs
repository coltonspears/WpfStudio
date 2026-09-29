using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

/// <summary>View-only canvas behavior: viewport size for fit zoom, Ctrl+wheel zoom around the pointer,
/// middle-button panning, and the property grid's search filter.</summary>
public partial class DesignerPane : UserControl
{
    private DesignerViewModel? _model;
    private Point? _panOrigin;
    private Vector _panStart;

    public DesignerPane()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DesignerViewModel);
        Loaded += (_, _) => { Attach(DataContext as DesignerViewModel); PushViewport(); };
        Unloaded += (_, _) => Attach(null);
        CanvasScroller.SizeChanged += (_, _) => PushViewport();
        CanvasScroller.PreviewMouseWheel += CanvasWheel;
        CanvasScroller.PreviewMouseDown += PanStart;
        CanvasScroller.PreviewMouseMove += PanMove;
        CanvasScroller.PreviewMouseUp += PanEnd;
        CanvasScroller.LostMouseCapture += (_, _) => EndPan();
    }

    private void Attach(DesignerViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is not null) _model.PropertyFilterChanged -= ApplyPropertyFilter;
        _model = model;
        if (model is null) return;
        model.PropertyFilterChanged += ApplyPropertyFilter;
        // The grid binds to Properties itself; the appearance picker uses its own view.
        CollectionViewSource.GetDefaultView(model.Properties).Filter = item =>
            item is not PreviewProperty property || _model is not { } current || current.MatchesPropertyFilter(property);
        PushViewport();
    }

    private void ApplyPropertyFilter()
    {
        if (_model is { } model) CollectionViewSource.GetDefaultView(model.Properties).Refresh();
    }

    private void PushViewport()
    {
        if (_model is null || !CanvasScroller.IsLoaded) return;
        _model.CanvasViewportWidth = CanvasScroller.ActualWidth;
        _model.CanvasViewportHeight = CanvasScroller.ActualHeight;
    }

    private void CanvasWheel(object sender, MouseWheelEventArgs args)
    {
        if (_model is not { } model || Keyboard.Modifiers != ModifierKeys.Control || args.Delta == 0) return;
        args.Handled = true;
        double previous = model.Zoom, next = DesignerViewModel.StepZoom(previous, args.Delta > 0 ? 1 : -1);
        if (Math.Abs(next - previous) < 0.0001) return;
        // Keep the artboard point under the pointer in place.
        var pointer = args.GetPosition(CanvasScroller);
        double x = CanvasScroller.HorizontalOffset + pointer.X, y = CanvasScroller.VerticalOffset + pointer.Y;
        model.Zoom = next;
        CanvasScroller.UpdateLayout();
        CanvasScroller.ScrollToHorizontalOffset(Math.Max(0, x * next / previous - pointer.X));
        CanvasScroller.ScrollToVerticalOffset(Math.Max(0, y * next / previous - pointer.Y));
    }

    private void PanStart(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Middle) return;
        _panOrigin = args.GetPosition(CanvasScroller);
        _panStart = new Vector(CanvasScroller.HorizontalOffset, CanvasScroller.VerticalOffset);
        if (!CanvasScroller.CaptureMouse()) { _panOrigin = null; return; }
        CanvasScroller.Cursor = Cursors.ScrollAll;
        args.Handled = true;
    }

    private void PanMove(object sender, MouseEventArgs args)
    {
        if (_panOrigin is not { } origin) return;
        var delta = args.GetPosition(CanvasScroller) - origin;
        CanvasScroller.ScrollToHorizontalOffset(Math.Max(0, _panStart.X - delta.X));
        CanvasScroller.ScrollToVerticalOffset(Math.Max(0, _panStart.Y - delta.Y));
        args.Handled = true;
    }

    private void PanEnd(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Middle || _panOrigin is null) return;
        args.Handled = true;
        CanvasScroller.ReleaseMouseCapture();
        EndPan();
    }

    private void EndPan()
    {
        _panOrigin = null;
        CanvasScroller.ClearValue(CursorProperty);
    }
}
