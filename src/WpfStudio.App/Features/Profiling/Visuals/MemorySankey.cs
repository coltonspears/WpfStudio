using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling.Visuals;

/// <summary>Retention-path Sankey. GC roots sit on the left, the investigated type on the right, and each band's width
/// is the number of instances (or retained bytes) that are kept alive through that owner. Hovering traces a flow
/// end to end; clicking selects a node, double-clicking opens it.</summary>
public sealed class MemorySankey : FrameworkElement
{
    public static readonly DependencyProperty FlowProperty = DependencyProperty.Register(nameof(Flow), typeof(MemoryRetentionFlow), typeof(MemorySankey), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MemorySankey)d).Invalidate()));
    public static readonly DependencyProperty UseBytesProperty = DependencyProperty.Register(nameof(UseBytes), typeof(bool), typeof(MemorySankey), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MemorySankey)d).Invalidate()));
    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(nameof(SelectCommand), typeof(ICommand), typeof(MemorySankey));
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(MemorySankey));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(MemorySankey), new FrameworkPropertyMetadata("Select a type to see what keeps its instances alive.", FrameworkPropertyMetadataOptions.AffectsRender));
    public MemoryRetentionFlow? Flow { get => (MemoryRetentionFlow?)GetValue(FlowProperty); set => SetValue(FlowProperty, value); }
    public bool UseBytes { get => (bool)GetValue(UseBytesProperty); set => SetValue(UseBytesProperty, value); }
    public ICommand? SelectCommand { get => (ICommand?)GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value); }
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private const double NodeWidth = 12, Padding = 14, LabelSpace = 168;
    private readonly Dictionary<int, Rect> _nodeRects = [];
    private readonly List<Band> _bands = [];
    private Size _laidOutFor;
    private int? _hoverNode, _selectedNode;
    private Band? _hoverBand;
    private HashSet<int> _litNodes = [];
    private HashSet<Band> _litBands = [];

    private sealed record Band(MemoryFlowLink Link, double SourceY, double TargetY, double Thickness, double X0, double X1);

    public MemorySankey()
    {
        Focusable = true; ClipToBounds = true;
        ToolTipService.SetInitialShowDelay(this, 200);
        ToolTipService.SetBetweenShowDelay(this, 0);
    }

    private void Invalidate() { _laidOutFor = default; _hoverNode = null; _hoverBand = null; _selectedNode = null; _litNodes = []; _litBands = []; InvalidateMeasure(); InvalidateVisual(); }
    private const double MinColumnGap = 175;

    /// <summary>Asks for enough width to keep columns readable; inside a horizontal ScrollViewer the flow scrolls instead
    /// of crushing its labels. Never asks for more than a finite available size.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var levels = Flow is { Nodes.Count: > 0 } flow ? flow.Nodes.Max(n => n.Level) : 0;
        var wanted = Math.Max(360, levels * MinColumnGap + LabelSpace + 20);
        return new(double.IsInfinity(availableSize.Width) ? wanted : Math.Min(wanted, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? 260 : 0);
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); _laidOutFor = default; }

    private double Value(MemoryFlowNode node) => UseBytes ? node.Bytes : node.Count;
    private double Value(MemoryFlowLink link) => UseBytes ? link.Bytes : link.Count;

    private void Layout()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size == _laidOutFor) return;
        _laidOutFor = size; _nodeRects.Clear(); _bands.Clear();
        if (Flow is not { Nodes.Count: > 0 } flow || size.Width < 120 || size.Height < 60) return;
        var maxLevel = flow.Nodes.Max(n => n.Level);
        // An instance held through several paths appears on each, so a node is as tall as its widest side.
        var sizes = flow.Nodes.ToDictionary(n => n.Id, n => Math.Max(Value(n), Math.Max(
            flow.Links.Where(l => l.ToId == n.Id).Sum(Value), flow.Links.Where(l => l.FromId == n.Id).Sum(Value))));
        double Size(MemoryFlowNode node) => sizes[node.Id];
        var columns = Enumerable.Range(0, maxLevel + 1).Select(level => flow.Nodes.Where(n => n.Level == level).ToList()).ToArray();
        var left = 8d; var right = size.Width - LabelSpace;
        var span = Math.Max(1, right - left - NodeWidth);
        double X(int level) => maxLevel == 0 ? right - NodeWidth : left + span * (maxLevel - level) / maxLevel;
        var top = 10d; var height = size.Height - 20;
        // One scale for every column so bands keep their width as they travel.
        var scale = double.MaxValue;
        foreach (var column in columns.Where(c => c.Count > 0))
        {
            var total = column.Sum(n => Math.Max(Size(n), 0));
            if (total <= 0) continue;
            scale = Math.Min(scale, (height - Padding * (column.Count - 1)) / total);
        }
        if (scale == double.MaxValue || scale <= 0) scale = 1;
        var byId = flow.Nodes.ToDictionary(n => n.Id);
        // Order: largest first, then two barycentre passes against the neighbouring column to reduce crossings.
        foreach (var column in columns) column.Sort((a, b) => Rank(a).CompareTo(Rank(b)) is var r && r != 0 ? r : Size(b).CompareTo(Size(a)));
        var position = new Dictionary<int, double>();
        void Place()
        {
            foreach (var column in columns)
            {
                var used = column.Sum(n => Math.Max(2, Size(n) * scale)) + Padding * Math.Max(0, column.Count - 1);
                var y = top + Math.Max(0, (height - used) / 2);
                foreach (var node in column) { var h = Math.Max(2, Size(node) * scale); position[node.Id] = y + h / 2; y += h + Padding; }
            }
        }
        Place();
        for (var pass = 0; pass < 2; pass++)
        {
            for (var level = maxLevel - 1; level >= 0; level--) Reorder(columns[level], target: false);
            for (var level = 1; level <= maxLevel; level++) Reorder(columns[level], target: true);
        }
        void Reorder(List<MemoryFlowNode> column, bool target)
        {
            double Center(MemoryFlowNode node)
            {
                var links = flow.Links.Where(l => target ? l.FromId == node.Id : l.ToId == node.Id).ToArray();
                var weight = links.Sum(Value);
                return weight <= 0 ? position[node.Id] : links.Sum(l => position[target ? l.ToId : l.FromId] * Value(l)) / weight;
            }
            var centers = column.ToDictionary(n => n.Id, Center);
            column.Sort((a, b) => Rank(a).CompareTo(Rank(b)) is var r && r != 0 ? r : centers[a.Id].CompareTo(centers[b.Id]));
            Place();
        }
        foreach (var column in columns)
            foreach (var node in column)
            {
                var h = Math.Max(2, Size(node) * scale);
                _nodeRects[node.Id] = new Rect(X(node.Level), position[node.Id] - h / 2, NodeWidth, h);
            }
        // Bands leave each owner ordered by their target's position and arrive ordered by their source's position.
        var outOffset = flow.Nodes.ToDictionary(n => n.Id, _ => 0d); var inOffset = flow.Nodes.ToDictionary(n => n.Id, _ => 0d);
        var ordered = flow.Links.Where(l => _nodeRects.ContainsKey(l.FromId) && _nodeRects.ContainsKey(l.ToId))
            .OrderBy(l => _nodeRects[l.ToId].Y).ThenBy(l => _nodeRects[l.FromId].Y).ToArray();
        var sourceY = new Dictionary<MemoryFlowLink, double>();
        foreach (var link in ordered)
        {
            var thickness = Math.Max(1, Value(link) * scale);
            sourceY[link] = _nodeRects[link.FromId].Y + outOffset[link.FromId] + thickness / 2; outOffset[link.FromId] += thickness;
        }
        foreach (var link in ordered.OrderBy(l => _nodeRects[l.FromId].Y).ThenBy(l => _nodeRects[l.ToId].Y))
        {
            var thickness = Math.Max(1, Value(link) * scale);
            var targetY = _nodeRects[link.ToId].Y + inOffset[link.ToId] + thickness / 2; inOffset[link.ToId] += thickness;
            _bands.Add(new(link, sourceY[link], targetY, thickness, _nodeRects[link.FromId].Right, _nodeRects[link.ToId].Left));
        }
        _ = byId;
    }

    /// <summary>Special buckets stay at the bottom of their column.</summary>
    private static int Rank(MemoryFlowNode node) => node.Kind switch { "Other" or "Truncated" => 2, "Unrooted" or "Hidden" => 3, _ => 0 };

    private Color NodeColor(MemoryFlowNode node) => node.Kind switch
    {
        "Target" => ChartPalette.Resource(this, "AccentBrush", Colors.SlateBlue),
        "Root" or "Static" => ChartPalette.Resource(this, "WarningBrush", Colors.Goldenrod),
        "Unrooted" => ChartPalette.Resource(this, "SuccessBrush", Colors.SeaGreen),
        "Other" or "Truncated" or "Hidden" => ChartPalette.Resource(this, "SubtleBrush", Colors.Gray),
        _ => ChartPalette.Resource(this, "MutedBrush", Colors.Gray)
    };

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(ChartPalette.Brush(this, "SurfaceBrush"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        Layout();
        var muted = ChartPalette.Resource(this, "MutedBrush", Colors.Gray);
        var text = ChartPalette.Resource(this, "TextBrush", Colors.White);
        if (Flow is not { } flow || _nodeRects.Count == 0)
        {
            dc.DrawText(ChartPalette.Text(this, Flow is null ? EmptyText : "No retention paths for this selection.", 12, muted, Math.Max(10, ActualWidth - 24)), new Point(12, 12));
            return;
        }
        var nodes = flow.Nodes.ToDictionary(n => n.Id);
        var accent = ChartPalette.Resource(this, "AccentBrush", Colors.SlateBlue);
        var tracing = _litBands.Count > 0;
        foreach (var band in _bands)
        {
            var source = nodes[band.Link.FromId];
            var lit = _litBands.Contains(band);
            var baseColor = source.Kind is "Root" or "Static" ? NodeColor(source) : muted;
            var color = lit ? ChartPalette.WithAlpha(accent, 0.55) : ChartPalette.WithAlpha(baseColor, tracing ? 0.12 : 0.32);
            dc.DrawGeometry(ChartPalette.Frozen(color), null, BandGeometry(band));
        }
        // Field names sit near the end of each band, clear of the node labels that start at the source.
        foreach (var band in _bands.Where(b => b.Thickness >= 14 && b.Link.Label.Length > 0))
        {
            var room = (band.X1 - band.X0) * 0.3;
            var label = ChartPalette.Text(this, band.Link.Label, 10, ChartPalette.WithAlpha(text, tracing && !_litBands.Contains(band) ? 0.35 : 0.8), Math.Max(10, room - 8));
            if (room < 36) continue;
            dc.DrawText(label, new Point(band.X1 - label.Width - 8, band.TargetY - label.Height / 2));
        }
        var rightmost = flow.Nodes.Min(n => n.Level);
        var lastLabelBottom = new Dictionary<int, double>();
        foreach (var node in flow.Nodes.OrderBy(n => _nodeRects.TryGetValue(n.Id, out var r) ? r.Y : 0))
        {
            if (!_nodeRects.TryGetValue(node.Id, out var rect)) continue;
            var dim = tracing && !_litNodes.Contains(node.Id);
            var color = NodeColor(node);
            dc.DrawRoundedRectangle(ChartPalette.Frozen(dim ? ChartPalette.WithAlpha(color, 0.35) : color), null, rect, 3, 3);
            if (_selectedNode == node.Id) dc.DrawRoundedRectangle(null, ChartPalette.Pen(text, 1.5), new Rect(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4), 4, 4);
            // Labels sit to the right of each node, except in the target column where they would leave the canvas.
            var isLast = node.Level == rightmost;
            var labelWidth = isLast ? LabelSpace - 20 : Math.Min(LabelSpace, Math.Max(60, (ColumnGap() - NodeWidth) * 0.68));
            var title = ChartPalette.Text(this, node.Label, 11.5, ChartPalette.WithAlpha(text, dim ? 0.45 : 1), labelWidth, bold: true);
            var metric = ChartPalette.Text(this, Metric(node), 10.5, ChartPalette.WithAlpha(muted, dim ? 0.5 : 1), labelWidth);
            var blockHeight = title.Height + metric.Height;
            var y = Math.Max(rect.Y + rect.Height / 2 - blockHeight / 2, lastLabelBottom.GetValueOrDefault(node.Level, double.MinValue) + 2);
            if (y + blockHeight > ActualHeight) continue;
            var x = rect.Right + 6;
            dc.DrawText(title, new Point(x, y)); dc.DrawText(metric, new Point(x, y + title.Height));
            lastLabelBottom[node.Level] = y + blockHeight;
        }
        if (IsKeyboardFocused) dc.DrawRectangle(null, ChartPalette.Pen(ChartPalette.WithAlpha(muted, 0.5), 1), new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1));
    }

    private double ColumnGap()
    {
        if (Flow is not { } flow || flow.Nodes.Count == 0) return LabelSpace;
        var maxLevel = flow.Nodes.Max(n => n.Level);
        return maxLevel == 0 ? LabelSpace : (ActualWidth - LabelSpace - 8 - NodeWidth) / maxLevel;
    }

    private string Metric(MemoryFlowNode node)
    {
        var count = node.Count == 1 ? "1 instance" : $"{node.Count:N0} instances";
        return UseBytes ? $"{MemorySize.Format(node.Bytes)} · {count}" : $"{count} · {MemorySize.Format(node.Bytes)}";
    }

    private static Geometry BandGeometry(Band band)
    {
        var half = band.Thickness / 2; var mid = (band.X0 + band.X1) / 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(band.X0, band.SourceY - half), true, true);
            context.BezierTo(new Point(mid, band.SourceY - half), new Point(mid, band.TargetY - half), new Point(band.X1, band.TargetY - half), true, false);
            context.LineTo(new Point(band.X1, band.TargetY + half), true, false);
            context.BezierTo(new Point(mid, band.TargetY + half), new Point(mid, band.SourceY + half), new Point(band.X0, band.SourceY + half), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private (int? Node, Band? Band) Hit(Point point)
    {
        Layout();
        foreach (var (id, rect) in _nodeRects)
            if (new Rect(rect.X - 4, rect.Y - 2, rect.Width + 8 + 120, Math.Max(rect.Height + 4, 14)).Contains(point) && point.X <= rect.Right + 120) return (id, null);
        for (var i = _bands.Count - 1; i >= 0; i--)
            if (BandGeometry(_bands[i]).FillContains(point)) return (null, _bands[i]);
        return (null, null);
    }

    private void Trace(int? node, Band? band)
    {
        _litNodes = []; _litBands = [];
        if (band is not null) _litBands.Add(band);
        foreach (var (origin, upstream) in Directions())
        {
            // Upstream toward the roots, downstream toward the target.
            var pending = new Stack<int>(); pending.Push(origin); var visited = new HashSet<int>();
            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current)) continue;
                _litNodes.Add(current);
                foreach (var b in _bands)
                    if (upstream ? b.Link.ToId == current : b.Link.FromId == current) { _litBands.Add(b); pending.Push(upstream ? b.Link.FromId : b.Link.ToId); }
            }
        }

        IEnumerable<(int Origin, bool Upstream)> Directions()
        {
            if (node is int id) { yield return (id, true); yield return (id, false); }
            if (band is not null) { yield return (band.Link.FromId, true); yield return (band.Link.ToId, false); }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (node, band) = Hit(e.GetPosition(this));
        if (node == _hoverNode && band == _hoverBand) return;
        _hoverNode = node; _hoverBand = band;
        Trace(node ?? _selectedNode, node is null ? band : null);
        if (node is int id && Flow?.Nodes.FirstOrDefault(n => n.Id == id) is { } hit)
            ToolTip = $"{hit.Label}\n{hit.Detail}\n{Metric(hit)}" + (hit.SampleObjectId is null ? "" : "\nClick to inspect the largest example" + (hit.TypeKey is not null && hit.Kind == "Owner" ? " · double-click to open this type" : ""));
        else if (band is not null)
            ToolTip = (band.Link.Label.Length > 0 ? $"Through {band.Link.Label}\n" : "") + $"{band.Link.Count:N0} instance(s) · {MemorySize.Format(band.Link.Bytes)}";
        else ToolTip = null;
        Cursor = node is not null ? Cursors.Hand : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e); _hoverNode = null; _hoverBand = null;
        Trace(_selectedNode, null); InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        var (node, _) = Hit(e.GetPosition(this));
        if (node is not int id || Flow?.Nodes.FirstOrDefault(n => n.Id == id) is not { } hit) return;
        _selectedNode = id; Trace(id, null);
        var command = e.ClickCount >= 2 ? OpenCommand : SelectCommand;
        if (command?.CanExecute(hit) == true) command.Execute(hit);
        e.Handled = true; InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SankeyPeer(this);
    private sealed class SankeyPeer(MemorySankey owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(MemorySankey);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetNameCore() => "Retention paths";
        protected override string GetHelpTextCore() => "GC roots on the left, retained objects on the right. Band width is the number of instances kept alive through each owner.";
    }
}
