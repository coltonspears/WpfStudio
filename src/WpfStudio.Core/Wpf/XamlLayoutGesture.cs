using WpfStudio.Contracts;

namespace WpfStudio.Core.Wpf;

public enum XamlLayoutHandle { Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }
public sealed record XamlLayoutGesture(XamlLayoutHandle Handle, double DeltaX, double DeltaY,
    double Zoom = 1, bool BypassSnapping = false);
/// <summary>A line in preview-root DIPs. Vertical lines have a constant X, horizontal lines a constant Y.</summary>
public sealed record XamlLayoutGuide(bool Vertical, double Position, double Start, double End);
public sealed record XamlLayoutGestureResult(bool Success, PreviewBounds? Bounds,
    IReadOnlyList<XamlLayoutGuide> Guides, string? Error = null, PreviewLayoutEditValues? Values = null);

public static partial class XamlLayoutEditService
{
    private const double MaximumCoordinate = 1_000_000;
    private const double Epsilon = 0.000001;
    private const int MaximumSiblings = 512;
    private sealed record SnapCandidate(double Position, double Start, double End);

    /// <summary>Calculates a local ghost and literal assignments without mutating source or the preview.</summary>
    public static XamlLayoutGestureResult Calculate(PreviewLayoutEditContext context, XamlLayoutGesture gesture)
    {
        if (context is null || gesture is null) return GestureFailure("A current layout target and gesture are required.");
        if (ValidateContext(context) is { } error) return GestureFailure(error);
        if (!Enum.IsDefined(gesture.Handle) || !Finite(gesture.DeltaX) || !Finite(gesture.DeltaY)
            || !double.IsFinite(gesture.Zoom) || gesture.Zoom <= 0)
            return GestureFailure("The gesture contains invalid coordinates, zoom or handle identity.");
        var before = context.Bounds!;
        double x = before.X, y = before.Y, width = before.Width, height = before.Height;
        bool west = West(gesture.Handle), east = East(gesture.Handle), north = North(gesture.Handle), south = South(gesture.Handle);
        double minimumWidth = context.MinWidth, minimumHeight = context.MinHeight;
        double maximumWidth = Math.Min(context.MaxWidth ?? MaximumCoordinate, MaximumCoordinate);
        double maximumHeight = Math.Min(context.MaxHeight ?? MaximumCoordinate, MaximumCoordinate);
        if (gesture.Handle == XamlLayoutHandle.Move)
        {
            x = Math.Clamp(x + gesture.DeltaX, -MaximumCoordinate, MaximumCoordinate - width);
            y = Math.Clamp(y + gesture.DeltaY, -MaximumCoordinate, MaximumCoordinate - height);
        }
        else
        {
            if (west || east)
            {
                width = Math.Clamp(width + (west ? -gesture.DeltaX : gesture.DeltaX), minimumWidth, maximumWidth);
                if (west) x = before.X + before.Width - width;
            }
            if (north || south)
            {
                height = Math.Clamp(height + (north ? -gesture.DeltaY : gesture.DeltaY), minimumHeight, maximumHeight);
                if (north) y = before.Y + before.Height - height;
            }
        }
        var bounds = new PreviewBounds(x, y, width, height);
        if (!ValidBounds(bounds)) return GestureFailure("The resized rectangle exceeds the finite layout coordinate budget.");
        var guides = new List<XamlLayoutGuide>(2);
        if (!gesture.BypassSnapping && (!Same(gesture.DeltaX, 0) || !Same(gesture.DeltaY, 0)))
        {
            double tolerance = 6 / Math.Clamp(gesture.Zoom, .1, 3);
            var rectangles = new List<PreviewBounds> { context.ParentBounds!, context.SlotBounds! };
            rectangles.AddRange((context.Siblings ?? []).Where(sibling => sibling.NodeId != context.NodeId)
                .Take(MaximumSiblings).Select(sibling => sibling.Bounds).Where(ValidBounds));
            if (gesture.Handle == XamlLayoutHandle.Move || west || east)
                bounds = Snap(bounds, before, rectangles, vertical: true, gesture.Handle, tolerance,
                    minimumWidth, maximumWidth, guides);
            if (gesture.Handle == XamlLayoutHandle.Move || north || south)
                bounds = Snap(bounds, before, rectangles, vertical: false, gesture.Handle, tolerance,
                    minimumHeight, maximumHeight, guides);
        }
        if (ValidateProposal(context, bounds, gesture.Handle) is { } proposalError) return GestureFailure(proposalError);
        return new(true, bounds, guides, Values: ValuesFor(context, bounds, gesture.Handle));
    }

