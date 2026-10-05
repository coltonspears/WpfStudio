using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfStudio.App.Features.Profiling.Visuals;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

public sealed record GraphExpandRequest(int ObjectId, bool Incoming);

/// <summary>View-only rendering and interaction for the vertical retention graph. GC roots are at the top, the
/// investigated object in the middle and the objects it references below. A full minimap shows the whole graph and the
/// visible region. No heap analysis runs here.</summary>
public sealed class MemoryGraphSurface : FrameworkElement
{
    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(nameof(Graph), typeof(MemoryGraph), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(null, GraphChanged));
    public static readonly DependencyProperty InspectObjectCommandProperty = DependencyProperty.Register(nameof(InspectObjectCommand), typeof(ICommand), typeof(MemoryGraphSurface));
    public static readonly DependencyProperty ExpandCommandProperty = DependencyProperty.Register(nameof(ExpandCommand), typeof(ICommand), typeof(MemoryGraphSurface));
    public static readonly DependencyProperty SelectedReferenceProperty = DependencyProperty.Register(nameof(SelectedReference), typeof(MemoryReferenceInfo), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedObjectIdProperty = DependencyProperty.Register(nameof(SelectedObjectId), typeof(int?), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReleasedObjectsProperty = DependencyProperty.Register(nameof(ReleasedObjects), typeof(IReadOnlyList<int>), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(null, ReleasedChanged));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FocusModeProperty = DependencyProperty.Register(nameof(FocusMode), typeof(bool), typeof(MemoryGraphSurface), new PropertyMetadata(false, FocusModeChanged));
    public static readonly DependencyProperty ShowMinimapProperty = DependencyProperty.Register(nameof(ShowMinimap), typeof(bool), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly RoutedUICommand FitGraphCommand = new("Fit graph", nameof(FitGraphCommand), typeof(MemoryGraphSurface));
    public static readonly RoutedUICommand ZoomInCommand = new("Zoom in", nameof(ZoomInCommand), typeof(MemoryGraphSurface));
    public static readonly RoutedUICommand ZoomOutCommand = new("Zoom out", nameof(ZoomOutCommand), typeof(MemoryGraphSurface));
    public static readonly RoutedUICommand ResetLayoutCommand = new("Reset layout", nameof(ResetLayoutCommand), typeof(MemoryGraphSurface));
    public static readonly RoutedUICommand CenterFocusCommand = new("Center on object", nameof(CenterFocusCommand), typeof(MemoryGraphSurface));

    public MemoryGraph? Graph { get => (MemoryGraph?)GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public ICommand? InspectObjectCommand { get => (ICommand?)GetValue(InspectObjectCommandProperty); set => SetValue(InspectObjectCommandProperty, value); }
    public ICommand? ExpandCommand { get => (ICommand?)GetValue(ExpandCommandProperty); set => SetValue(ExpandCommandProperty, value); }
    public MemoryReferenceInfo? SelectedReference { get => (MemoryReferenceInfo?)GetValue(SelectedReferenceProperty); set => SetValue(SelectedReferenceProperty, value); }
    public int? SelectedObjectId { get => (int?)GetValue(SelectedObjectIdProperty); set => SetValue(SelectedObjectIdProperty, value); }
    public IReadOnlyList<int>? ReleasedObjects { get => (IReadOnlyList<int>?)GetValue(ReleasedObjectsProperty); set => SetValue(ReleasedObjectsProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool FocusMode { get => (bool)GetValue(FocusModeProperty); set => SetValue(FocusModeProperty, value); }
    public bool ShowMinimap { get => (bool)GetValue(ShowMinimapProperty); set => SetValue(ShowMinimapProperty, value); }

    private GraphLayout? _layout;
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
    private readonly List<(Rect Bounds, GraphDisplayEdge Edge)> _edgeLabels = [];
    private readonly List<(Rect Bounds, int ObjectId, bool Incoming)> _handles = [];
    private HashSet<int> _released = [];
    private Point _pan, _lastPointer;
    private GraphDisplayNode? _dragNode, _hoverNode, _keyboardNode;
    private bool _panning, _minimapDragging, _needsViewport = true;
    private int? _anchorId, _previousFocusId;
    private Point? _anchorScreen;
    private Rect _minimapBox;
    private double _minimapScale;
    private Point _minimapOrigin;

    public MemoryGraphSurface()
    {
        Focusable = true; ClipToBounds = true;
        ToolTipService.SetInitialShowDelay(this, 350);
        CommandBindings.Add(new(FitGraphCommand, (_, _) => FitGraph()));
        CommandBindings.Add(new(ZoomInCommand, (_, _) => ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1.2)));
        CommandBindings.Add(new(ZoomOutCommand, (_, _) => ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1 / 1.2)));
        CommandBindings.Add(new(ResetLayoutCommand, (_, _) => { _expandedGroups.Clear(); Rebuild(); _needsViewport = true; InitializeViewport(); }));
        CommandBindings.Add(new(CenterFocusCommand, (_, _) => CenterOn(_layout?.Focus)));
        SizeChanged += (_, e) =>
        {
            if (_needsViewport) InitializeViewport();
            else { _pan += new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2, (e.NewSize.Height - e.PreviousSize.Height) / 2); InvalidateVisual(); }
        };
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : 0, double.IsInfinity(availableSize.Height) ? 300 : 0);

    private static void FocusModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MemoryGraphSurface)d; surface._needsViewport = true;
        // Column widths change in the same binding update. Center only after layout settles.
        surface.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(surface.InitializeViewport));
    }

