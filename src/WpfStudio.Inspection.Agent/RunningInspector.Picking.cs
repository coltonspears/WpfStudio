using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Inspection.Agent;

internal sealed partial class RunningInspector
{
    private readonly object _pickingGate = new();
    private readonly Dictionary<int, DispatcherPicker> _pickers = [];
    private InspectionPickState _pickState = new(false, 0);
    private volatile bool _pickingDisposed;

    public async Task<InspectionPickState> SetPickingAsync(InspectionPickRequest request)
    {
        long sequence;
        lock (_pickingGate)
        {
            if (_pickingDisposed) return _pickState;
            _pickState = new(request.Enabled, _pickState.Sequence + 1, Status: request.Enabled
                ? "Click an element in the application to inspect it. Press Escape to cancel."
                : "Element picking cancelled.");
            sequence = _pickState.Sequence;
        }
        if (request.Enabled)
        {
            await ClearHighlightsAsync().ConfigureAwait(false);
            var sources = PresentationSource.CurrentSources.Cast<PresentationSource>().Take(64).ToArray();
            await UpdatePickingSourcesAsync(sources).ConfigureAwait(false);
        }
        else await StopPickingAsync(clearHighlight: true, sequence).ConfigureAwait(false);
        return GetPickState();
    }

    private InspectionPickState GetPickState()
    {
        lock (_pickingGate) return _pickState;
    }

    private async Task UpdatePickingSourcesAsync(IReadOnlyList<PresentationSource> sources)
    {
        DispatcherPicker[] existing;
        lock (_pickingGate) existing = _pickers.Values.ToArray();
        await Task.WhenAll(existing.Select(picker => OnPickerDispatcherAsync(picker, picker.ValidateHighlight,
            TimeSpan.FromMilliseconds(250), retainPending: true))).ConfigureAwait(false);
        if (!GetPickState().IsActive) return;
        var pickers = sources.Select(source => GetPicker(source.Dispatcher)).Distinct().ToArray();
        await Task.WhenAll(pickers.Select(picker => OnPickerDispatcherAsync(picker, picker.StartListening,
            TimeSpan.FromMilliseconds(250), retainPending: false))).ConfigureAwait(false);
    }

    private DispatcherPicker GetPicker(Dispatcher dispatcher)
    {
        lock (_pickingGate)
        {
            foreach (int stale in _pickers.Where(pair => !pair.Value.TryGetDispatcher(out var existing) || existing.HasShutdownFinished)
                .Select(pair => pair.Key).ToArray()) _pickers.Remove(stale);
            int id = dispatcher.Thread.ManagedThreadId;
            if (!_pickers.TryGetValue(id, out var picker))
            {
                picker = new(this, dispatcher);
                _pickers[id] = picker;
            }
            return picker;
        }
    }