    private static PreviewBounds Snap(PreviewBounds bounds, PreviewBounds original, IReadOnlyList<PreviewBounds> rectangles,
        bool vertical, XamlLayoutHandle handle, double tolerance, double minimum, double maximum,
        List<XamlLayoutGuide> guides)
    {
        bool move = handle == XamlLayoutHandle.Move;
        bool leading = vertical ? West(handle) : North(handle);
        double start = vertical ? bounds.X : bounds.Y, size = vertical ? bounds.Width : bounds.Height;
        double fixedEdge = vertical ? leading ? original.X + original.Width : original.X
            : leading ? original.Y + original.Height : original.Y;
        double[] movingEdges = move ? [start, start + size / 2, start + size] : [leading ? start : start + size];
        var candidates = rectangles.SelectMany(rectangle =>
        {
            double position = vertical ? rectangle.X : rectangle.Y, dimension = vertical ? rectangle.Width : rectangle.Height;
            double from = vertical ? rectangle.Y : rectangle.X, to = from + (vertical ? rectangle.Height : rectangle.Width);
            return new[] { new SnapCandidate(position, from, to), new SnapCandidate(position + dimension / 2, from, to),
                new SnapCandidate(position + dimension, from, to) };
        });
        var matches = candidates.SelectMany(candidate => movingEdges.Select(edge => (Candidate: candidate, Delta: candidate.Position - edge)))
            .Where(match => Math.Abs(match.Delta) <= tolerance)
            .Where(match => move ? start + match.Delta >= -MaximumCoordinate && start + match.Delta + size <= MaximumCoordinate
                : (leading ? fixedEdge - match.Candidate.Position : match.Candidate.Position - fixedEdge) >= minimum
                    && (leading ? fixedEdge - match.Candidate.Position : match.Candidate.Position - fixedEdge) <= maximum)
            .OrderBy(match => Math.Abs(match.Delta)).ThenBy(match => match.Candidate.Position)
            .ThenBy(match => match.Candidate.Start).ThenBy(match => match.Candidate.End).ToArray();
        if (matches.Length == 0) return bounds;
        var best = matches[0];
        if (move) start += best.Delta;
        else if (leading) { start = best.Candidate.Position; size = fixedEdge - start; }
        else size = best.Candidate.Position - fixedEdge;
        var result = vertical ? bounds with { X = start, Width = size } : bounds with { Y = start, Height = size };
        if (!ValidBounds(result)) return bounds;
        double ownStart = vertical ? result.Y : result.X, ownEnd = ownStart + (vertical ? result.Height : result.Width);
        guides.Add(new(vertical, best.Candidate.Position, Math.Min(best.Candidate.Start, ownStart), Math.Max(best.Candidate.End, ownEnd)));
        return result;
    }

    private static string? ValidateContext(PreviewLayoutEditContext context)
    {
        if (!context.Available) return context.Status ?? "The selected element cannot be moved or resized from this preview.";
        if (context.ParentKind is not ("Canvas" or "Grid") || context.Bounds is null || context.ParentBounds is null
            || context.SlotBounds is null || context.Margin is null || !ValidBounds(context.Bounds)
            || !ValidBounds(context.ParentBounds) || !ValidBounds(context.SlotBounds)
            || !ValidInsets(context.Margin)) return "The preview lacks a finite Canvas or Grid layout observation.";
        if (!Finite(context.MinWidth) || !Finite(context.MinHeight) || context.MinWidth < 0 || context.MinHeight < 0
            || context.MaxWidth is { } maxWidth && (!Finite(maxWidth) || maxWidth < context.MinWidth)
            || context.MaxHeight is { } maxHeight && (!Finite(maxHeight) || maxHeight < context.MinHeight)
            || context.Width is { } width && (!Finite(width) || width < 0)
            || context.Height is { } height && (!Finite(height) || height < 0)
            || new[] { context.CanvasLeft, context.CanvasTop, context.CanvasRight, context.CanvasBottom }.Any(value => value is { } number && !Finite(number)))
            return "The preview contains unsupported dimension constraints or Canvas anchors.";
        if (context.GridRow < 0 || context.GridColumn < 0 || context.GridRowSpan < 1 || context.GridColumnSpan < 1)
            return "The observed Grid placement is invalid.";
        return null;
    }

