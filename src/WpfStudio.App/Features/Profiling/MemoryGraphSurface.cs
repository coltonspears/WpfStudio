using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>View-only rendering and interaction for a bounded object graph. No heap analysis runs here.</summary>
public sealed class MemoryGraphSurface : FrameworkElement
{
    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(nameof(Graph), typeof(MemoryGraph), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(null, GraphChanged));
    public static readonly DependencyProperty InspectObjectCommandProperty = DependencyProperty.Register(nameof(InspectObjectCommand), typeof(ICommand), typeof(MemoryGraphSurface));
    public static readonly DependencyProperty SelectedReferenceProperty = DependencyProperty.Register(nameof(SelectedReference), typeof(MemoryReferenceInfo), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReleasedObjectsProperty = DependencyProperty.Register(nameof(ReleasedObjects), typeof(IReadOnlyList<int>), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(null, ReleasedChanged));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(MemoryGraphSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FocusModeProperty = DependencyProperty.Register(nameof(FocusMode), typeof(bool), typeof(MemoryGraphSurface), new PropertyMetadata(false, FocusModeChanged));
    public static readonly RoutedUICommand FitGraphCommand = new("Fit graph", nameof(FitGraphCommand), typeof(MemoryGraphSurface));
    public static readonly RoutedUICommand ZoomInCommand = new("Zoom in", nameof(ZoomInCommand), typeof(MemoryGraphSurface));
    public static readonly RoutedUICommand ZoomOutCommand = new("Zoom out", nameof(ZoomOutCommand), typeof(MemoryGraphSurface));
    private readonly Dictionary<int, Point> _positions = [];
    private readonly List<(MemoryReferenceInfo Reference, Rect Bounds)> _edgeLabels = [];
    private HashSet<int> _released = [];
    private Point _pan, _lastPointer;
    private int? _dragNode, _keyboardNode;
    private bool _panning, _fitOnResize;
    private const double NodeWidth = 276, NodeHeight = 88;
    public MemoryGraph? Graph { get => (MemoryGraph?)GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public ICommand? InspectObjectCommand { get => (ICommand?)GetValue(InspectObjectCommandProperty); set => SetValue(InspectObjectCommandProperty, value); }
    public MemoryReferenceInfo? SelectedReference { get => (MemoryReferenceInfo?)GetValue(SelectedReferenceProperty); set => SetValue(SelectedReferenceProperty, value); }
    public IReadOnlyList<int>? ReleasedObjects { get => (IReadOnlyList<int>?)GetValue(ReleasedObjectsProperty); set => SetValue(ReleasedObjectsProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool FocusMode { get => (bool)GetValue(FocusModeProperty); set => SetValue(FocusModeProperty, value); }

    public MemoryGraphSurface()
    {
        Focusable = true; ClipToBounds = true;
        CommandBindings.Add(new(FitGraphCommand, (_, _) => FitGraph()));
        CommandBindings.Add(new(ZoomInCommand, (_, _) => ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1.2)));
        CommandBindings.Add(new(ZoomOutCommand, (_, _) => ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1 / 1.2)));
        SizeChanged += (_, e) =>
        {
            if (_fitOnResize || FocusMode) InitializeViewport();
            else { _pan += new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2, (e.NewSize.Height - e.PreviousSize.Height) / 2); InvalidateVisual(); }
        };
    }
    private static void FocusModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MemoryGraphSurface)d; surface._fitOnResize = true;
        // The column styles and overview visibility change in the same binding update.
        // Center only after their layout pass has established the final graph viewport.
        surface.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(surface.InitializeViewport));
    }
    private static void GraphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MemoryGraphSurface)d; surface._positions.Clear(); surface._keyboardNode = null;
        if (surface.Graph is { } graph)
            foreach (var group in graph.Nodes.GroupBy(n => n.Column))
            {
                var ordered = group.OrderByDescending(n => n.IsFocus).ThenByDescending(n => n.IsRootTarget).ThenByDescending(n => n.Object.RetainedBytes).ToArray();
                for (var i = 0; i < ordered.Length; i++) surface._positions[ordered[i].Object.Id] = new(group.Key * 356, (i - (ordered.Length - 1) / 2d) * 120);
            }
        surface._fitOnResize = true; surface.InitializeViewport(); surface.InvalidateVisual();
    }
    private static void ReleasedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { var surface = (MemoryGraphSurface)d; surface._released = surface.ReleasedObjects?.ToHashSet() ?? []; surface.InvalidateVisual(); }
    private void InitializeViewport()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _fitOnResize = false;
        if (_positions.Count <= 18 && !FocusMode) FitGraph();
        else
        {
            Zoom = 0.85;
            var focus = Graph?.Nodes.FirstOrDefault(n => n.IsFocus);
            var position = focus is null ? new Point() : _positions[focus.Object.Id];
            _pan = new(ActualWidth / 2 - (position.X + NodeWidth / 2) * Zoom, ActualHeight / 2 - (position.Y + NodeHeight / 2) * Zoom);
        }
    }
    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brush("CanvasBrush"), null, new(0, 0, ActualWidth, ActualHeight));
        if (Graph is not { } graph || _positions.Count == 0)
        {
            Text(dc, "Select an object to explore its relationships", new(24, 24), Brush("MutedBrush"), 13, Math.Max(20, ActualWidth - 48));
            return;
        }
        _edgeLabels.Clear();
        dc.PushTransform(new TranslateTransform(_pan.X, _pan.Y)); dc.PushTransform(new ScaleTransform(Zoom, Zoom));
        foreach (var reference in graph.References)
        {
            if (!_positions.TryGetValue(reference.ToId, out var target)) continue;
            var to = new Point(target.X, target.Y + NodeHeight / 2);
            var from = reference.FromId is int id && _positions.TryGetValue(id, out var source)
                ? new Point(source.X + NodeWidth, source.Y + NodeHeight / 2) : new Point(to.X - 84, to.Y);
            var color = SelectedReference?.Id == reference.Id ? Brush("AccentBrush") : reference.IsRoot ? Brush("WarningBrush") : Brush("StrongBorderBrush");
            var delta = Math.Max(48, Math.Abs(to.X - from.X) / 2);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(from, false, false);
                context.BezierTo(new(from.X + delta, from.Y), new(to.X - delta, to.Y), to, true, false);
            }
            dc.DrawGeometry(null, new(color, SelectedReference?.Id == reference.Id ? 2.5 : 1.3), geometry);
            dc.DrawLine(new(color, 1.5), new(to.X - 7, to.Y - 4), to);
            dc.DrawLine(new(color, 1.5), new(to.X - 7, to.Y + 4), to);
            if (Zoom >= 0.55 && !reference.IsRoot)
            {
                var bounds = new Rect((from.X + to.X) / 2 - 76, (from.Y + to.Y) / 2 - 18, 152, 18);
                dc.DrawRoundedRectangle(Brush("CanvasBrush"), null, bounds, 3, 3);
                Text(dc, reference.Label, new(bounds.X + 3, bounds.Y + 1), SelectedReference?.Id == reference.Id ? color : Brush("MutedBrush"), 10, 146);
                _edgeLabels.Add((reference, bounds));
            }
        }
        foreach (var node in graph.Nodes)
        {
            var p = _positions[node.Object.Id]; var bounds = new Rect(p, new Size(NodeWidth, NodeHeight));
            var active = node.IsFocus || _keyboardNode == node.Object.Id;
            var color = _released.Contains(node.Object.Id) ? Brush("SuccessBrush") : active ? Brush("AccentBrush") : node.IsRootTarget ? Brush("WarningBrush") : Brush("StrongBorderBrush");
            dc.DrawRoundedRectangle(Brush(active ? "AccentSoftBrush" : "RaisedBrush"), new(color, active ? 2 : 1), bounds, 7, 7);
            dc.DrawRoundedRectangle(color, null, new Rect(p.X, p.Y + 10, 3, NodeHeight - 20), 1, 1);
            Text(dc, ShortType(node.Object.Type), new(p.X + 13, p.Y + 10), Brush("TextBrush"), 12, NodeWidth - 24, true);
            Text(dc, node.Object.Address + (node.IsRootTarget ? " · GC root target" : ""), new(p.X + 13, p.Y + 31), Brush("MutedBrush"), 10, NodeWidth - 24);
            Text(dc, $"Own {MemorySize.Format(node.Object.ShallowBytes)}    Retains {MemorySize.Format(node.Object.RetainedBytes)}", new(p.X + 13, p.Y + 52),
                active || node.IsRootTarget || _released.Contains(node.Object.Id) ? color : Brush("MutedBrush"), 11, NodeWidth - 24);
            if (_released.Contains(node.Object.Id)) Text(dc, "Eligible after modeled removal", new(p.X + 13, p.Y + 69), color, 9, NodeWidth - 24);
        }
        dc.Pop(); dc.Pop();
        if (IsKeyboardFocusWithin) dc.DrawRectangle(null, new(Brush("AccentBrush"), 1), new(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
    }
    private static string ShortType(string name)
    {
        if (name.Length == 0) return "<unknown type>";
        var generic = name.IndexOf('<'); var dot = name.LastIndexOf('.', generic < 0 ? name.Length - 1 : generic);
        return dot < 0 ? name : name[(dot + 1)..];
    }
    private void Text(DrawingContext dc, string text, Point position, Brush brush, double size, double width, bool bold = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, width), MaxTextHeight = size * 1.6, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, position);
    }
    private Point World(Point point) => new((point.X - _pan.X) / Zoom, (point.Y - _pan.Y) / Zoom);
    private int? HitNode(Point world) => Graph?.Nodes.Reverse().FirstOrDefault(n => new Rect(_positions[n.Object.Id], new Size(NodeWidth, NodeHeight)).Contains(world))?.Object.Id;
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        var point = e.GetPosition(this); var world = World(point); _lastPointer = point;
        if (e.ChangedButton == MouseButton.Left)
        {
            var node = HitNode(world);
            if (node is int id)
            {
                _keyboardNode = id;
                if (e.ClickCount == 2) { InvokeInspect(id); e.Handled = true; return; }
                _dragNode = id;
            }
            else if (_edgeLabels.LastOrDefault(x => x.Bounds.Contains(world)).Reference is { } reference)
            { SetCurrentValue(SelectedReferenceProperty, reference); InvalidateVisual(); e.Handled = true; return; }
            else _panning = true;
        }
        else if (e.ChangedButton is MouseButton.Middle or MouseButton.Right) _panning = true;
        else return;
        CaptureMouse(); e.Handled = true; InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var point = e.GetPosition(this); var delta = point - _lastPointer;
        if (_dragNode is int id) { _positions[id] += delta / Zoom; InvalidateVisual(); }
        else if (_panning) { _pan += delta; InvalidateVisual(); }
        else if (HitNode(World(point)) is int hover && Graph?.Nodes.FirstOrDefault(n => n.Object.Id == hover) is { } node)
            ToolTip = $"{node.Object.Type}\n{node.Object.Address}\nOwn: {node.Object.ShallowBytes:N0} bytes\nRetained: {node.Object.RetainedBytes:N0} bytes\nDouble-click or press Enter to inspect";
        else ToolTip = "Drag objects to rearrange. Drag the background to pan. Use the wheel to zoom. Arrows select; Enter inspects; Ctrl+0 fits.";
        _lastPointer = point;
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    { base.OnMouseUp(e); _dragNode = null; _panning = false; ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _dragNode = null; _panning = false; }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { base.OnMouseWheel(e); ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true; }
    private void ZoomAt(Point point, double factor)
    { var world = World(point); Zoom = Math.Clamp(Zoom * factor, 0.08, 2.5); _pan = new(point.X - world.X * Zoom, point.Y - world.Y * Zoom); InvalidateVisual(); }
    public void FitGraph()
    {
        if (_positions.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var minX = _positions.Values.Min(p => p.X) - 90; var minY = _positions.Values.Min(p => p.Y) - 20;
        var width = _positions.Values.Max(p => p.X) + NodeWidth + 20 - minX;
        var height = _positions.Values.Max(p => p.Y) + NodeHeight + 20 - minY;
        Zoom = Math.Clamp(Math.Min(Math.Max(1, ActualWidth - 48) / width, Math.Max(1, ActualHeight - 48) / height), 0.08, 1);
        _pan = new((ActualWidth - width * Zoom) / 2 - minX * Zoom, (ActualHeight - height * Zoom) / 2 - minY * Zoom); InvalidateVisual();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { FitGraph(); e.Handled = true; return; }
        if (e.Key is Key.Add or Key.OemPlus) { ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1.2); e.Handled = true; return; }
        if (e.Key is Key.Subtract or Key.OemMinus) { ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1 / 1.2); e.Handled = true; return; }
        if (Graph is not { Nodes.Count: > 0 } graph) return;
        var nodes = graph.Nodes.OrderBy(n => _positions[n.Object.Id].X).ThenBy(n => _positions[n.Object.Id].Y).ToArray();
        if (e.Key == Key.Enter) { InvokeInspect(_keyboardNode ?? graph.Nodes.First(n => n.IsFocus).Object.Id); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var index = Array.FindIndex(nodes, n => n.Object.Id == _keyboardNode);
        if (index < 0) index = Array.FindIndex(nodes, n => n.IsFocus);
        index = (index + (e.Key is Key.Left or Key.Up ? -1 : 1) + nodes.Length) % nodes.Length;
        _keyboardNode = nodes[index].Object.Id;
        var p = _positions[_keyboardNode.Value]; _pan = new(ActualWidth / 2 - (p.X + NodeWidth / 2) * Zoom, ActualHeight / 2 - (p.Y + NodeHeight / 2) * Zoom);
        InvalidateVisual(); e.Handled = true;
    }
    private void InvokeInspect(int id) { if (InspectObjectCommand?.CanExecute(id) == true) InspectObjectCommand.Execute(id); }
    protected override AutomationPeer OnCreateAutomationPeer() => new GraphPeer(this);
    private sealed class GraphPeer(MemoryGraphSurface owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(MemoryGraphSurface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetNameCore() => "Object relationship map";
        protected override string GetHelpTextCore() => "Arrows select an object. Enter inspects it. Plus and minus zoom. Control+0 fits the graph. Drag to pan or move objects.";
    }
}
