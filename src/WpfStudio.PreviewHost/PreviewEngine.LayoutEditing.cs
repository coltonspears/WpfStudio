using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

public sealed partial class PreviewEngine
{
    private const int LayoutObservationLimit = 64;
    private readonly Dictionary<string, LayoutObservation> _layoutObservations = new(StringComparer.Ordinal);
    private sealed record LayoutObservation(PreviewLayoutEditContext Context, string Fingerprint,
        WeakReference<FrameworkElement> Target, WeakReference<Panel> Parent, object? Style, object? ParentStyle,
        IReadOnlyList<object?> Locals);
    private static readonly DependencyProperty[] LayoutElementProperties =
    [
        FrameworkElement.WidthProperty, FrameworkElement.HeightProperty, FrameworkElement.MarginProperty,
        FrameworkElement.MinWidthProperty, FrameworkElement.MinHeightProperty,
        FrameworkElement.MaxWidthProperty, FrameworkElement.MaxHeightProperty,
        FrameworkElement.HorizontalAlignmentProperty, FrameworkElement.VerticalAlignmentProperty
    ];
    private static readonly DependencyProperty[] LayoutCanvasProperties =
        [Canvas.LeftProperty, Canvas.TopProperty, Canvas.RightProperty, Canvas.BottomProperty];
    private static readonly DependencyProperty[] LayoutGridProperties =
        [Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty];

    public Task<PreviewLayoutValidationResult> ValidateLayoutEditAsync(PreviewLayoutValidationRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Version != _version || request.Surface != _surfaceIdentity || !CurrentSurface(request.Surface)
                || !_layoutObservations.TryGetValue(request.NodeId, out var observed) || request.Token != observed.Context.Token)
                return Reply("The preview or layout observation changed. Refresh the selection.");
            if (!observed.Target.TryGetTarget(out var target) || !observed.Parent.TryGetTarget(out var parent)
                || !_objects.TryGetValue(request.NodeId, out var current) || !ReferenceEquals(target, current))
                return Reply("The selected preview object is no longer available.");
            var captured = ReadLayoutEditing(target, out var locals);
            if (!captured.Available || !ReferenceEquals(VisualTreeHelper.GetParent(target), parent)
                || Fingerprint(captured) != observed.Fingerprint || !SameLocals(locals, observed.Locals)
                || !ReferenceEquals(target.GetValue(FrameworkElement.StyleProperty), observed.Style)
                || !ReferenceEquals(parent.GetValue(FrameworkElement.StyleProperty), observed.ParentStyle))
                return Reply(captured.Status ?? "Layout or property provenance changed. Refresh the selection.");
            string? error = ValidateLayoutValues(request.Values, captured);
            return Reply(error);

