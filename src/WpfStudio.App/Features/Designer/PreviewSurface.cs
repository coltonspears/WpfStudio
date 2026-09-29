using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Designer;

/// <summary>A point in preview DIPs. <paramref name="Exact"/> picks the exact visual rather than its authored element.</summary>
public sealed record PreviewPoint(double X, double Y, bool Exact = false);

/// <summary>Draws the remote bitmap and selection; translates mouse coordinates to preview DIPs.</summary>
public sealed partial class PreviewSurface : FrameworkElement
{
    public static readonly DependencyProperty ImageBytesProperty = DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, ImageChanged));
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(nameof(Scale), typeof(double), typeof(PreviewSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, GestureContextChanged));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(nameof(Selection), typeof(PreviewBounds), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, GestureContextChanged));
    public static readonly DependencyProperty PickCommandProperty = DependencyProperty.Register(nameof(PickCommand), typeof(ICommand), typeof(PreviewSurface));
    public static readonly DependencyProperty LayoutOverlaysProperty = DependencyProperty.Register(nameof(LayoutOverlays), typeof(IReadOnlyList<LayoutOverlay>), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HoverNodesProperty = DependencyProperty.Register(nameof(HoverNodes), typeof(IReadOnlyList<PreviewNode>), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, HoverNodesChanged));
    public static readonly DependencyProperty SelectionLabelProperty = DependencyProperty.Register(nameof(SelectionLabel), typeof(string), typeof(PreviewSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private BitmapSource? _bitmap;
    private (Rect Bounds, PreviewNode Node)[] _hoverTargets = [];
    private PreviewNode? _hover;
    public byte[]? ImageBytes { get => (byte[]?)GetValue(ImageBytesProperty); set => SetValue(ImageBytesProperty, value); }
    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }
    public PreviewBounds? Selection { get => (PreviewBounds?)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public ICommand? PickCommand { get => (ICommand?)GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    public IReadOnlyList<LayoutOverlay>? LayoutOverlays { get => (IReadOnlyList<LayoutOverlay>?)GetValue(LayoutOverlaysProperty); set => SetValue(LayoutOverlaysProperty, value); }
    /// <summary>Snapshot nodes used for hover hints; the click itself is still hit tested by the preview host.</summary>
    public IReadOnlyList<PreviewNode>? HoverNodes { get => (IReadOnlyList<PreviewNode>?)GetValue(HoverNodesProperty); set => SetValue(HoverNodesProperty, value); }
    /// <summary>Type and name shown on the selection adorner.</summary>
    public string? SelectionLabel { get => (string?)GetValue(SelectionLabelProperty); set => SetValue(SelectionLabelProperty, value); }
    private double SafeScale => double.IsFinite(Scale) ? Math.Clamp(Scale, .1, 3) : 1;
    private static void ImageChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var surface = (PreviewSurface)owner; surface.CancelLayoutGesture(); surface._bitmap = null;
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
        DrawHover(drawing);
        Rect? selected = null;
        if (Selection is { Width: >= 0, Height: >= 0 } bounds &&
            double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
            double.IsFinite((Math.Abs(bounds.X) + bounds.Width) * SafeScale) && double.IsFinite((Math.Abs(bounds.Y) + bounds.Height) * SafeScale))
        {
            var rect = new Rect(bounds.X * SafeScale, bounds.Y * SafeScale, bounds.Width * SafeScale, bounds.Height * SafeScale);
            selected = rect;
            if (!drewLayout && !CanEditLayout)
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, AdornerColor.R, AdornerColor.G, AdornerColor.B)), AdornerPen(1.5), rect);
                DrawCornerTicks(drawing, rect);
            }
        }
        DrawLayoutEditing(drawing);
        drawing.Pop();
        // Labels may extend past the artboard edge; the surface still clips to its own bounds.
        if (selected is { } label && !string.IsNullOrEmpty(SelectionLabel) && Selection is { } size)
            DrawTag(drawing, label, $"{SelectionLabel}  {size.Width:0.#} × {size.Height:0.#}", AdornerColor, Colors.White);
    }

    // Selection and hover adorners use one fixed designer colour so they stay visible on any artboard.
    private static readonly Color AdornerColor = Color.FromRgb(0x4F, 0x6B, 0xFF);
    private static readonly Color HoverColor = Color.FromRgb(0x8C, 0x9B, 0xFF);
    private static Pen AdornerPen(double thickness) { var pen = new Pen(new SolidColorBrush(AdornerColor), thickness); pen.Freeze(); return pen; }

    private static void DrawCornerTicks(DrawingContext drawing, Rect rect)
    {
        if (rect.Width < 12 || rect.Height < 12) return;
        var brush = new SolidColorBrush(AdornerColor); brush.Freeze();
        const double size = 5;
        foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft })
            drawing.DrawRectangle(Brushes.White, new Pen(brush, 1.25), new Rect(corner.X - size / 2, corner.Y - size / 2, size, size));
    }

    private void DrawTag(DrawingContext drawing, Rect target, string text, Color background, Color foreground)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11,
            new SolidColorBrush(foreground), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        const double padX = 5, padY = 2;
        double width = formatted.Width + padX * 2, height = formatted.Height + padY * 2;
        // Above the element when there is room, otherwise just inside its top edge.
        double y = target.Top - height - 2 >= 0 ? target.Top - height - 2 : Math.Max(0, target.Top + 2);
        double x = Math.Max(0, Math.Min(target.Left, ActualWidth - width));
        var tag = new Rect(x, y, width, height);
        drawing.DrawRoundedRectangle(new SolidColorBrush(background), null, tag, 3, 3);
        drawing.DrawText(formatted, new Point(x + padX, y + padY));
    }

    private void DrawHover(DrawingContext drawing)
    {
        if (_hover?.Bounds is not { } bounds || _pointerArmed || Selection is { } selection && selection == bounds) return;
        var rect = new Rect(bounds.X * SafeScale, bounds.Y * SafeScale, bounds.Width * SafeScale, bounds.Height * SafeScale);
        var pen = new Pen(new SolidColorBrush(HoverColor), 1) { DashStyle = new DashStyle([4, 3], 0) };
        drawing.DrawRectangle(null, pen, rect);
        var name = _hover.Type.Split('.').Last().Split('`')[0] + (string.IsNullOrEmpty(_hover.Name) ? "" : " #" + _hover.Name);
        DrawTag(drawing, rect, name, HoverColor, Color.FromRgb(0x14, 0x16, 0x2E));
    }

    private static void HoverNodesChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var surface = (PreviewSurface)owner;
        var nodes = (args.NewValue as IReadOnlyList<PreviewNode> ?? []).Where(node => node.IsVisual && node.Bounds is { Width: > 0, Height: > 0 } b
            && double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.Width) && double.IsFinite(b.Height)).ToArray();
        // Hover the authored element a click will select; compiled views without a source map use every visual.
        var authored = nodes.Where(node => node.Source is not null).ToArray();
        surface._hoverTargets = (authored.Length > 0 ? authored : nodes)
            .Select(node => (new Rect(node.Bounds!.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height), node))
            .OrderBy(target => target.Item1.Width * target.Item1.Height).ToArray();
        surface.SetHover(null);
    }

    private void UpdateHover(Point point)
    {
        if (!IsEnabled || _bitmap is null || _pointerArmed) { SetHover(null); return; }
        var target = new Point(point.X / SafeScale, point.Y / SafeScale);
        SetHover(_hoverTargets.FirstOrDefault(candidate => candidate.Bounds.Contains(target)).Node);
    }

    private void SetHover(PreviewNode? node)
    {
        if (ReferenceEquals(node, _hover)) return;
        _hover = node;
        InvalidateVisual();
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
}
