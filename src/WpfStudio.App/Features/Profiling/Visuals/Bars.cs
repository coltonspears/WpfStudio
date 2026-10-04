using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfStudio.App.Features.Profiling.Visuals;

/// <summary>A thin proportional bar (value / maximum) for list rows. Negative values draw from the right in the
/// negative brush, so growth and shrinkage read at a glance.</summary>
public sealed class RatioBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RatioBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RatioBar), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(RatioBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty NegativeFillProperty = DependencyProperty.Register(nameof(NegativeFill), typeof(Brush), typeof(RatioBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(RatioBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? NegativeFill { get => (Brush?)GetValue(NegativeFillProperty); set => SetValue(NegativeFillProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public RatioBar() { Height = 4; SnapsToDevicePixels = true; }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 40 : 0, Math.Min(double.IsNaN(Height) ? 4 : Height, availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var radius = Math.Min(2, h / 2);
        dc.DrawRoundedRectangle(Track ?? ChartPalette.Brush(this, "BorderBrush"), null, new Rect(0, 0, w, h), radius, radius);
        var max = Math.Max(Maximum, 1e-9);
        var ratio = Math.Clamp(Math.Abs(Value) / max, 0, 1);
        if (ratio <= 0) return;
        var width = Math.Max(2, w * ratio);
        var brush = Value < 0 ? NegativeFill ?? ChartPalette.Brush(this, "SuccessBrush") : Fill ?? ChartPalette.Brush(this, "AccentBrush");
        dc.DrawRoundedRectangle(brush, null, Value < 0 ? new Rect(w - width, 0, width, h) : new Rect(0, 0, width, h), radius, radius);
    }
}

public sealed record BarSegment(string Label, double Value, int ColorIndex, string ValueText);

/// <summary>A horizontal stacked bar of categorical segments separated by 2 px surface gaps, with per-segment
/// hover text. Used for heap generations.</summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments), typeof(IReadOnlyList<BarSegment>), typeof(SegmentBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<BarSegment>? Segments { get => (IReadOnlyList<BarSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    private readonly List<(Rect Bounds, BarSegment Segment)> _hits = [];
    private BarSegment? _hover;

    public SegmentBar() { Height = 10; SnapsToDevicePixels = true; ToolTipService.SetInitialShowDelay(this, 150); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : 0, Math.Min(double.IsNaN(Height) ? 10 : Height, availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Segments is not { Count: > 0 } segments || w <= 0) return;
        var total = segments.Sum(s => Math.Max(0, s.Value));
        if (total <= 0) return;
        var visible = segments.Where(s => s.Value > 0).ToArray();
        var gap = 2d; var usable = Math.Max(0, w - gap * (visible.Length - 1));
        var x = 0d;
        for (var i = 0; i < visible.Length; i++)
        {
            var segment = visible[i];
            var width = Math.Max(2, usable * segment.Value / total);
            if (i == visible.Length - 1) width = Math.Max(2, w - x);
            var rect = new Rect(x, 0, width, h);
            var color = ChartPalette.Series(this, segment.ColorIndex);
            if (_hover is not null && _hover != segment) color = ChartPalette.WithAlpha(color, 0.45);
            var r = i == 0 || i == visible.Length - 1 ? Math.Min(4, h / 2) : 0;
            dc.DrawRoundedRectangle(ChartPalette.Frozen(color), null, rect, Math.Min(r, width / 2), Math.Min(r, h / 2));
            _hits.Add((rect, segment));
            x += width + gap;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var hit = _hits.FirstOrDefault(x => x.Bounds.Contains(point) || point.X >= x.Bounds.Left && point.X <= x.Bounds.Right + 2).Segment;
        if (hit == _hover) return;
        _hover = hit; ToolTip = hit is null ? null : $"{hit.Label}: {hit.ValueText}"; InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = null; InvalidateVisual(); }
}
