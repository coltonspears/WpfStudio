using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfStudio.App.Features.Profiling.Visuals;

/// <summary>A snapshot shown on the timeline. Value is the managed heap size at capture.</summary>
public sealed record TimelineMarker(DateTimeOffset Time, string Label, long Value, string Detail, bool IsCurrent, bool IsBaseline, object? Tag = null);

/// <summary>Live process memory over time (private bytes as an area, working set as a line) with snapshot markers.
/// Without live samples it plots the snapshots' managed heap evenly spaced, so dumps still get a trend.</summary>
public sealed class MemoryTimeline : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(IReadOnlyList<ProcessMemorySample>), typeof(MemoryTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IReadOnlyList<TimelineMarker>), typeof(MemoryTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(nameof(WindowSeconds), typeof(double), typeof(MemoryTimeline), new FrameworkPropertyMetadata(300d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(nameof(SelectCommand), typeof(ICommand), typeof(MemoryTimeline));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(MemoryTimeline), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<ProcessMemorySample>? Samples { get => (IReadOnlyList<ProcessMemorySample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public IReadOnlyList<TimelineMarker>? Markers { get => (IReadOnlyList<TimelineMarker>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    /// <summary>Invoked with a marker's Tag when it is clicked.</summary>
    public ICommand? SelectCommand { get => (ICommand?)GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private const double Left = 58, Right = 14, Top = 18, Bottom = 20;
    private readonly List<(Point At, TimelineMarker Marker)> _markerHits = [];
    private Point? _hover;
    private Func<double, DateTimeOffset>? _timeAt;

    public MemoryTimeline() { MinHeight = 120; ToolTipService.SetInitialShowDelay(this, 0); ToolTipService.SetBetweenShowDelay(this, 0); Cursor = Cursors.Cross; }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 320 : 0, double.IsInfinity(availableSize.Height) ? 150 : Math.Min(150, availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        _markerHits.Clear(); _timeAt = null;
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < Left + Right + 40 || h < Top + Bottom + 30) return;
        var text = ChartPalette.Resource(this, "TextBrush", Colors.White);
        var muted = ChartPalette.Resource(this, "MutedBrush", Colors.Gray);
        var subtle = ChartPalette.Resource(this, "SubtleBrush", Colors.Gray);
        var grid = ChartPalette.Resource(this, "BorderBrush", Colors.DimGray);
        var accent = ChartPalette.Resource(this, "AccentBrush", Colors.SteelBlue);
        var warning = ChartPalette.Resource(this, "WarningBrush", Colors.Orange);
        var plot = new Rect(Left, Top, w - Left - Right, h - Top - Bottom);
        var samples = Samples ?? [];
        var markers = Markers ?? [];
        var timeMode = samples.Count > 0;
        if (!timeMode && markers.Count == 0)
        {
            if (EmptyText.Length > 0)
            {
                var empty = ChartPalette.Text(this, EmptyText, 12, muted, plot.Width);
                dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            }
            return;
        }

        // X scale: wall-clock time over the window, or one slot per snapshot.
        Func<DateTimeOffset, double> xOfTime = _ => plot.Left; Func<int, double> xOfIndex = _ => plot.Left;
        DateTimeOffset end = default, start = default;
        if (timeMode)
        {
            end = samples[^1].Time;
            foreach (var marker in markers) if (marker.Time > end) end = marker.Time;
            // The window is a maximum: a short recording fills the chart instead of hugging the right edge.
            var earliest = samples[0].Time;
            foreach (var marker in markers) if (marker.Time < earliest) earliest = marker.Time;
            var span = Math.Clamp((end - earliest).TotalSeconds * 1.04, 30, Math.Max(30, WindowSeconds));
            start = end - TimeSpan.FromSeconds(span);
            xOfTime = t => plot.Left + plot.Width * Math.Clamp((t - start).TotalSeconds / span, 0, 1);
            var s0 = start;
            _timeAt = x => s0 + TimeSpan.FromSeconds(Math.Clamp((x - plot.Left) / plot.Width, 0, 1) * span);
        }
        else
        {
            var slots = Math.Max(1, markers.Count - 1);
            xOfIndex = i => markers.Count == 1 ? plot.Left + plot.Width / 2 : plot.Left + plot.Width * i / slots;
        }

        // Y scale from zero to a rounded maximum.
        double max = 1;
        foreach (var s in samples) if (!timeMode || s.Time >= start) max = Math.Max(max, Math.Max(s.PrivateBytes, s.WorkingSetBytes));
        foreach (var m in markers) max = Math.Max(max, m.Value);
        max = NiceCeiling(max * 1.08);
        double Y(double value) => plot.Bottom - plot.Height * Math.Clamp(value / max, 0, 1);

        var gridPen = ChartPalette.Pen(ChartPalette.WithAlpha(grid, 0.9), 1);
        for (var i = 0; i <= 3; i++)
        {
            var value = max * i / 3; var y = Math.Round(Y(value)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = ChartPalette.Text(this, MemorySize.Format((long)value), 10, subtle, Left - 8);
            dc.DrawText(label, new Point(Left - 8 - label.Width, y - label.Height / 2));
        }

        if (timeMode)
        {
            var visible = samples.Where(s => s.Time >= start - TimeSpan.FromSeconds(2)).ToArray();
            if (visible.Length > 0)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(xOfTime(visible[0].Time), plot.Bottom), true, true);
                    foreach (var s in visible) g.LineTo(new Point(xOfTime(s.Time), Y(s.PrivateBytes)), true, true);
                    g.LineTo(new Point(xOfTime(visible[^1].Time), plot.Bottom), true, false);
                }
                area.Freeze();
                dc.DrawGeometry(ChartPalette.Frozen(ChartPalette.WithAlpha(accent, 0.16)), null, area);
                DrawLine(dc, visible.Select(s => new Point(xOfTime(s.Time), Y(s.PrivateBytes))).ToArray(), ChartPalette.Pen(accent, 1.6));
                DrawLine(dc, visible.Select(s => new Point(xOfTime(s.Time), Y(s.WorkingSetBytes))).ToArray(), ChartPalette.Pen(ChartPalette.WithAlpha(muted, 0.85), 1));
            }
            // Time labels at the window's start, middle and end.
            foreach (var fraction in new[] { 0d, 0.5, 1d })
            {
                var t = start + TimeSpan.FromSeconds((end - start).TotalSeconds * fraction);
                var label = ChartPalette.Text(this, t.LocalDateTime.ToString("T", CultureInfo.CurrentCulture), 10, subtle, 90);
                var x = plot.Left + plot.Width * fraction - label.Width * fraction;
                dc.DrawText(label, new Point(x, plot.Bottom + 4));
            }
        }
        else if (markers.Count > 1)
        {
            DrawLine(dc, markers.Select((m, i) => new Point(xOfIndex(i), Y(m.Value))).ToArray(), ChartPalette.Pen(accent, 1.6));
        }

        // Snapshot markers: a guide line, the managed-heap dot and a "#n" label. Labels that would collide are skipped
        // unless they mark the current snapshot or the baseline; the dot's tooltip still names every snapshot.
        var labelRight = double.NegativeInfinity;
        var dashed = new Pen(ChartPalette.Frozen(ChartPalette.WithAlpha(subtle, 0.8)), 1) { DashStyle = new DashStyle([3, 3], 0) };
        dashed.Freeze();
        for (var i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            if (timeMode && marker.Time < start) continue;
            var x = Math.Round(timeMode ? xOfTime(marker.Time) : xOfIndex(i)) + 0.5;
            var y = Y(marker.Value);
            if (timeMode) dc.DrawLine(dashed, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var fill = marker.IsCurrent ? accent : marker.IsBaseline ? warning : ChartPalette.Resource(this, "EditorBrush", Colors.Black);
            var ring = marker.IsBaseline ? warning : accent;
            dc.DrawEllipse(ChartPalette.Frozen(fill), ChartPalette.Pen(ring, 2), new Point(x, y), 4.5, 4.5);
            var label = ChartPalette.Text(this, marker.Label, 10.5, marker.IsCurrent ? text : muted, 80, bold: marker.IsCurrent || marker.IsBaseline);
            var lx = Math.Clamp(x - label.Width / 2, plot.Left - 4, plot.Right - label.Width + 4);
            if (lx > labelRight + 4 || marker.IsCurrent || marker.IsBaseline || !timeMode)
            {
                if (timeMode && lx <= labelRight + 4) lx = labelRight + 4;
                dc.DrawText(label, new Point(Math.Min(lx, plot.Right - label.Width + 4), timeMode ? plot.Top - label.Height - 2 : Math.Max(0, y - label.Height - 7)));
                labelRight = Math.Min(lx, plot.Right - label.Width + 4) + label.Width;
            }
            if (!timeMode)
            {
                var index = ChartPalette.Text(this, marker.Time.LocalDateTime.ToString("t", CultureInfo.CurrentCulture), 10, subtle, 80);
                dc.DrawText(index, new Point(Math.Clamp(x - index.Width / 2, plot.Left - 4, plot.Right - index.Width + 4), plot.Bottom + 4));
            }
            _markerHits.Add((new Point(x, y), marker));
        }

        if (_hover is { } hover && timeMode && plot.Contains(hover))
        {
            var crosshair = ChartPalette.Pen(ChartPalette.WithAlpha(text, 0.35), 1);
            dc.DrawLine(crosshair, new Point(Math.Round(hover.X) + 0.5, plot.Top), new Point(Math.Round(hover.X) + 0.5, plot.Bottom));
        }
    }

    private static void DrawLine(DrawingContext dc, Point[] points, Pen pen)
    {
        if (points.Length < 2) { if (points.Length == 1) dc.DrawEllipse(pen.Brush, null, points[0], 1.5, 1.5); return; }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], false, false);
            g.PolyLineTo(points[1..], true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>1, 2 or 5 times a power of two-ish byte unit, so axis labels read cleanly.</summary>
    internal static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        var unit = value >= 1 << 30 ? 1 << 30 : value >= 1 << 20 ? 1 << 20 : value >= 1 << 10 ? 1 << 10 : 1;
        var scaled = value / unit;
        var power = Math.Pow(10, Math.Floor(Math.Log10(scaled)));
        var step = scaled / power <= 1.5 ? 1.5 : scaled / power <= 3 ? 3 : scaled / power <= 6 ? 6 : 10;
        return step * power * unit;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.GetPosition(this);
        var near = NearestMarker(_hover.Value);
        if (near is { } marker) ToolTip = $"{marker.Label} · {marker.Time.LocalDateTime:T}\n{marker.Detail}" + (SelectCommand is null ? "" : "\nClick to compare with this snapshot");
        else if (_timeAt is { } timeAt && Samples is { Count: > 0 } samples)
        {
            var t = timeAt(_hover.Value.X);
            var sample = samples.MinBy(s => Math.Abs((s.Time - t).TotalSeconds))!;
            ToolTip = $"{sample.Time.LocalDateTime:T}\nPrivate bytes {MemorySize.Format(sample.PrivateBytes)}\nWorking set {MemorySize.Format(sample.WorkingSetBytes)}";
        }
        else ToolTip = null;
        Cursor = near is not null && SelectCommand is not null ? Cursors.Hand : Cursors.Cross;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = null; ToolTip = null; InvalidateVisual(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (NearestMarker(e.GetPosition(this)) is { } marker && SelectCommand?.CanExecute(marker.Tag) == true) SelectCommand.Execute(marker.Tag);
    }

    private TimelineMarker? NearestMarker(Point point)
    {
        TimelineMarker? best = null; var bestDistance = 10d;
        foreach (var (at, marker) in _markerHits)
        {
            var distance = Math.Abs(at.X - point.X);
            if (distance < bestDistance) { best = marker; bestDistance = distance; }
        }
        return best;
    }
}

/// <summary>A tiny trend line for table rows: values left to right, scaled to their own range, with the last point marked.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public Sparkline() { Height = 18; SnapsToDevicePixels = true; }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 60 : Math.Min(60, availableSize.Width), Math.Min(double.IsNaN(Height) ? 18 : Height, availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (Values is not { Count: > 1 } values || w <= 6 || h <= 4) return;
        var min = values.Min(); var max = values.Max(); var range = max - min;
        var color = Stroke is SolidColorBrush brush ? brush.Color : ChartPalette.Resource(this, "AccentBrush", Colors.SteelBlue);
        var points = values.Select((v, i) => new Point(2 + (w - 4) * i / (values.Count - 1), range <= 0 ? h / 2 : h - 2 - (h - 4) * (v - min) / range)).ToArray();
        var geometry = new StreamGeometry();
        using (var g = geometry.Open()) { g.BeginFigure(points[0], false, false); g.PolyLineTo(points[1..], true, true); }
        geometry.Freeze();
        dc.DrawGeometry(null, ChartPalette.Pen(color, 1.3), geometry);
        dc.DrawEllipse(ChartPalette.Frozen(color), null, points[^1], 2.2, 2.2);
    }
}

/// <summary>A diverging bar centred on zero: growth extends right in the danger colour, shrinkage left in the success
/// colour. Value is a ratio in [-1, 1].</summary>
public sealed class DeltaBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(DeltaBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public DeltaBar() { Height = 8; SnapsToDevicePixels = true; }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 80 : 0, Math.Min(double.IsNaN(Height) ? 8 : Height, availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 4 || h <= 0) return;
        var mid = Math.Round(w / 2);
        dc.DrawRectangle(ChartPalette.Brush(this, "BorderBrush"), null, new Rect(mid - 0.5, 0, 1, h));
        var ratio = Math.Clamp(Value, -1, 1);
        if (Math.Abs(ratio) < 1e-9) return;
        var width = Math.Max(2, (w / 2 - 1) * Math.Abs(ratio));
        var brush = ratio > 0 ? ChartPalette.Brush(this, "DangerBrush") : ChartPalette.Brush(this, "SuccessBrush");
        var rect = ratio > 0 ? new Rect(mid + 1, 0, width, h) : new Rect(mid - 1 - width, 0, width, h);
        dc.DrawRoundedRectangle(brush, null, rect, Math.Min(2, h / 2), Math.Min(2, h / 2));
    }
}
