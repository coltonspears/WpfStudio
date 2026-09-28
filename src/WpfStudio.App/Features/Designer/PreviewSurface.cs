using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Designer;

public sealed record PreviewPoint(double X, double Y);

/// <summary>Draws the remote bitmap and selection; translates mouse coordinates to preview DIPs.</summary>
public sealed class PreviewSurface : FrameworkElement
{
    public static readonly DependencyProperty ImageBytesProperty = DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, ImageChanged));
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(nameof(Scale), typeof(double), typeof(PreviewSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(nameof(Selection), typeof(PreviewBounds), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PickCommandProperty = DependencyProperty.Register(nameof(PickCommand), typeof(ICommand), typeof(PreviewSurface));
    public static readonly DependencyProperty LayoutOverlaysProperty = DependencyProperty.Register(nameof(LayoutOverlays), typeof(IReadOnlyList<LayoutOverlay>), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private BitmapSource? _bitmap;
    public byte[]? ImageBytes { get => (byte[]?)GetValue(ImageBytesProperty); set => SetValue(ImageBytesProperty, value); }
    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }
    public PreviewBounds? Selection { get => (PreviewBounds?)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public ICommand? PickCommand { get => (ICommand?)GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    public IReadOnlyList<LayoutOverlay>? LayoutOverlays { get => (IReadOnlyList<LayoutOverlay>?)GetValue(LayoutOverlaysProperty); set => SetValue(LayoutOverlaysProperty, value); }
    private double SafeScale => double.IsFinite(Scale) ? Math.Clamp(Scale, .1, 3) : 1;
    private static void ImageChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var surface = (PreviewSurface)owner; surface._bitmap = null;
        if (args.NewValue is not byte[] { Length: > 0 } bytes) return;
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            surface._bitmap = bitmap;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or System.Runtime.InteropServices.COMException) { surface.ToolTip = exception.Message; }
    }
    protected override Size MeasureOverride(Size availableSize) => _bitmap == null ? new(0, 0) : new(_bitmap.PixelWidth * SafeScale, _bitmap.PixelHeight * SafeScale);
    protected override void OnRender(DrawingContext drawing)
    {
        if (_bitmap == null) return;
        var imageBounds = new Rect(0, 0, _bitmap.PixelWidth * SafeScale, _bitmap.PixelHeight * SafeScale);
        drawing.DrawImage(_bitmap, imageBounds);
        // Remote geometry is in the same 96-DPI root coordinates as the bitmap.
        // Drawing directly adds no visual children or hit-test targets to picking.
        drawing.PushClip(new RectangleGeometry(imageBounds));
        bool drewLayout = false;
        foreach (var overlay in LayoutOverlays ?? [])
        {
            if (!TryGeometry(overlay, SafeScale, out var geometry)) continue;
            var color = overlay.Kind switch
            {
                "slot" => Color.FromRgb(0x38, 0xBD, 0xF8),
                "render" => Color.FromRgb(0x4A, 0xDE, 0x80),
                "margin" => Color.FromRgb(0xFB, 0x92, 0x3C),
                "clip-bounds" => Color.FromRgb(0xC0, 0x84, 0xFC),
                _ => Colors.Transparent
            };
            if (color.A == 0) continue;
            var pen = new Pen(new SolidColorBrush(color), 1.7)
            {
                DashStyle = overlay.Kind switch { "slot" => DashStyles.Dash, "margin" => DashStyles.Dot, "clip-bounds" => DashStyles.DashDot, _ => DashStyles.Solid }
            };
            drawing.DrawGeometry(new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B)), pen, geometry);
            drewLayout = true;
        }
        if (!drewLayout && Selection is { Width: >= 0, Height: >= 0 } bounds &&
            double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
            double.IsFinite((Math.Abs(bounds.X) + bounds.Width) * SafeScale) && double.IsFinite((Math.Abs(bounds.Y) + bounds.Height) * SafeScale))
        {
            var rect = new Rect(bounds.X * SafeScale, bounds.Y * SafeScale, bounds.Width * SafeScale, bounds.Height * SafeScale);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 70, 160, 255)), new Pen(Brushes.DodgerBlue, 2), rect);
        }
        drawing.Pop();
    }
    private static bool TryGeometry(LayoutOverlay overlay, double scale, out StreamGeometry geometry)
    {
        geometry = new StreamGeometry();
        if (overlay.Points is not { Count: >= 3 and <= 64 } points || points.Any(p => !double.IsFinite(p.X * scale) || !double.IsFinite(p.Y * scale) || Math.Abs(p.X) > 1_000_000 || Math.Abs(p.Y) > 1_000_000)) return false;
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X * scale, points[0].Y * scale), true, true);
            for (var i = 1; i < points.Count; i++) context.LineTo(new Point(points[i].X * scale, points[i].Y * scale), true, false);
        }
        geometry.Freeze();
        return true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        var point = args.GetPosition(this); var request = new PreviewPoint(point.X / SafeScale, point.Y / SafeScale);
        if (_bitmap != null && PickCommand?.CanExecute(request) == true) { PickCommand.Execute(request); args.Handled = true; }
    }
}
