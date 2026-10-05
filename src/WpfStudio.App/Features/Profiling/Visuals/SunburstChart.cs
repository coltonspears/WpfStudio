using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling.Visuals;

/// <summary>A radial view of the dominator tree: the focused owner in the centre, what it keeps alive in the first ring,
/// what those keep alive in the next, and so on. A segment's angle is its share of its parent's retained bytes, so the
/// gap left in a ring is the parent's own size. Hues follow the first ring; deeper rings are lighter tints.</summary>
public sealed class SunburstChart : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(MemorySunburst), typeof(SunburstChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SunburstChart)d)._hover = null));
    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(nameof(SelectedKey), typeof(string), typeof(SunburstChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(nameof(SelectCommand), typeof(ICommand), typeof(SunburstChart));
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(SunburstChart));
    public static readonly DependencyProperty UpCommandProperty = DependencyProperty.Register(nameof(UpCommand), typeof(ICommand), typeof(SunburstChart));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(SunburstChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public MemorySunburst? Data { get => (MemorySunburst?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public string? SelectedKey { get => (string?)GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }
    /// <summary>Invoked with the clicked <see cref="MemorySunburstNode"/>.</summary>
    public ICommand? SelectCommand { get => (ICommand?)GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value); }
    /// <summary>Invoked on double-click with the path from the centre's child down to the segment.</summary>
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    /// <summary>Invoked when the centre is clicked, to step back out.</summary>
    public ICommand? UpCommand { get => (ICommand?)GetValue(UpCommandProperty); set => SetValue(UpCommandProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private sealed record Segment(MemorySunburstNode Node, IReadOnlyList<MemorySunburstNode> Path, int Ring, double Start, double Sweep, Color Color);
    private readonly List<Segment> _segments = [];
    private Point _centre; private double _inner, _ring;
    private Segment? _hover;

    public SunburstChart() { ClipToBounds = true; ToolTipService.SetInitialShowDelay(this, 150); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 320 : 0, double.IsInfinity(availableSize.Height) ? 320 : 0);

    protected override void OnRender(DrawingContext dc)
    {
        _segments.Clear();
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var muted = ChartPalette.Resource(this, "MutedBrush", Colors.Gray);
        var text = ChartPalette.Resource(this, "TextBrush", Colors.White);
        var surface = ChartPalette.Resource(this, "EditorBrush", Colors.Black);
        if (Data is not { } data || data.Root.Children.Count == 0 || data.TotalBytes <= 0)
        {
            if (EmptyText.Length > 0 && w > 40)
            {
                var empty = ChartPalette.Text(this, EmptyText, 12, muted, w - 20);
                dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            }
            return;
        }
        var radius = Math.Min(w, h) / 2 - 6;
        if (radius < 60) return;
        var depth = Math.Max(1, Depth(data.Root) - 1);
        _centre = new Point(w / 2, h / 2);
        _inner = Math.Max(42, radius * 0.26);
        _ring = (radius - _inner) / depth;

        // Lay out: each child's sweep is its share of the parent's bytes.
        var top = data.Root.Children;
        for (var i = 0; i < top.Count; i++)
        {
            var color = top[i].IsOther ? ChartPalette.Series(this, -1) : ChartPalette.Series(this, i);
            Layout(top[i], [top[i]], 0, Offset(top, i, data.Root.RetainedBytes, 0, 360), Sweep(top[i], data.Root.RetainedBytes, 360), color);
        }

        var separator = ChartPalette.Pen(surface, 1.2);
        var hoverPath = _hover?.Path;
        foreach (var segment in _segments)
        {
            var color = segment.Color;
            if (hoverPath is not null && !OnPath(segment, hoverPath)) color = ChartPalette.Mix(color, surface, 0.55);
            var geometry = Sector(segment.Ring, segment.Start, segment.Sweep);
            dc.DrawGeometry(ChartPalette.Frozen(color), separator, geometry);
        }
        if (SelectedKey is { } selected && _segments.FirstOrDefault(s => s.Node.Key == selected) is { } chosen)
            dc.DrawGeometry(null, ChartPalette.Pen(text, 2), Sector(chosen.Ring, chosen.Start, chosen.Sweep));

        // Centre: the focused owner and its total.
        dc.DrawEllipse(ChartPalette.Frozen(surface), ChartPalette.Pen(ChartPalette.Resource(this, "BorderBrush", Colors.DimGray), 1), _centre, _inner - 3, _inner - 3);

        // Labels where they fit: horizontal text at the segment's middle, sized so it stays inside its ring and its angle.
        foreach (var segment in _segments)
        {
            var mid = _inner + _ring * (segment.Ring + 0.5);
            var arc = Math.PI * 2 * mid * segment.Sweep / 360;
            if (arc < 40 || _ring < 18) continue;
            var angle = (segment.Start + segment.Sweep / 2 - 90) * Math.PI / 180;
            var radial = Math.Abs(Math.Cos(angle)); var tangential = Math.Abs(Math.Sin(angle));
            var width = Math.Min(Math.Min((_ring - 6) / Math.Max(radial, 0.05), (arc - 8) / Math.Max(tangential, 0.05)), 170);
            if (width < 28) continue;
            var at = new Point(_centre.X + mid * Math.Cos(angle), _centre.Y + mid * Math.Sin(angle));
            var color = ChartPalette.ReadableText(segment.Color);
            var label = ChartPalette.Text(this, segment.Node.Label, 10.5, color, width, bold: segment.Ring == 0);
            dc.DrawText(label, new Point(at.X - label.Width / 2, at.Y - label.Height / 2));
        }

        var name = ChartPalette.Text(this, data.Root.Label, 11.5, text, _inner * 1.6, bold: true);
        var total = ChartPalette.Text(this, MemorySize.Format(data.TotalBytes), 13, ChartPalette.Resource(this, "AccentBrush", Colors.SteelBlue), _inner * 1.6, bold: true);
        var hint = UpCommand?.CanExecute(null) == true && data.Root.Key != "heap" ? ChartPalette.Text(this, "click to go up", 9.5, muted, _inner * 1.6) : null;
        var height = name.Height + total.Height + (hint?.Height ?? 0);
        var y = _centre.Y - height / 2;
        dc.DrawText(name, new Point(_centre.X - name.Width / 2, y)); y += name.Height;
        dc.DrawText(total, new Point(_centre.X - total.Width / 2, y)); y += total.Height;
        if (hint is not null) dc.DrawText(hint, new Point(_centre.X - hint.Width / 2, y));
    }

    private void Layout(MemorySunburstNode node, IReadOnlyList<MemorySunburstNode> path, int ring, double start, double sweep, Color color)
    {
        if (sweep < 0.25) return;
        _segments.Add(new(node, path, ring, start, sweep, color));
        if (node.Children.Count == 0 || node.RetainedBytes <= 0) return;
        var surface = ChartPalette.Resource(this, "EditorBrush", Colors.Black);
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var tint = child.IsOther ? ChartPalette.Mix(ChartPalette.Series(this, -1), surface, 0.35) : ChartPalette.Mix(color, surface, 0.22);
            Layout(child, [.. path, child], ring + 1, start + Offset(node.Children, i, node.RetainedBytes, 0, sweep), Sweep(child, node.RetainedBytes, sweep), tint);
        }
    }

    private static double Sweep(MemorySunburstNode node, long parentBytes, double parentSweep) =>
        parentBytes <= 0 ? 0 : parentSweep * Math.Min(1, (double)node.RetainedBytes / parentBytes);

    private static double Offset(IReadOnlyList<MemorySunburstNode> siblings, int index, long parentBytes, double start, double parentSweep)
    {
        var offset = start;
        for (var i = 0; i < index; i++) offset += Sweep(siblings[i], parentBytes, parentSweep);
        return offset;
    }

    private static int Depth(MemorySunburstNode node) => node.Children.Count == 0 ? 1 : 1 + node.Children.Max(Depth);

    private static bool OnPath(Segment segment, IReadOnlyList<MemorySunburstNode> hoverPath) =>
        segment.Path.Count <= hoverPath.Count ? ReferenceEquals(hoverPath[segment.Path.Count - 1], segment.Node) : ReferenceEquals(segment.Path[hoverPath.Count - 1], hoverPath[^1]);

    private Geometry Sector(int ring, double start, double sweep)
    {
        var r1 = _inner + _ring * ring; var r2 = r1 + _ring - 1;
        sweep = Math.Min(sweep, 359.99);
        Point At(double radius, double degrees) { var a = (degrees - 90) * Math.PI / 180; return new(_centre.X + radius * Math.Cos(a), _centre.Y + radius * Math.Sin(a)); }
        var large = sweep > 180;
        var figure = new PathFigure { StartPoint = At(r1, start), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(At(r2, start), true));
        figure.Segments.Add(new ArcSegment(At(r2, start + sweep), new Size(r2, r2), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(r1, start + sweep), true));
        figure.Segments.Add(new ArcSegment(At(r1, start), new Size(r1, r1), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private Segment? HitTest(Point point, out bool centre)
    {
        var dx = point.X - _centre.X; var dy = point.Y - _centre.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        centre = distance < _inner - 3;
        if (centre || _ring <= 0) return null;
        var ring = (int)Math.Floor((distance - _inner) / _ring);
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
        if (angle < 0) angle += 360;
        return _segments.FirstOrDefault(s => s.Ring == ring && angle >= s.Start && angle < s.Start + s.Sweep);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this), out var centre);
        Cursor = hit is not null && !hit.Node.IsOther || centre && UpCommand is not null ? Cursors.Hand : null;
        if (ReferenceEquals(hit, _hover)) return;
        _hover = hit;
        if (hit is null) ToolTip = centre && Data is { } data ? $"{data.Root.Label}\n{data.Root.Detail}\nRetains {MemorySize.Format(data.TotalBytes)}" : null;
        else
        {
            var share = Data is { TotalBytes: > 0 } d ? (double)hit.Node.RetainedBytes / d.TotalBytes : 0;
            ToolTip = $"{hit.Node.Label}\n{hit.Node.Detail}\nRetains {MemorySize.Format(hit.Node.RetainedBytes)} · {share:P1} of the centre" +
                (hit.Node.IsOther ? "" : hit.Node.ObjectId is null ? "\nDouble-click to drill in" : "\nClick to inspect · double-click to drill in");
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = null; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTest(e.GetPosition(this), out var centre);
        if (centre && e.ClickCount == 1) { if (UpCommand?.CanExecute(null) == true) UpCommand.Execute(null); e.Handled = true; return; }
        if (hit is null || hit.Node.IsOther) return;
        if (e.ClickCount >= 2) { if (OpenCommand?.CanExecute(hit.Path) == true) OpenCommand.Execute(hit.Path); }
        else if (SelectCommand?.CanExecute(hit.Node) == true) SelectCommand.Execute(hit.Node);
        e.Handled = true;
    }
}