    private static string? ValidateProposal(PreviewLayoutEditContext context, PreviewBounds bounds, XamlLayoutHandle handle)
    {
        if (!Enum.IsDefined(handle) || !ValidBounds(bounds)) return "The proposed layout rectangle is invalid.";
        var before = context.Bounds!;
        if (handle == XamlLayoutHandle.Move)
        {
            if (!Same(bounds.Width, before.Width) || !Same(bounds.Height, before.Height)) return "A move cannot change the element's dimensions.";
        }
        else
        {
            if (West(handle) ? !Same(bounds.X + bounds.Width, before.X + before.Width) : !Same(bounds.X, before.X))
                return "The horizontal resize anchor changed.";
            if (North(handle) ? !Same(bounds.Y + bounds.Height, before.Y + before.Height) : !Same(bounds.Y, before.Y))
                return "The vertical resize anchor changed.";
            if (!West(handle) && !East(handle) && !Same(bounds.Width, before.Width)
                || !North(handle) && !South(handle) && !Same(bounds.Height, before.Height)) return "The gesture changed an axis outside its resize handle.";
            if ((West(handle) || East(handle)) && (bounds.Width < context.MinWidth - Epsilon || bounds.Width > (context.MaxWidth ?? MaximumCoordinate) + Epsilon)
                || (North(handle) || South(handle)) && (bounds.Height < context.MinHeight - Epsilon || bounds.Height > (context.MaxHeight ?? MaximumCoordinate) + Epsilon))
                return "The proposed size is outside the observed minimum or maximum.";
        }
        return null;
    }

    private static PreviewLayoutEditValues ValuesFor(PreviewLayoutEditContext context, PreviewBounds bounds, XamlLayoutHandle handle)
    {
        var before = context.Bounds!;
        double dx = bounds.X - before.X, dy = bounds.Y - before.Y, dw = bounds.Width - before.Width, dh = bounds.Height - before.Height;
        double? width = (West(handle) || East(handle)) && !Same(dw, 0) ? bounds.Width : null;
        double? height = (North(handle) || South(handle)) && !Same(dh, 0) ? bounds.Height : null;
        if (context.ParentKind == "Grid")
        {
            var old = context.Margin!;
            var margin = new PreviewLayoutInsets(old.Left + dx, old.Top + dy, old.Right - dx - dw, old.Bottom - dy - dh);
            return new(width, height, !Same(dx, 0) || !Same(dy, 0) || !Same(dw, 0) || !Same(dh, 0) ? margin : null);
        }
        double? left = null, top = null, right = null, bottom = null;
        if (context.CanvasLeft is { } activeLeft)
        { if (!Same(dx, 0)) left = activeLeft + dx; }
        else if (context.CanvasRight is { } activeRight)
        { if (!Same(dx + dw, 0)) right = activeRight - dx - dw; }
        else if (!Same(dx, 0)) left = dx;
        if (context.CanvasTop is { } activeTop)
        { if (!Same(dy, 0)) top = activeTop + dy; }
        else if (context.CanvasBottom is { } activeBottom)
        { if (!Same(dy + dh, 0)) bottom = activeBottom - dy - dh; }
        else if (!Same(dy, 0)) top = dy;
        return new(width, height, CanvasLeft: left, CanvasTop: top, CanvasRight: right, CanvasBottom: bottom);
    }

    private static bool West(XamlLayoutHandle handle) => handle is XamlLayoutHandle.Left or XamlLayoutHandle.TopLeft or XamlLayoutHandle.BottomLeft;
    private static bool East(XamlLayoutHandle handle) => handle is XamlLayoutHandle.Right or XamlLayoutHandle.TopRight or XamlLayoutHandle.BottomRight;
    private static bool North(XamlLayoutHandle handle) => handle is XamlLayoutHandle.Top or XamlLayoutHandle.TopLeft or XamlLayoutHandle.TopRight;
    private static bool South(XamlLayoutHandle handle) => handle is XamlLayoutHandle.Bottom or XamlLayoutHandle.BottomLeft or XamlLayoutHandle.BottomRight;
    private static bool Same(double left, double right) => Math.Abs(left - right) <= Epsilon;
    private static bool Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= MaximumCoordinate;
    private static bool ValidBounds(PreviewBounds bounds) => bounds is not null && Finite(bounds.X) && Finite(bounds.Y)
        && Finite(bounds.Width) && Finite(bounds.Height) && bounds.Width >= 0 && bounds.Height >= 0
        && Finite(bounds.X + bounds.Width) && Finite(bounds.Y + bounds.Height);
    private static bool ValidInsets(PreviewLayoutInsets margin) => Finite(margin.Left) && Finite(margin.Top) && Finite(margin.Right) && Finite(margin.Bottom);
    private static XamlLayoutGestureResult GestureFailure(string message) => new(false, null, [], message);
}
