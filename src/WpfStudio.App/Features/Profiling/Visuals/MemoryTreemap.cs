using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfStudio.App.Features.Profiling.Visuals;

/// <summary>One rectangle. Items with children draw as a labelled group containing their children.</summary>
public sealed record TreemapItem(string Key, string Label, string Detail, double Value, int ColorIndex, string ValueText,
    IReadOnlyList<TreemapItem>? Children = null, object? Tag = null);

/// <summary>Squarified treemap (Bruls, Huizing and van Wijk) with one level of grouping. Click selects, double-click
/// opens. Area encodes the value; hue encodes the group, in the fixed categorical order.</summary>
public sealed class MemoryTreemap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<TreemapItem>), typeof(MemoryTreemap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MemoryTreemap)d).Invalidate()));
    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(nameof(SelectedKey), typeof(string), typeof(MemoryTreemap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(nameof(SelectCommand), typeof(ICommand), typeof(MemoryTreemap));
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(MemoryTreemap));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(MemoryTreemap), new FrameworkPropertyMetadata("Nothing to show", FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<TreemapItem>? Items { get => (IReadOnlyList<TreemapItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public string? SelectedKey { get => (string?)GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }
    public ICommand? SelectCommand { get => (ICommand?)GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value); }
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private const double Gap = 2, Header = 18;
    private readonly List<Cell> _cells = [];
    private Size _laidOutFor;
    private Cell? _hover;

    private sealed record Cell(TreemapItem Item, Rect Bounds, bool IsGroup, Color Fill, int Depth);

    public MemoryTreemap()
    {
        Focusable = true; ClipToBounds = true; SnapsToDevicePixels = true;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetBetweenShowDelay(this, 0);
    }

    private void Invalidate() { _laidOutFor = default; _hover = null; InvalidateVisual(); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 320 : 0, double.IsInfinity(availableSize.Height) ? 220 : 0);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); _laidOutFor = default; }

    private void Layout()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size == _laidOutFor) return;
        _laidOutFor = size; _cells.Clear();
        if (Items is not { Count: > 0 } items || size.Width < 4 || size.Height < 4) return;
        var surface = ChartPalette.Resource(this, "SurfaceBrush", Colors.Black);
        var top = items.Where(i => i.Value > 0).OrderByDescending(i => i.Value).ToArray();
        foreach (var (item, rect) in Squarify(top, new Rect(0, 0, size.Width, size.Height)))
        {
            var hue = ChartPalette.Series(this, item.ColorIndex);
            var inner = Deflate(rect, Gap / 2);
            if (inner.Width < 1 || inner.Height < 1) continue;
            if (item.Children is { Count: > 0 } children && inner.Width > 28 && inner.Height > Header + 12)
            {
                _cells.Add(new(item, inner, true, ChartPalette.Mix(hue, surface, 0.72), 0));
                var body = new Rect(inner.X + 1, inner.Y + Header, Math.Max(0, inner.Width - 2), Math.Max(0, inner.Height - Header - 1));
                var ordered = children.Where(c => c.Value > 0).OrderByDescending(c => c.Value).ToArray();
                var rank = 0;
                foreach (var (child, childRect) in Squarify(ordered, body))
                {
                    var cell = Deflate(childRect, Gap / 2);
                    if (cell.Width < 1 || cell.Height < 1) continue;
                    // Same hue for the group; larger members slightly stronger so the biggest reads first.
                    var shade = 0.18 + Math.Min(0.32, rank++ * 0.035);
                    _cells.Add(new(child, cell, false, ChartPalette.Mix(hue, surface, shade), 1));
                }
            }
            else _cells.Add(new(item, inner, false, ChartPalette.Mix(hue, surface, 0.2), 0));
        }
    }

    private static Rect Deflate(Rect rect, double amount) =>
        new(rect.X + amount, rect.Y + amount, Math.Max(0, rect.Width - amount * 2), Math.Max(0, rect.Height - amount * 2));

    /// <summary>Lays items (sorted largest first) into rows along the shorter side, keeping aspect ratios near 1.</summary>
    internal static List<(TreemapItem Item, Rect Bounds)> Squarify(IReadOnlyList<TreemapItem> items, Rect bounds)
    {
        var result = new List<(TreemapItem, Rect)>();
        var total = items.Sum(i => i.Value);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;
        var scale = bounds.Width * bounds.Height / total;
        var rect = bounds; var index = 0;
        while (index < items.Count)
        {
            var side = Math.Min(rect.Width, rect.Height);
            if (side <= 0) break;
            var row = new List<TreemapItem>(); double rowArea = 0; var worst = double.MaxValue;
            while (index < items.Count)
            {
                var area = items[index].Value * scale;
                var candidate = Worst(row.Select(r => r.Value * scale).Append(area), rowArea + area, side);
                if (row.Count > 0 && candidate > worst) break;
                row.Add(items[index]); rowArea += area; worst = candidate; index++;
            }
            var thickness = rowArea / side;
            var horizontal = rect.Width >= rect.Height; // Row runs along the short (vertical) side when the box is wide.
            var offset = 0d;
            foreach (var item in row)
            {
                var length = item.Value * scale / Math.Max(thickness, 1e-9);
                var cell = horizontal ? new Rect(rect.X, rect.Y + offset, thickness, length) : new Rect(rect.X + offset, rect.Y, length, thickness);
                result.Add((item, cell)); offset += length;
            }
            rect = horizontal ? new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height)
                : new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
        }
        return result;

        static double Worst(IEnumerable<double> areas, double sum, double side)
        {
            double max = 0, min = double.MaxValue;
            foreach (var a in areas) { max = Math.Max(max, a); min = Math.Min(min, a); }
            var s2 = sum * sum; var w2 = side * side;
            return Math.Max(w2 * max / s2, s2 / (w2 * Math.Max(min, 1e-9)));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(ChartPalette.Brush(this, "SurfaceBrush"), null, bounds);
        Layout();
        if (_cells.Count == 0)
        {
            var empty = ChartPalette.Text(this, EmptyText, 12, ChartPalette.Resource(this, "MutedBrush", Colors.Gray), Math.Max(10, ActualWidth - 24));
            dc.DrawText(empty, new Point(12, 12));
            return;
        }
        var text = ChartPalette.Resource(this, "TextBrush", Colors.White);
        var muted = ChartPalette.Resource(this, "MutedBrush", Colors.Gray);
        foreach (var cell in _cells)
        {
            var r = cell.Bounds; var radius = cell.IsGroup ? 6 : 4;
            dc.DrawRoundedRectangle(ChartPalette.Frozen(cell.Fill), null, r, Math.Min(radius, r.Width / 2), Math.Min(radius, r.Height / 2));
            var ink = ChartPalette.ReadableText(cell.Fill);
            if (cell.IsGroup)
            {
                if (r.Width > 40)
                {
                    var value = ChartPalette.Text(this, cell.Item.ValueText, 10.5, ChartPalette.WithAlpha(ink, 0.75), Math.Max(10, r.Width * 0.4));
                    var label = ChartPalette.Text(this, cell.Item.Label, 11, ink, Math.Max(10, r.Width - value.Width - 18), bold: true);
                    dc.DrawText(label, new Point(r.X + 6, r.Y + 2));
                    if (r.Width > label.Width + value.Width + 20) dc.DrawText(value, new Point(r.Right - value.Width - 6, r.Y + 2.5));
                }
                continue;
            }
            if (r.Width > 34 && r.Height > 16)
            {
                var label = ChartPalette.Text(this, cell.Item.Label, 11, ink, r.Width - 10, bold: true);
                dc.DrawText(label, new Point(r.X + 5, r.Y + 3));
                if (r.Height > 32)
                {
                    var value = ChartPalette.Text(this, cell.Item.ValueText, 10.5, ChartPalette.WithAlpha(ink, 0.78), r.Width - 10);
                    dc.DrawText(value, new Point(r.X + 5, r.Y + 18));
                }
            }
        }
        var selected = _cells.FirstOrDefault(c => c.Item.Key == SelectedKey);
        if (selected is not null)
            dc.DrawRoundedRectangle(null, ChartPalette.Pen(ChartPalette.Resource(this, "AccentBrush", Colors.SlateBlue), 2), Deflate(selected.Bounds, 1), 4, 4);
        if (_hover is not null && _hover != selected)
            dc.DrawRoundedRectangle(null, ChartPalette.Pen(ChartPalette.WithAlpha(text, 0.85), 1.5), Deflate(_hover.Bounds, 0.75), 4, 4);
        if (IsKeyboardFocused) dc.DrawRectangle(null, ChartPalette.Pen(ChartPalette.WithAlpha(muted, 0.5), 1), Deflate(bounds, 0.5));
    }

    private Cell? Hit(Point point)
    {
        Layout();
        return _cells.LastOrDefault(c => !c.IsGroup && c.Bounds.Contains(point)) ?? _cells.LastOrDefault(c => c.Bounds.Contains(point));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = Hit(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        ToolTip = hit is null ? null : $"{hit.Item.Label}\n{hit.Item.Detail}\n{hit.Item.ValueText}" + (OpenCommand is null ? "" : "\nDouble-click to open");
        Cursor = hit is null ? null : Cursors.Hand;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = null; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        if (Hit(e.GetPosition(this)) is not { } hit) return;
        SetCurrentValue(SelectedKeyProperty, hit.Item.Key);
        var command = e.ClickCount >= 2 ? OpenCommand : SelectCommand;
        if (command?.CanExecute(hit.Item) == true) command.Execute(hit.Item);
        e.Handled = true; InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Layout();
        var leaves = _cells.Where(c => !c.IsGroup).ToList();
        if (leaves.Count == 0) return;
        var index = leaves.FindIndex(c => c.Item.Key == SelectedKey);
        if (e.Key is Key.Right or Key.Down) index = Math.Min(leaves.Count - 1, index + 1);
        else if (e.Key is Key.Left or Key.Up) index = Math.Max(0, index - 1);
        else if (e.Key == Key.Enter && index >= 0) { if (OpenCommand?.CanExecute(leaves[index].Item) == true) OpenCommand.Execute(leaves[index].Item); e.Handled = true; return; }
        else return;
        SetCurrentValue(SelectedKeyProperty, leaves[Math.Max(0, index)].Item.Key);
        if (SelectCommand?.CanExecute(leaves[Math.Max(0, index)].Item) == true) SelectCommand.Execute(leaves[Math.Max(0, index)].Item);
        e.Handled = true; InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreemapPeer(this);
    private sealed class TreemapPeer(MemoryTreemap owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(MemoryTreemap);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetHelpTextCore() => "Area shows size. Arrow keys move between rectangles, Enter opens the selected one.";
    }
}