    public async Task<InspectionHighlightResult> HighlightAsync(InspectionHighlightRequest request)
    {
        if (request.NodeId is null)
        {
            await ClearHighlightsAsync().ConfigureAwait(false);
            return new(true, "Highlight cleared.");
        }
        if (_pickingDisposed || request.Revision != _revision || !_entries.TryGetValue(request.NodeId, out var entry) ||
            !entry.Target.TryGetTarget(out var target))
            return new(false, "The selected element is no longer in the current tree. Refresh the tree.");
        await ClearHighlightsAsync().ConfigureAwait(false);
        var picker = GetPicker(target.Dispatcher);
        try
        {
            return await OnDispatcherAsync(target.Dispatcher, _ =>
            {
                if (_pickingDisposed || request.Revision != _revision || !entry.Source.TryGetTarget(out var source) || source.IsDisposed ||
                    !entry.Root.TryGetTarget(out var root) || source.RootVisual != root || !IsAttached(target, root))
                    return new InspectionHighlightResult(false, "The selected element has left its presentation tree.");
                return picker.Highlight(target, root, request.ShowLayout);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) { return new(false, Limit(exception.GetBaseException().Message, 500)); }
    }

    private Task ClearHighlightsAsync()
    {
        DispatcherPicker[] pickers;
        lock (_pickingGate) pickers = _pickers.Values.ToArray();
        return Task.WhenAll(pickers.Select(picker => OnPickerDispatcherAsync(picker, picker.ClearHighlight,
            TimeSpan.FromMilliseconds(250), retainPending: true)));
    }

    private Task StopPickingAsync(bool clearHighlight, long sequence, DispatcherPicker? keepUntilMouseUp = null)
    {
        DispatcherPicker[] pickers;
        lock (_pickingGate) pickers = _pickers.Values.ToArray();
        return Task.WhenAll(pickers.Where(picker => !ReferenceEquals(picker, keepUntilMouseUp)).Select(picker =>
            OnPickerDispatcherAsync(picker, () =>
            {
                // A delayed cleanup must not remove a later pick session's
                // listeners when a busy dispatcher starts processing again.
                if (GetPickState().Sequence != sequence) return;
                picker.StopListening();
                if (clearHighlight) picker.ClearHighlight();
            }, TimeSpan.FromMilliseconds(250), retainPending: true)));
    }

    private bool CompletePick(DispatcherPicker picker, DependencyObject? target)
    {
        long sequence;
        lock (_pickingGate)
        {
            if (_pickingDisposed || !_pickState.IsActive) return false;
            _pickState = new(false, _pickState.Sequence + 1, target is null ? null : GetId(target),
                target is null ? "Element picking cancelled." : "Element selected from the running application.");
            sequence = _pickState.Sequence;
        }
        if (target is null)
        {
            picker.StopListening();
            picker.ClearHighlight();
            _ = StopPickingAsync(clearHighlight: true, sequence);
        }
        else
        {
            picker.SuppressMouseUp = true;
            var result = picker.Highlight(target);
            if (!result.Applied)
                lock (_pickingGate)
                    if (_pickState.Sequence == sequence) _pickState = _pickState with { Status = "Element selected. " + result.Status };
            // Only the clicked dispatcher's handler remains until the matching up
            // event. Other dispatchers stop immediately and remove hover adorners.
            _ = StopPickingAsync(clearHighlight: true, sequence, keepUntilMouseUp: picker);
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        DispatcherPicker[] pickers;
        lock (_pickingGate)
        {
            if (_pickingDisposed) return;
            _pickingDisposed = true;
            _editingDisposed = true;
            _pickState = new(false, _pickState.Sequence + 1, Status: "Inspector disconnected.");
            pickers = _pickers.Values.ToArray();
            _pickers.Clear();
        }
        var restore = RestorePropertyEditsAsync();
        await Task.WhenAll(pickers.Select(picker => OnPickerDispatcherAsync(picker, () =>
        {
            picker.StopListening();
            picker.ClearHighlight();
        }, TimeSpan.FromSeconds(2), retainPending: true))).ConfigureAwait(false);
        await restore.ConfigureAwait(false);
    }

    private static async Task OnPickerDispatcherAsync(DispatcherPicker picker, Action action, TimeSpan timeout, bool retainPending)
    {
        if (!picker.TryGetDispatcher(out var dispatcher) || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        DispatcherOperation? operation = null;
        try
        {
            operation = dispatcher.InvokeAsync(action, DispatcherPriority.Send);
            await operation.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Cleanup remains queued for a suspended/unresponsive dispatcher.
            // Its helper holds only weak object references and cannot retain a
            // closed window. Input callbacks also become inert immediately.
            if (!retainPending) operation?.Abort();
        }
        catch (Exception) { }
    }

    private static bool IsInspectionAdornment(DependencyObject target) => target is InspectionAdorner;

    private sealed class DispatcherPicker
    {
        private readonly WeakReference<RunningInspector> _owner;
        private readonly WeakReference<Dispatcher> _dispatcher;
        private WeakReference<InputManager>? _manager;
        private WeakReference<InspectionAdorner>? _adorner;
        private WeakReference<AdornerLayer>? _layer;
        private WeakReference<DependencyObject>? _highlightedTarget;
        private WeakReference<Visual>? _highlightedRoot;
        private bool _listening;
        public bool SuppressMouseUp { get; set; }

        public DispatcherPicker(RunningInspector owner, Dispatcher dispatcher)
        {
            _owner = new(owner);
            _dispatcher = new(dispatcher);
        }

        public bool TryGetDispatcher(out Dispatcher dispatcher) => _dispatcher.TryGetTarget(out dispatcher!);

        public void StartListening()
        {
            if (!_owner.TryGetTarget(out var owner) || owner._pickingDisposed || !owner.GetPickState().IsActive) return;
            SuppressMouseUp = false;
            if (_listening) return;
            var manager = InputManager.Current;
            manager.PreProcessInput += OnPreProcessInput;
            _manager = new(manager);
            SuppressMouseUp = false;
            _listening = true;
        }

        public void StopListening()
        {
            if (_listening && _manager?.TryGetTarget(out var manager) == true) manager.PreProcessInput -= OnPreProcessInput;
            _listening = false;
            SuppressMouseUp = false;
            _manager = null;
        }

        private void OnPreProcessInput(object sender, PreProcessInputEventArgs args)
        {
            try
            {
                if (!_owner.TryGetTarget(out var owner) || owner._pickingDisposed)
                {
                    StopListening();
                    ClearHighlight();
                    return;
                }
                var input = args.StagingItem.Input;
                if (SuppressMouseUp && !owner.GetPickState().IsActive && input is MouseButtonEventArgs nextDown &&
                    nextDown.ChangedButton == MouseButton.Left &&
                    (nextDown.RoutedEvent == Mouse.PreviewMouseDownEvent || nextDown.RoutedEvent == Mouse.MouseDownEvent))
                {
                    // A release may have happened outside this dispatcher or
                    // window. Never swallow the next independent click's up.
                    StopListening();
                    return;
                }
                if (SuppressMouseUp && input is MouseButtonEventArgs up && up.ChangedButton == MouseButton.Left &&
                    (up.RoutedEvent == Mouse.PreviewMouseUpEvent || up.RoutedEvent == Mouse.MouseUpEvent))
                {
                    input.Handled = true;
                    args.Cancel();
                    StopListening();
                    return;
                }
                if (!owner.GetPickState().IsActive) return;
                if (input is KeyEventArgs key && (key.Key == Key.Escape || key.SystemKey == Key.Escape) &&
                    (key.RoutedEvent == Keyboard.PreviewKeyDownEvent || key.RoutedEvent == Keyboard.KeyDownEvent))
                {
                    input.Handled = true;
                    args.Cancel();
                    owner.CompletePick(this, null);
                    return;
                }
                if (input is not MouseEventArgs mouse) return;
                var target = input.OriginalSource as DependencyObject ?? input.Source as DependencyObject ?? mouse.MouseDevice.DirectlyOver as DependencyObject;
                if (target is null || IsInspectionAdornment(target) || FindPresentationSource(target) is null) return;
                if (input is MouseButtonEventArgs down && down.ChangedButton == MouseButton.Left &&
                    (down.RoutedEvent == Mouse.PreviewMouseDownEvent || down.RoutedEvent == Mouse.MouseDownEvent))
                {
                    // Cancelling the input staging item prevents routed control
                    // actions and preview-to-bubble promotion of this click.
                    input.Handled = true;
                    args.Cancel();
                    owner.CompletePick(this, target);
                }
                else if (mouse.RoutedEvent == Mouse.PreviewMouseMoveEvent || mouse.RoutedEvent == Mouse.MouseMoveEvent)
                    Highlight(target);
            }
            catch (Exception)
            {
                // Inspector event handlers never inject exceptions into app input.
                try { StopListening(); } catch (Exception) { }
                try { ClearHighlight(); } catch (Exception) { }
            }
        }

        private static PresentationSource? FindPresentationSource(DependencyObject target)
        {
            var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
            for (DependencyObject? current = target; current is not null && visited.Count < 512 && visited.Add(current);)
            {
                if (current is Visual visual) return PresentationSource.FromVisual(visual);
                current = current is Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        public InspectionHighlightResult Highlight(DependencyObject target, Visual? coordinateRoot = null, bool showLayout = false)
        {
            if (target is not UIElement element)
            {
                ClearHighlight();
                return new(false, "Highlighting requires a visual UIElement; this node has no supported adorner surface.");
            }
            if (!showLayout && _adorner?.TryGetTarget(out var existing) == true && !existing.IsLayout &&
                ReferenceEquals(existing.AdornedElement, element))
                return new(true);
            ClearHighlight();
            if (!element.IsVisible || element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0)
                return new(false, "The element has no visible arranged bounds to highlight.");
            coordinateRoot ??= PresentationSource.FromVisual(element)?.RootVisual;
            if (coordinateRoot is null || !IsAttached(target, coordinateRoot))
                return new(false, "The element has left its presentation tree.");
            UIElement surface = element;
            var layer = AdornerLayer.GetAdornerLayer(surface);
            IReadOnlyList<LayoutOverlay>? polygons = null;
            if (showLayout)
            {
                var layout = LayoutReader.Capture(target, coordinateRoot);
                if (!layout.Available || layout.Overlays.Count == 0)
                    return new(false, layout.Status ?? "No layout overlay geometry is available for this element.");
                // Adorn the highest supported ancestor, so the layout slot and
                // margin remain visible beyond the selected child's own bounds.
                DependencyObject? current = element;
                for (int depth = 0; current is Visual visual && depth < 512; depth++)
                {
                    if (current is UIElement candidate && AdornerLayer.GetAdornerLayer(candidate) is { } candidateLayer)
                    {
                        surface = candidate;
                        layer = candidateLayer;
                    }
                    if (ReferenceEquals(visual, coordinateRoot)) break;
                    current = VisualTreeHelper.GetParent(visual);
                }
                if (layer is not null)
                {
                    var transform = coordinateRoot.TransformToVisual(surface);
                    var converted = new List<LayoutOverlay>();
                    foreach (var overlay in layout.Overlays.Take(64))
                    {
                        var points = new List<LayoutPoint>();
                        foreach (var point in overlay.Points.Take(256))
                        {
                            if (!transform.TryTransform(new Point(point.X, point.Y), out var mapped) ||
                                !double.IsFinite(mapped.X) || !double.IsFinite(mapped.Y))
                                return new(false, "The layout coordinates cannot be mapped to this adorner surface.");
                            points.Add(new(mapped.X, mapped.Y));
                        }
                        if (points.Count >= 3) converted.Add(overlay with { Points = points });
                    }
                    if (converted.Count == 0) return new(false, "No supported layout polygons are available.");
                    polygons = converted;
                }
            }
            if (layer is null) return new(false, "This presentation root has no adorner layer; highlighting is unavailable here.");
            var adorner = new InspectionAdorner(surface, target, coordinateRoot, polygons);
            layer.Add(adorner);
            _adorner = new(adorner);
            _layer = new(layer);
            _highlightedTarget = new(target);
            _highlightedRoot = new(coordinateRoot);
            return new(true);
        }

        public void ValidateHighlight()
        {
            if (_adorner is null) return;
            if (_highlightedTarget?.TryGetTarget(out var target) != true ||
                _highlightedRoot?.TryGetTarget(out var root) != true ||
                target is null || root is null ||
                PresentationSource.FromVisual(root)?.RootVisual != root || !IsAttached(target, root))
                ClearHighlight();
        }

        public void ClearHighlight()
        {
            if (_adorner?.TryGetTarget(out var adorner) == true && _layer?.TryGetTarget(out var layer) == true)
                layer.Remove(adorner);
            _adorner = null;
            _layer = null;
            _highlightedTarget = null;
            _highlightedRoot = null;
        }
    }

    private sealed class InspectionAdorner : Adorner
    {
        private static readonly Pen Outline = CreateOutline();
        private readonly WeakReference<DependencyObject> _target;
        private readonly WeakReference<Visual> _root;
        private readonly IReadOnlyList<(Geometry Geometry, Brush Fill, Pen Outline)>? _polygons;
        public bool IsLayout => _polygons is not null;

        public InspectionAdorner(UIElement element, DependencyObject target, Visual root, IReadOnlyList<LayoutOverlay>? polygons) : base(element)
        {
            _target = new(target);
            _root = new(root);
            _polygons = polygons?.Select(CreatePolygon).ToArray();
            IsHitTestVisible = false;
            Focusable = false;
            IsEnabled = false;
        }

        private static (Geometry, Brush, Pen) CreatePolygon(LayoutOverlay overlay)
        {
            // Shared palette with the preview surface: slot cyan, render green,
            // margin orange, and clip-bounds purple. Clip polygons are bounds,
            // not an assertion of the exact visible region.
            Color color = overlay.Kind switch
            {
                "slot" => Color.FromRgb(0x38, 0xBD, 0xF8),
                "render" => Color.FromRgb(0x4A, 0xDE, 0x80),
                "margin" => Color.FromRgb(0xFB, 0x92, 0x3C),
                "clip-bounds" => Color.FromRgb(0xC0, 0x84, 0xFC),
                _ => Color.FromRgb(0x94, 0xA3, 0xB8)
            };
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(overlay.Points[0].X, overlay.Points[0].Y), isFilled: true, isClosed: true);
                context.PolyLineTo(overlay.Points.Skip(1).Select(point => new Point(point.X, point.Y)).ToArray(), isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(20, color.R, color.G, color.B));
            fill.Freeze();
            var pen = new Pen(new SolidColorBrush(color), 1.5);
            pen.Freeze();
            return (geometry, fill, pen);
        }

        private static Pen CreateOutline()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 2);
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (!_target.TryGetTarget(out var target) || !_root.TryGetTarget(out var root) || !IsAttached(target, root)) return;
            if (_polygons is not null)
            {
                foreach (var (geometry, fill, outline) in _polygons) drawingContext.DrawGeometry(fill, outline, geometry);
                return;
            }
            Size size = AdornedElement.RenderSize;
            if (size.Width > 0 && size.Height > 0)
                drawingContext.DrawRectangle(null, Outline, new Rect(0, 0, size.Width, size.Height));
        }
    }
}