    private static void GraphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MemoryGraphSurface)d;
        var focus = (e.NewValue as MemoryGraph)?.Nodes.FirstOrDefault(n => n.IsFocus)?.Object.Id;
        var sameFocus = focus is not null && focus == surface._previousFocusId;
        // Expanding keeps the clicked node where it was on screen; a new object starts a fresh view.
        if (sameFocus && surface._layout is not null)
        {
            var anchor = surface._anchorId ?? focus;
            var node = surface._layout.Nodes.FirstOrDefault(n => n.ObjectId == anchor);
            surface._anchorScreen = node is null ? null : surface.Screen(node.Center);
            surface._anchorId = anchor;
        }
        else { surface._expandedGroups.Clear(); surface._anchorId = null; surface._anchorScreen = null; surface._keyboardNode = null; }
        surface._previousFocusId = focus;
        surface.Rebuild();
        if (sameFocus && surface._anchorScreen is Point screen && surface._layout?.Nodes.FirstOrDefault(n => n.ObjectId == surface._anchorId) is { } anchored)
        {
            surface._pan += screen - surface.Screen(anchored.Center);
            surface._needsViewport = false;
        }
        else { surface._needsViewport = true; surface.InitializeViewport(); }
        surface._anchorId = null;
        surface.InvalidateVisual();
    }

    private static void ReleasedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { var surface = (MemoryGraphSurface)d; surface._released = surface.ReleasedObjects?.ToHashSet() ?? []; surface.InvalidateVisual(); }

    private void Rebuild()
    {
        _hoverNode = null; _dragNode = null;
        var keyboardKey = _keyboardNode?.Key;
        try { _layout = Graph is { Nodes.Count: > 0 } graph ? GraphLayout.Build(graph, _expandedGroups, MeasureText) : null; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
        {
            // A layout defect must not take down the workbench; show the empty state instead.
            System.Diagnostics.Trace.TraceError("Memory graph layout failed: " + ex);
            _layout = null;
        }
        _keyboardNode = keyboardKey is null ? null : _layout?.Nodes.FirstOrDefault(n => n.Key == keyboardKey);
        InvalidateVisual();
    }

    private double MeasureText(string text, double size) => ChartPalette.Text(this, text, size, Colors.Black, 10_000, bold: true).WidthIncludingTrailingWhitespace;

    private void InitializeViewport()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _needsViewport = false;
        if (_layout is null) { InvalidateVisual(); return; }
        var extent = _layout.Extent;
        var fit = Math.Min((ActualWidth - 60) / Math.Max(1, extent.Width), (ActualHeight - 60) / Math.Max(1, extent.Height));
        // Show the whole graph when it stays legible; otherwise keep cards readable and center on the object.
        if (fit >= 0.55) FitGraph();
        else
        {
            Zoom = 0.8;
            CenterOn(_layout.Focus);
        }
    }

    private void CenterOn(GraphDisplayNode? node)
    {
        if (node is null || ActualWidth <= 0) return;
        _pan = new(ActualWidth / 2 - node.Center.X * Zoom, ActualHeight / 2 - node.Center.Y * Zoom);
        InvalidateVisual();
    }

    public void FitGraph()
    {
        if (_layout is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var extent = _layout.Extent; extent.Inflate(24, 24);
        Zoom = Math.Clamp(Math.Min(Math.Max(1, ActualWidth - 24) / extent.Width, Math.Max(1, ActualHeight - 24) / extent.Height), 0.08, 1);
        _pan = new((ActualWidth - extent.Width * Zoom) / 2 - extent.X * Zoom, (ActualHeight - extent.Height * Zoom) / 2 - extent.Y * Zoom);
        InvalidateVisual();
    }

    private Point Screen(Point world) => new(world.X * Zoom + _pan.X, world.Y * Zoom + _pan.Y);
    private Point World(Point screen) => new((screen.X - _pan.X) / Zoom, (screen.Y - _pan.Y) / Zoom);
    private Color C(string key, Color fallback) => ChartPalette.Resource(this, key, fallback);

    protected override void OnRender(DrawingContext dc)
    {
        var canvas = C("CanvasBrush", Color.FromRgb(0x11, 0x12, 0x15));
        dc.DrawRectangle(ChartPalette.Frozen(canvas), null, new Rect(0, 0, ActualWidth, ActualHeight));
        _edgeLabels.Clear(); _handles.Clear();
        if (_layout is null)
        {
            var muted = C("MutedBrush", Colors.Gray);
            dc.DrawText(ChartPalette.Text(this, "Select an object to see what keeps it alive", 14, C("TextBrush", Colors.White), Math.Max(20, ActualWidth - 48), bold: true), new Point(24, 24));
            dc.DrawText(ChartPalette.Text(this, "GC roots appear at the top, owners above the object, and the objects it references below.", 12, muted, Math.Max(20, ActualWidth - 48)), new Point(24, 46));
            return;
        }
        DrawDots(dc, C("CanvasDotBrush", Color.FromRgb(0x24, 0x26, 0x2D)));
        dc.PushTransform(new TranslateTransform(_pan.X, _pan.Y)); dc.PushTransform(new ScaleTransform(Zoom, Zoom));
        DrawEdges(dc);
        foreach (var node in _layout.Nodes) DrawNode(dc, node);
        dc.Pop(); dc.Pop();
        if (ShowMinimap) DrawMinimap(dc);
        if (IsKeyboardFocusWithin) dc.DrawRectangle(null, ChartPalette.Pen(C("AccentBrush", Colors.SlateBlue), 1), new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));
    }

    private void DrawDots(DrawingContext dc, Color dot)
    {
        if (Zoom < 0.45) return;
        var spacing = 24 * Zoom; var brush = ChartPalette.Frozen(dot);
        var startX = _pan.X % spacing; var startY = _pan.Y % spacing;
        var radius = Math.Max(0.6, 1.1 * Zoom);
        var count = 0;
        for (var x = startX; x < ActualWidth && count < 6000; x += spacing)
            for (var y = startY; y < ActualHeight && count < 6000; y += spacing, count++)
                dc.DrawEllipse(brush, null, new Point(x, y), radius, radius);
    }

    private void DrawEdges(DrawingContext dc)
    {
        var neutral = C("StrongBorderBrush", Colors.Gray);
        var muted = C("MutedBrush", Colors.Gray);
        var warning = C("WarningBrush", Colors.Goldenrod);
        var accent = C("AccentBrush", Colors.SlateBlue);
        var canvas = C("CanvasBrush", Colors.Black);
        foreach (var edge in _layout!.Edges)
        {
            if (edge.Route.Count < 2) continue;
            var selected = SelectedReference is { } reference && edge.References.Any(r => r.Id == reference.Id);
            var hovered = _hoverNode is not null && (edge.From == _hoverNode || edge.To == _hoverNode);
            var focusPath = edge.To == _layout.Focus || edge.From == _layout.Focus;
            var color = selected ? accent : hovered ? ChartPalette.WithAlpha(accent, 0.85)
                : edge.From.Kind is GraphNodeKind.Root or GraphNodeKind.Static ? ChartPalette.WithAlpha(warning, 0.85)
                : focusPath ? ChartPalette.Mix(neutral, muted, 0.6) : neutral;
            var thickness = (selected ? 2.6 : hovered ? 2 : 1.4) * Math.Max(1, 0.9 / Math.Max(Zoom, 0.3));
            var geometry = Smooth(edge.Route);
            dc.DrawGeometry(null, ChartPalette.Pen(color, thickness), geometry);
            var tip = edge.Route[^1]; var before = edge.Route.Count > 1 ? edge.Route[^2] : new Point(tip.X, tip.Y - 10);
            DrawArrow(dc, tip, before, color, 4 + thickness);
            if (Zoom < 0.6 || edge.IsRoot || edge.From.Kind == GraphNodeKind.Static) continue;
            var label = edge.Label;
            if (label.Length == 0) continue;
            var text = ChartPalette.Text(this, label, 10, selected ? accent : muted, 150);
            var anchor = edge.Route.Count >= 3 ? edge.Route[^2] : new Point((edge.Route[0].X + tip.X) / 2, (edge.Route[0].Y + tip.Y) / 2);
            var y = edge.Route.Count >= 3 ? (anchor.Y + tip.Y) / 2 : anchor.Y;
            var bounds = new Rect(anchor.X - text.Width / 2 - 5, y - text.Height / 2 - 1, text.Width + 10, text.Height + 2);
            dc.DrawRoundedRectangle(ChartPalette.Frozen(ChartPalette.WithAlpha(canvas, 0.92)), selected ? ChartPalette.Pen(ChartPalette.WithAlpha(accent, 0.6), 1) : null, bounds, 4, 4);
            dc.DrawText(text, new Point(bounds.X + 5, bounds.Y + 1));
            _edgeLabels.Add((bounds, edge));
        }
    }

    private static Geometry Smooth(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i]; var dy = (b.Y - a.Y) * 0.5;
                context.BezierTo(new Point(a.X, a.Y + dy), new Point(b.X, b.Y - dy), b, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static void DrawArrow(DrawingContext dc, Point tip, Point from, Color color, double size)
    {
        var direction = tip - from; if (direction.Length < 0.01) direction = new Vector(0, 1);
        // The curve arrives vertically, so aim the head mostly downward.
        direction = new Vector(direction.X * 0.15, Math.Abs(direction.Y) < 0.01 ? 1 : direction.Y); direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        var back = tip - direction * size * 1.4;
        var figure = new StreamGeometry();
        using (var context = figure.Open())
        {
            context.BeginFigure(tip, true, true);
            context.LineTo(back + normal * size * 0.75, true, false);
            context.LineTo(back - normal * size * 0.75, true, false);
        }
        figure.Freeze();
        dc.DrawGeometry(ChartPalette.Frozen(color), null, figure);
    }

    private void DrawNode(DrawingContext dc, GraphDisplayNode node)
    {
        var text = C("TextBrush", Colors.White); var muted = C("MutedBrush", Colors.Gray); var subtle = C("SubtleBrush", Colors.Gray);
        var accent = C("AccentBrush", Colors.SlateBlue); var warning = C("WarningBrush", Colors.Goldenrod); var success = C("SuccessBrush", Colors.SeaGreen);
        var raised = C("RaisedBrush", Colors.DimGray); var border = C("StrongBorderBrush", Colors.Gray);
        var r = node.Bounds;
        var hovered = node == _hoverNode; var keyboard = node == _keyboardNode || node.ObjectId is int id && id == SelectedObjectId;
        if (node.Kind is GraphNodeKind.Root or GraphNodeKind.Static)
        {
            var fill = C("WarningSoftBrush", Color.FromRgb(0x3A, 0x32, 0x23));
            dc.DrawRoundedRectangle(ChartPalette.Frozen(fill), ChartPalette.Pen(hovered ? text : warning, hovered ? 1.6 : 1.2), r, r.Height / 2, r.Height / 2);
            dc.DrawText(ChartPalette.Text(this, node.Caption, 9.5, warning, r.Width - 30), new Point(r.X + 16, r.Y + 5));
            dc.DrawText(ChartPalette.Text(this, node.Title, 11.5, text, r.Width - 30, bold: true), new Point(r.X + 16, r.Y + 17));
            return;
        }
        var released = node.Kind == GraphNodeKind.Group ? node.Members.Any(m => _released.Contains(m.Object.Id)) : node.ObjectId is int rid && _released.Contains(rid);
        var isFocus = node == _layout!.Focus;
        var isRootTarget = node.Node?.IsRootTarget == true;
        var stroke = released ? success : isFocus ? accent : keyboard || hovered ? ChartPalette.Mix(border, text, 0.55) : border;
        var strokeWidth = isFocus ? 2.2 : released || keyboard ? 1.6 : 1;
        if (node.Kind == GraphNodeKind.Group)
            for (var layer = 2; layer >= 1; layer--)
                dc.DrawRoundedRectangle(ChartPalette.Frozen(ChartPalette.Mix(raised, C("CanvasBrush", Colors.Black), 0.25 * layer)), ChartPalette.Pen(border, 1),
                    new Rect(r.X + 5 * layer, r.Y + 5 * layer, r.Width, r.Height), 8, 8);
        var cardFill = isFocus ? ChartPalette.Mix(raised, accent, 0.12) : raised;
        dc.DrawRoundedRectangle(ChartPalette.Frozen(cardFill), ChartPalette.Pen(stroke, strokeWidth), r, 8, 8);
        var dot = released ? success : isFocus ? accent : isRootTarget ? warning : subtle;
        dc.DrawEllipse(ChartPalette.Frozen(dot), null, new Point(r.X + 13, r.Y + 15), 4, 4);
        var info = node.Node?.Object;
        var tag = node.Kind == GraphNodeKind.Group ? "group" : info is null ? "" : GenerationTag(info.Generation) + (info.IsPinned ? " · pinned" : "");
        var tagText = ChartPalette.Text(this, tag, 9.5, subtle, 80);
        dc.DrawText(tagText, new Point(r.Right - tagText.Width - 10, r.Y + 9));
        dc.DrawText(ChartPalette.Text(this, node.Title, 12, text, r.Width - 36 - tagText.Width, bold: true), new Point(r.X + 24, r.Y + 7));
        dc.DrawText(ChartPalette.Text(this, node.Subtitle, 10, subtle, r.Width - 34), new Point(r.X + 24, r.Y + 24));
        long own, retained;
        if (node.Kind == GraphNodeKind.Group) { own = node.Members.Sum(m => m.Object.ShallowBytes); retained = node.Members.Sum(m => m.Object.RetainedBytes); }
        else { own = info!.ShallowBytes; retained = info.RetainedBytes; }
        var metrics = ChartPalette.Text(this, $"Own {MemorySize.Format(own)}  ·  Retains {MemorySize.Format(retained)}", 10.5,
            isFocus ? accent : released ? success : muted, r.Width - 24);
        dc.DrawText(metrics, new Point(r.X + 12, r.Y + 40));
        var max = Math.Max(1, _layout.Nodes.Where(n => n.Kind != GraphNodeKind.Root && n.Kind != GraphNodeKind.Static)
            .Max(n => n.Kind == GraphNodeKind.Group ? n.Members.Sum(m => m.Object.RetainedBytes) : n.Node?.Object.RetainedBytes ?? 0));
        var track = new Rect(r.X + 12, r.Bottom - 9, r.Width - 24, 3);
        dc.DrawRoundedRectangle(ChartPalette.Frozen(C("BorderBrush", Colors.DimGray)), null, track, 1.5, 1.5);
        var ratio = Math.Clamp((double)retained / max, 0, 1);
        if (ratio > 0)
            dc.DrawRoundedRectangle(ChartPalette.Frozen(ChartPalette.WithAlpha(released ? success : accent, isFocus ? 0.95 : 0.6)), null,
                new Rect(track.X, track.Y, Math.Max(3, track.Width * ratio), track.Height), 1.5, 1.5);
        if (node.Kind == GraphNodeKind.Object && node.ObjectId is int objectId)
        {
            if (node.HiddenIncoming > 0) DrawHandle(dc, new Point(r.X + r.Width / 2, r.Y), $"+{node.HiddenIncoming:N0} owners", objectId, true);
            if (node.HiddenOutgoing > 0) DrawHandle(dc, new Point(r.X + r.Width / 2, r.Bottom), $"+{node.HiddenOutgoing:N0}", objectId, false);
        }
    }

    private void DrawHandle(DrawingContext dc, Point center, string label, int objectId, bool incoming)
    {
        if (Zoom < 0.45) return;
        var text = ChartPalette.Text(this, label, 9.5, C("TextBrush", Colors.White), 120);
        var bounds = new Rect(center.X - text.Width / 2 - 7, center.Y - 8, text.Width + 14, 16);
        var hover = IsMouseOver && bounds.Contains(World(Mouse.GetPosition(this)));
        dc.DrawRoundedRectangle(ChartPalette.Frozen(hover ? C("AccentFillBrush", Colors.SlateBlue) : C("SurfaceBrush", Colors.Black)),
            ChartPalette.Pen(hover ? C("AccentFillBrush", Colors.SlateBlue) : C("StrongBorderBrush", Colors.Gray), 1), bounds, 8, 8);
        dc.DrawText(hover ? ChartPalette.Text(this, label, 9.5, Colors.White, 120) : text, new Point(bounds.X + 7, bounds.Y + 1.5));
        _handles.Add((bounds, objectId, incoming));
    }

    private static string GenerationTag(string generation) => generation switch
    {
        "Generation0" => "Gen0", "Generation1" => "Gen1", "Generation2" => "Gen2", "Large" => "LOH", "Pinned" => "POH", "Frozen" => "Frozen", _ => generation
    };

    private void DrawMinimap(DrawingContext dc)
    {
        if (_layout is null || ActualWidth < 360 || ActualHeight < 220) { _minimapBox = Rect.Empty; return; }
        var extent = _layout.Extent; extent.Inflate(30, 30);
        // When the whole graph is already on screen the minimap adds nothing and would cover nodes.
        var visible = new Rect(World(new Point(0, 0)), World(new Point(ActualWidth, ActualHeight)));
        if (visible.Contains(_layout.Extent)) { _minimapBox = Rect.Empty; return; }
        var maxW = Math.Min(220, ActualWidth * 0.3); var maxH = Math.Min(160, ActualHeight * 0.35);
        _minimapScale = Math.Min(maxW / extent.Width, maxH / extent.Height);
        var w = extent.Width * _minimapScale; var h = extent.Height * _minimapScale;
        _minimapBox = new Rect(ActualWidth - w - 14, ActualHeight - h - 14, w, h);
        _minimapOrigin = extent.TopLeft;
        var surface = C("SurfaceBrush", Colors.Black);
        dc.DrawRoundedRectangle(ChartPalette.Frozen(ChartPalette.WithAlpha(surface, 0.94)), ChartPalette.Pen(C("StrongBorderBrush", Colors.Gray), 1),
            new Rect(_minimapBox.X - 4, _minimapBox.Y - 4, w + 8, h + 8), 6, 6);
        dc.PushClip(new RectangleGeometry(_minimapBox));
        var edgePen = ChartPalette.Pen(ChartPalette.WithAlpha(C("MutedBrush", Colors.Gray), 0.45), 0.8);
        foreach (var edge in _layout.Edges)
            for (var i = 1; i < edge.Route.Count; i++) dc.DrawLine(edgePen, Mini(edge.Route[i - 1]), Mini(edge.Route[i]));
        foreach (var node in _layout.Nodes)
        {
            var color = node == _layout.Focus ? C("AccentBrush", Colors.SlateBlue)
                : node.Kind is GraphNodeKind.Root or GraphNodeKind.Static ? C("WarningBrush", Colors.Goldenrod)
                : node.ObjectId is int id && _released.Contains(id) ? C("SuccessBrush", Colors.SeaGreen)
                : ChartPalette.WithAlpha(C("MutedBrush", Colors.Gray), 0.75);
            var a = Mini(node.Position); var b = Mini(new Point(node.Position.X + node.Size.Width, node.Position.Y + node.Size.Height));
            dc.DrawRoundedRectangle(ChartPalette.Frozen(color), null, new Rect(a, b), 1.5, 1.5);
        }
        var view = new Rect(Mini(World(new Point(0, 0))), Mini(World(new Point(ActualWidth, ActualHeight))));
        var accent = C("AccentBrush", Colors.SlateBlue);
        dc.DrawRectangle(ChartPalette.Frozen(ChartPalette.WithAlpha(accent, 0.12)), ChartPalette.Pen(accent, 1.4), view);
        dc.Pop();
    }

    private Point Mini(Point world) => new(_minimapBox.X + (world.X - _minimapOrigin.X) * _minimapScale, _minimapBox.Y + (world.Y - _minimapOrigin.Y) * _minimapScale);
    private void NavigateMinimap(Point screen)
    {
        if (_minimapScale <= 0) return;
        var world = new Point(_minimapOrigin.X + (screen.X - _minimapBox.X) / _minimapScale, _minimapOrigin.Y + (screen.Y - _minimapBox.Y) / _minimapScale);
        _pan = new(ActualWidth / 2 - world.X * Zoom, ActualHeight / 2 - world.Y * Zoom);
        InvalidateVisual();
    }

    private GraphDisplayNode? HitNode(Point world) => _layout?.Nodes.LastOrDefault(n => n.Bounds.Contains(world));

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        var point = e.GetPosition(this); var world = World(point); _lastPointer = point;
        if (e.ChangedButton == MouseButton.Left)
        {
            if (ShowMinimap && _minimapBox.Contains(point)) { _minimapDragging = true; NavigateMinimap(point); CaptureMouse(); e.Handled = true; return; }
            if (_handles.LastOrDefault(h => h.Bounds.Contains(world)) is { Bounds.Width: > 0 } handle)
            {
                _anchorId = handle.ObjectId;
                var request = new GraphExpandRequest(handle.ObjectId, handle.Incoming);
                if (ExpandCommand?.CanExecute(request) == true) ExpandCommand.Execute(request);
                e.Handled = true; return;
            }
            if (HitNode(world) is { } node)
            {
                _keyboardNode = node;
                if (node.ObjectId is int id) SetCurrentValue(SelectedObjectIdProperty, id);
                if (e.ClickCount == 2) { Activate(node); e.Handled = true; return; }
                _dragNode = node;
            }
            else if (_edgeLabels.LastOrDefault(x => x.Bounds.Contains(world)).Edge is { } edge)
            {
                SetCurrentValue(SelectedReferenceProperty, edge.References[0]);
                InvalidateVisual(); e.Handled = true; return;
            }
            else _panning = true;
        }
        else if (e.ChangedButton is MouseButton.Middle or MouseButton.Right) { _panning = true; _rightDown = point; }
        else return;
        CaptureMouse(); e.Handled = true; InvalidateVisual();
    }

    private void Activate(GraphDisplayNode node)
    {
        if (node.Kind == GraphNodeKind.Group)
        {
            _anchorId = node.Members[0].Object.Id;
            var screen = Screen(node.Center);
            _expandedGroups.Add(node.Key); Rebuild();
            if (_layout?.Nodes.FirstOrDefault(n => n.ObjectId == _anchorId) is { } member) _pan += screen - Screen(member.Center);
            _anchorId = null; InvalidateVisual();
            return;
        }
        if (node.ObjectId is int id && InspectObjectCommand?.CanExecute(id) == true) InspectObjectCommand.Execute(id);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this); var delta = point - _lastPointer; _lastPointer = point;
        if (_minimapDragging) { NavigateMinimap(point); return; }
        if (_dragNode is { } dragged) { MoveNode(dragged, delta / Zoom); InvalidateVisual(); return; }
        if (_panning) { _pan += delta; InvalidateVisual(); return; }
        var world = World(point);
        var hover = HitNode(world);
        var overHandle = _handles.Any(h => h.Bounds.Contains(world));
        if (hover != _hoverNode || overHandle) { _hoverNode = hover; InvalidateVisual(); }
        Cursor = overHandle || hover is not null || _edgeLabels.Any(x => x.Bounds.Contains(world)) ? Cursors.Hand : null;
        ToolTip = hover is null ? (overHandle ? "Show more neighbours of this object" : null) : Describe(hover);
    }

    private static string Describe(GraphDisplayNode node) => node.Kind switch
    {
        GraphNodeKind.Root => $"GC root: {node.Title}\nThe runtime keeps everything below this root alive.",
        GraphNodeKind.Static => $"Static field {node.Subtitle}\nStatic fields live as long as their type is loaded, which is usually the whole process.",
        GraphNodeKind.Group => $"{node.Title}\n{node.Members.Count:N0} objects of the same type with the same owner.\nDouble-click to expand.",
        _ => $"{node.Node!.Object.Type}\n{node.Node.Object.Address} · {node.Node.Object.Generation}\nOwn {node.Node.Object.ShallowBytes:N0} bytes · retains {node.Node.Object.RetainedBytes:N0} bytes" +
             $"\n{node.Node.IncomingCount:N0} incoming · {node.Node.OutgoingCount:N0} outgoing references\nDouble-click to inspect · drag to move"
    };

    private void MoveNode(GraphDisplayNode node, Vector delta)
    {
        node.Position += delta;
        foreach (var edge in _layout!.Edges)
        {
            if (edge.Route.Count < 2) continue;
            if (edge.From == node) edge.Route[0] = new Point(node.Center.X, node.Position.Y + node.Size.Height);
            if (edge.To == node) edge.Route[^1] = new Point(node.Center.X, node.Position.Y);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Right)
        {
            // A right-click without dragging opens the object menu; a right-drag only pans.
            var point = e.GetPosition(this);
            var node = (point - _rightDown).Length > 4 ? null : HitNode(World(point));
            if (node?.Node is { } target) MemoryMenus.SetTarget(this, target.Object);
            else { MemoryMenus.SetTarget(this, null); e.Handled = true; }
        }
        _dragNode = null; _panning = false; _minimapDragging = false; ReleaseMouseCapture();
    }
    private Point _rightDown;
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _dragNode = null; _panning = false; _minimapDragging = false; }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (_hoverNode is not null) { _hoverNode = null; InvalidateVisual(); } }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { base.OnMouseWheel(e); ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true; }

    private void ZoomAt(Point point, double factor)
    { var world = World(point); Zoom = Math.Clamp(Zoom * factor, 0.08, 2.5); _pan = new(point.X - world.X * Zoom, point.Y - world.Y * Zoom); InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { FitGraph(); e.Handled = true; return; }
        if (e.Key is Key.Add or Key.OemPlus) { ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1.2); e.Handled = true; return; }
        if (e.Key is Key.Subtract or Key.OemMinus) { ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1 / 1.2); e.Handled = true; return; }
        if (e.Key == Key.M) { SetCurrentValue(ShowMinimapProperty, !ShowMinimap); e.Handled = true; return; }
        if (_layout is not { Nodes.Count: > 0 } layout) return;
        var current = _keyboardNode ?? layout.Focus ?? layout.Nodes[0];
        if (e.Key is Key.Enter or Key.Space) { Activate(current); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var direction = e.Key switch { Key.Left => new Vector(-1, 0), Key.Right => new Vector(1, 0), Key.Up => new Vector(0, -1), _ => new Vector(0, 1) };
        // Nearest node in the pressed direction, favouring straight lines.
        var best = layout.Nodes.Where(n => n != current).Select(n => (Node: n, Offset: n.Center - current.Center))
            .Where(x => Vector.Multiply(x.Offset, direction) > 1)
            .OrderBy(x => x.Offset.Length + Math.Abs(Vector.CrossProduct(x.Offset, direction)) * 2).Select(x => x.Node).FirstOrDefault();
        if (best is null) return;
        _keyboardNode = best;
        if (best.ObjectId is int id) SetCurrentValue(SelectedObjectIdProperty, id);
        var screen = Screen(best.Center);
        if (screen.X < 40 || screen.Y < 40 || screen.X > ActualWidth - 40 || screen.Y > ActualHeight - 40) CenterOn(best);
        InvalidateVisual(); e.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GraphPeer(this);
    private sealed class GraphPeer(MemoryGraphSurface owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(MemoryGraphSurface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetNameCore() => "Object retention graph";
        protected override string GetHelpTextCore() => "Arrow keys move between objects. Enter inspects an object or expands a group. Plus and minus zoom, Control+0 fits, M toggles the minimap. Drag to pan or move objects.";
    }
}