            Task<PreviewLayoutValidationResult> Reply(string? error) => Task.FromResult(new PreviewLayoutValidationResult(request, error is null, error));
        }, cancellationToken);

    private PreviewLayoutEditContext CaptureLayoutEditing(DependencyObject target)
    {
        var context = ReadLayoutEditing(target, out var locals);
        if (!context.Available || target is not FrameworkElement element || VisualTreeHelper.GetParent(element) is not Panel parent)
        { _layoutObservations.Remove(context.NodeId); return context; }
        string fingerprint = Fingerprint(context);
        object? style = element.GetValue(FrameworkElement.StyleProperty), parentStyle = parent.GetValue(FrameworkElement.StyleProperty);
        if (_layoutObservations.TryGetValue(context.NodeId, out var previous) && previous.Fingerprint == fingerprint
            && previous.Target.TryGetTarget(out var previousTarget) && ReferenceEquals(previousTarget, target)
            && previous.Parent.TryGetTarget(out var previousParent) && ReferenceEquals(previousParent, parent)
            && SameLocals(previous.Locals, locals) && ReferenceEquals(previous.Style, style) && ReferenceEquals(previous.ParentStyle, parentStyle))
            return context with { Token = previous.Context.Token };
        context = context with { Token = Guid.NewGuid().ToString("N") };
        if (!_layoutObservations.ContainsKey(context.NodeId) && _layoutObservations.Count >= LayoutObservationLimit)
            _layoutObservations.Remove(_layoutObservations.Keys.First());
        _layoutObservations[context.NodeId] = new(context, fingerprint, new(element), new(parent), style, parentStyle, locals);
        return context;
    }

    private PreviewLayoutEditContext ReadLayoutEditing(DependencyObject target, out IReadOnlyList<object?> locals)
    {
        locals = [];
        string nodeId = _ids.GetValueOrDefault(target) ?? "";
        PreviewLayoutEditContext Unavailable(string reason) => new(false, _version, nodeId, Status: reason, Surface: _surfaceIdentity);
        try
        {
            if (_document is null) return Unavailable("Visual source editing requires the current source preview; compiled views are not source-editing evidence.");
            if (_viewport is null || !CurrentSurface(_surfaceIdentity) || !IsInCurrentPreview(target))
                return Unavailable("The selected object is not attached to the current preview.");
            if (target is not FrameworkElement element || ReferenceEquals(target, _root))
                return Unavailable("Select an authored child element. Preview roots and generated placeholders cannot be moved or resized in source.");
            var authored = _document.GetLayoutSource(target);
            if (authored is null || authored.Type != target.GetType() || authored.Parent is null)
                return Unavailable("The runtime object does not have an exact authored object and parent identity.");
            if (authored.Unavailable is not null) return Unavailable(authored.Unavailable);
            if (VisualTreeHelper.GetParent(target) is not Panel parent || parent.GetType() != typeof(Canvas) && parent.GetType() != typeof(Grid))
                return Unavailable("Visual source editing currently supports direct children of framework Canvas and Grid panels.");
            if (!ReferenceEquals(LogicalTreeHelper.GetParent(target), parent) || parent.IsItemsHost)
                return Unavailable("Generated containers and nonmatching logical/layout parents cannot establish an authored child relationship.");
            var parentSource = _document.GetLayoutSource(parent);
            if (parentSource is null || parentSource.Unavailable is not null || parentSource.Type != parent.GetType()
                || parentSource.Element != authored.Parent)
                return Unavailable("The current layout parent differs from the authored parent, or belongs to a deferred declaration.");
            if (!UniqueLayoutSources(authored.Id, parentSource.Id))
                return Unavailable("Source identities are repeated or the current visual tree exceeds the authoring scan limit.");
            if (!IdentityLayoutTransform(element, FrameworkElement.LayoutTransformProperty)
                || !IdentityLayoutTransform(element, UIElement.RenderTransformProperty))
                return Unavailable("The selected element must have identity layout and render transforms for source move/resize.");
            if (!TryLayoutOffset(element, out var offset, out string? reason)
                || !TryLayoutOffset(parent, out var parentOffset, out reason))
                return Unavailable(reason ?? "The viewport coordinate frame is unavailable.");
            if (!Positive(element.RenderSize) || !Positive(parent.RenderSize))
                return Unavailable("The selected element and parent must have finite, nonzero render sizes.");
            Rect slot = LayoutInformation.GetLayoutSlot(element);
            if (!FiniteLayoutRect(slot) || slot.Width <= 0 || slot.Height <= 0)
                return Unavailable("The cached parent layout slot is empty or unavailable.");
            var properties = new List<PreviewProperty>();
            var localValues = new List<object?>();
            string? contentProperty = PreviewDocument.LayoutContentProperty(authored.Type);
            bool canvas = parent.GetType() == typeof(Canvas);
            foreach (var property in LayoutElementProperties.Concat(canvas ? LayoutCanvasProperties : LayoutGridProperties))
            {
                var provenance = DependencyPropertyHelper.GetValueSource(element, property);
                // Inspector-owned temporary edits are marker bindings and therefore
                // expressions too. No binding-source lookup is needed here.
                if (provenance.IsExpression || provenance.IsAnimated || provenance.IsCoerced)
                    return Unavailable($"{property.Name} uses an expression, animation, coercion, or temporary override. Edit its declaration directly.");
                if (_document.IsDesignTimeProperty(element, property))
                    return Unavailable($"{property.Name} is supplied by a design-time override; runtime source layout is not inferred from it.");
                bool attached = property.OwnerType == typeof(Canvas) || property.OwnerType == typeof(Grid);
                if (!attached && authored.Type.GetProperty(property.Name)?.DeclaringType != typeof(FrameworkElement))
                    return Unavailable($"The authored type hides the framework {property.Name} wrapper; its source assignment is ambiguous.");
                object value = element.GetValue(property);
                localValues.Add(element.ReadLocalValue(property));
                string literal = LayoutLiteral(value);
                properties.Add(new PreviewProperty(attached ? property.OwnerType.Name + "." + property.Name : property.Name,
                    property.PropertyType.FullName!, literal, provenance.BaseValueSource.ToString(), false, false, false, true,
                    OwnerType: property.OwnerType.FullName, OwnerAssembly: property.OwnerType.Assembly.GetName().Name,
                    IsAttached: attached, EditableValue: literal, CanWriteSource: true, ContentProperty: contentProperty));
            }
            var margin = (Thickness)element.GetValue(FrameworkElement.MarginProperty);
            if (!FiniteLayoutThickness(margin)) return Unavailable("Margin values are not finite.");
            var siblings = new List<PreviewLayoutSibling>();
            bool truncated = parent.Children.Count > 129;
            foreach (UIElement sibling in parent.Children.Cast<UIElement>().Take(129))
            {
                if (ReferenceEquals(sibling, element) || sibling is not FrameworkElement frame || !Positive(frame.RenderSize)) continue;
                if (TryLayoutOffset(frame, out var siblingOffset, out _) && siblings.Count < 128)
                    siblings.Add(new(GetId(frame), Bounds(siblingOffset, frame.RenderSize)));
            }
            var context = new PreviewLayoutEditContext(true, _version, nodeId, Element: authored.Element, Parent: parentSource.Element,
                ParentKind: canvas ? "Canvas" : "Grid", Bounds: Bounds(offset, element.RenderSize), ParentBounds: Bounds(parentOffset, parent.RenderSize),
                SlotBounds: new(parentOffset.X + slot.X, parentOffset.Y + slot.Y, slot.Width, slot.Height),
                Margin: new(margin.Left, margin.Top, margin.Right, margin.Bottom),
                Width: OptionalLayoutDouble(element, FrameworkElement.WidthProperty), Height: OptionalLayoutDouble(element, FrameworkElement.HeightProperty),
                MinWidth: (double)element.GetValue(FrameworkElement.MinWidthProperty), MinHeight: (double)element.GetValue(FrameworkElement.MinHeightProperty),
                MaxWidth: OptionalLayoutDouble(element, FrameworkElement.MaxWidthProperty), MaxHeight: OptionalLayoutDouble(element, FrameworkElement.MaxHeightProperty),
                HorizontalAlignment: ((HorizontalAlignment)element.GetValue(FrameworkElement.HorizontalAlignmentProperty)).ToString(),
                VerticalAlignment: ((VerticalAlignment)element.GetValue(FrameworkElement.VerticalAlignmentProperty)).ToString(),
                CanvasLeft: canvas ? OptionalLayoutDouble(element, Canvas.LeftProperty) : null,
                CanvasTop: canvas ? OptionalLayoutDouble(element, Canvas.TopProperty) : null,
                CanvasRight: canvas ? OptionalLayoutDouble(element, Canvas.RightProperty) : null,
                CanvasBottom: canvas ? OptionalLayoutDouble(element, Canvas.BottomProperty) : null,
                GridRow: canvas ? 0 : (int)element.GetValue(Grid.RowProperty), GridColumn: canvas ? 0 : (int)element.GetValue(Grid.ColumnProperty),
                GridRowSpan: canvas ? 1 : (int)element.GetValue(Grid.RowSpanProperty), GridColumnSpan: canvas ? 1 : (int)element.GetValue(Grid.ColumnSpanProperty),
                EditProperties: properties, Siblings: siblings, Surface: _surfaceIdentity, SourceHash: _document.LayoutSourceHash, SiblingsTruncated: truncated);
            // Reading framework DPs may encounter WPF expressions internally. Do not
            // accept a capture if that work invalidated the parent/geometry meanwhile.
            if (!ReferenceEquals(VisualTreeHelper.GetParent(element), parent) || !element.IsMeasureValid || !element.IsArrangeValid
                || !parent.IsMeasureValid || !parent.IsArrangeValid || !IsInCurrentPreview(element)
                || !TryLayoutOffset(element, out var finalOffset, out _) || finalOffset != offset
                || !TryLayoutOffset(parent, out var finalParentOffset, out _) || finalParentOffset != parentOffset
                || Bounds(offset, element.RenderSize) != context.Bounds || Bounds(parentOffset, parent.RenderSize) != context.ParentBounds
                || LayoutInformation.GetLayoutSlot(element) != slot)
                return Unavailable("Layout changed during the observation. Refresh the selection.");
            locals = localValues;
            return context;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return Unavailable($"Layout source editing is unavailable ({exception.GetType().Name})."); }
    }

    private bool UniqueLayoutSources(string selected, string parent)
    {
        if (_viewport is null) return false;
        var pending = new Stack<DependencyObject>();
        pending.Push(_viewport);
        int remaining = 10_000, selectedCount = 0, parentCount = 0;
        while (pending.TryPop(out var current))
        {
            if (--remaining < 0) return false;
            string? id = PreviewSource.GetId(current);
            if (id == selected && ++selectedCount > 1 || id == parent && ++parentCount > 1) return false;
            int count = VisualTreeHelper.GetChildrenCount(current);
            if (count > remaining) return false;
            for (int index = 0; index < count; index++) pending.Push(VisualTreeHelper.GetChild(current, index));
        }
        return selectedCount == 1 && parentCount == 1;
    }

    private bool TryLayoutOffset(FrameworkElement element, out Point offset, out string? reason)
    {
        offset = default; reason = null;
        Visual? current = element;
        for (int depth = 0; current is not null && depth < 64; depth++)
        {
            if (current is UIElement ui && (!ui.IsMeasureValid || !ui.IsArrangeValid || !ui.IsVisible))
            { reason = "Layout is pending or the element/ancestor is not visible."; return false; }
            if (current is FrameworkElement frame && (FlowDirection)frame.GetValue(FrameworkElement.FlowDirectionProperty) != FlowDirection.LeftToRight)
            { reason = "Right-to-left layout requires a separate editing policy and is unavailable for this gesture."; return false; }
            if (VisualTreeHelper.GetEffect(current) is not null)
            { reason = "Effects can change visible geometry; source layout gestures are unavailable for this frame."; return false; }
            if (ReferenceEquals(current, _viewport)) return double.IsFinite(offset.X) && double.IsFinite(offset.Y);
            if (!TryTranslation(VisualTreeHelper.GetTransform(current), out var translation))
            { reason = "Source layout gestures require translation-only, 1:1 visual transforms."; return false; }
            var localOffset = VisualTreeHelper.GetOffset(current);
            offset = new(offset.X + translation.X + localOffset.X, offset.Y + translation.Y + localOffset.Y);
            current = VisualTreeHelper.GetParent(current) as Visual;
        }
        reason = "The viewport is outside the supported 2D ancestor chain.";
        return false;
    }

    private static bool IdentityLayoutTransform(DependencyObject target, DependencyProperty property)
    {
        var source = DependencyPropertyHelper.GetValueSource(target, property);
        return !source.IsExpression && !source.IsAnimated && !source.IsCoerced
            && TryTranslation(target.GetValue(property) as Transform, out var translation) && translation.X == 0 && translation.Y == 0;
    }

    private static bool TryTranslation(Transform? transform, out Point translation)
    {
        int remaining = 32;
        return Read(transform, out translation, 0);
        bool Read(Transform? value, out Point result, int depth)
        {
            result = default;
            if (value is null) return true;
            if (--remaining < 0 || depth > 8 || value.HasAnimatedProperties) return false;
            if (value.GetType() == typeof(TransformGroup))
            {
                var children = (TransformCollection)value.GetValue(TransformGroup.ChildrenProperty);
                if (children.Count > remaining) return false;
                foreach (var child in children)
                {
                    if (!Read(child, out var part, depth + 1)) return false;
                    result = new(result.X + part.X, result.Y + part.Y);
                }
                return double.IsFinite(result.X) && double.IsFinite(result.Y);
            }
            var type = value.GetType();
            if (type != typeof(MatrixTransform) && type != typeof(TranslateTransform) && type != typeof(ScaleTransform)
                && type != typeof(RotateTransform) && type != typeof(SkewTransform)) return false;
            var matrix = value.Value;
            if (matrix.M11 != 1 || matrix.M22 != 1 || matrix.M12 != 0 || matrix.M21 != 0
                || !double.IsFinite(matrix.OffsetX) || !double.IsFinite(matrix.OffsetY)) return false;
            result = new(matrix.OffsetX, matrix.OffsetY);
            return true;
        }
    }

    private static string? ValidateLayoutValues(PreviewLayoutEditValues? values, PreviewLayoutEditContext context)
    {
        if (values is null) return null;
        if (values.Width is { } width && (!double.IsFinite(width) || width < context.MinWidth || width > (context.MaxWidth ?? double.PositiveInfinity))
            || values.Height is { } height && (!double.IsFinite(height) || height < context.MinHeight || height > (context.MaxHeight ?? double.PositiveInfinity)))
            return "The proposed size is outside the captured minimum/maximum constraints.";
        if (values.Margin is { } margin && (!double.IsFinite(margin.Left) || !double.IsFinite(margin.Top) || !double.IsFinite(margin.Right) || !double.IsFinite(margin.Bottom)))
            return "The proposed margin must be finite.";
        if (new[] { values.CanvasLeft, values.CanvasTop, values.CanvasRight, values.CanvasBottom }.Any(value => value is { } number && !double.IsFinite(number)))
            return "The proposed Canvas anchors must be finite.";
        if (context.ParentKind != "Canvas" && (values.CanvasLeft is not null || values.CanvasTop is not null || values.CanvasRight is not null || values.CanvasBottom is not null))
            return "Canvas anchors do not apply to this layout parent.";
        if (values.HorizontalAlignment is { } horizontal && (!Enum.TryParse<HorizontalAlignment>(horizontal, out var h) || !Enum.IsDefined(h))
            || values.VerticalAlignment is { } vertical && (!Enum.TryParse<VerticalAlignment>(vertical, out var v) || !Enum.IsDefined(v)))
            return "The proposed alignment is invalid.";
        if (values.GridRow is { } row && row != context.GridRow || values.GridColumn is { } column && column != context.GridColumn
            || values.GridRowSpan is { } rowSpan && rowSpan != context.GridRowSpan || values.GridColumnSpan is { } columnSpan && columnSpan != context.GridColumnSpan)
            return "This gesture preserves the existing Grid row, column, and spans.";
        return null;
    }

    private static bool SameLocals(IReadOnlyList<object?> first, IReadOnlyList<object?> second) => first.Count == second.Count
        && first.Zip(second).All(pair => ReferenceEquals(pair.First, pair.Second) || pair.First is double or int or Thickness or HorizontalAlignment or VerticalAlignment && pair.First.Equals(pair.Second));
    private static string Fingerprint(PreviewLayoutEditContext context) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(context with { Token = null }))));
    private static PreviewBounds Bounds(Point origin, Size size) => new(origin.X, origin.Y, size.Width, size.Height);
    private static bool Positive(Size size) => !size.IsEmpty && double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;
    private static bool FiniteLayoutRect(Rect value) => !value.IsEmpty && double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Width) && double.IsFinite(value.Height);
    private static bool FiniteLayoutThickness(Thickness value) => double.IsFinite(value.Left) && double.IsFinite(value.Top) && double.IsFinite(value.Right) && double.IsFinite(value.Bottom);
    private static double? OptionalLayoutDouble(DependencyObject target, DependencyProperty property) => target.GetValue(property) is double value && double.IsFinite(value) ? value : null;
    private static string LayoutLiteral(object value) => value switch
    {
        double number => double.IsNaN(number) ? "Auto" : double.IsPositiveInfinity(number) ? "Infinity" : number.ToString("R", CultureInfo.InvariantCulture),
        Thickness insets => string.Join(",", new[] { insets.Left, insets.Top, insets.Right, insets.Bottom }.Select(number => number.ToString("R", CultureInfo.InvariantCulture))),
        int number => number.ToString(CultureInfo.InvariantCulture),
        HorizontalAlignment horizontal => horizontal.ToString(), VerticalAlignment vertical => vertical.ToString(),
        _ => throw new InvalidOperationException("Unexpected layout property type.")
    };
}
