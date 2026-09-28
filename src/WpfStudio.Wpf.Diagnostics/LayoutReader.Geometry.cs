using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

public static partial class LayoutReader
{
    private static bool TryFrames(IReadOnlyList<Visual> chain, List<string> notices, out Dictionary<Visual, Matrix>? frames)
    {
        frames = null;
        foreach (var visual in chain)
        {
            var effect = VisualTreeHelper.GetEffect(visual);
            if (effect is null) continue;
            if (effect.GetType() != typeof(BlurEffect) && effect.GetType() != typeof(DropShadowEffect))
            {
                notices.Add("Custom effect coordinate mapping is not evaluated; layout facts remain available but geometry overlays are omitted.");
                return false;
            }
            notices.Add("Blur and shadow ink extents are excluded; outlines show layout geometry before the effect.");
        }
        var result = new Dictionary<Visual, Matrix> { [chain[^1]] = Matrix.Identity };
        for (int index = chain.Count - 2; index >= 0; index--)
        {
            var visual = chain[index];
            if (!TryMatrix(VisualTreeHelper.GetTransform(visual), out var matrix))
            {
                notices.Add("An unknown, overly complex or non-finite visual transform prevents safe coordinate mapping.");
                return false;
            }
            var offset = VisualTreeHelper.GetOffset(visual);
            if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y)) return false;
            // These are the affine steps used by WPF's TransformToAncestor. Compose them
            // from cached visual state to avoid EffectMapping and descendant-bounds callbacks.
            matrix.Translate(offset.X, offset.Y);
            matrix.Append(result[chain[index + 1]]);
            if (!Finite(matrix)) return false;
            result[visual] = matrix;
        }
        frames = result;
        return true;
    }

    private static string TransformText(Transform? transform)
    {
        if (!TryMatrix(transform, out var matrix)) return $"{(transform is null ? "None" : TypeName(transform))} (not evaluated)";
        if (matrix.IsIdentity) return "Identity";
        return $"{TypeName(transform!)}: [{Number(matrix.M11)}, {Number(matrix.M12)}; {Number(matrix.M21)}, {Number(matrix.M22)}; {Number(matrix.OffsetX)}, {Number(matrix.OffsetY)}]";
    }

    private static bool TryMatrix(Transform? transform, out Matrix matrix)
    {
        int budget = 32;
        return TryMatrix(transform, out matrix, ref budget, 0);
    }

    private static bool TryMatrix(Transform? transform, out Matrix matrix, ref int budget, int depth)
    {
        matrix = Matrix.Identity;
        if (transform is null) return true;
        if (--budget < 0 || depth > 8) return false;
        Type type = transform.GetType();
        if (type == typeof(TransformGroup))
        {
            var children = (TransformCollection)transform.GetValue(TransformGroup.ChildrenProperty);
            if (children.Count > budget) return false;
            foreach (var child in children)
            {
                if (!TryMatrix(child, out var childMatrix, ref budget, depth + 1)) return false;
                matrix.Append(childMatrix);
                if (!Finite(matrix)) return false;
            }
            return true;
        }
        if (type != typeof(MatrixTransform) && type != typeof(TranslateTransform) && type != typeof(ScaleTransform)
            && type != typeof(RotateTransform) && type != typeof(SkewTransform)) return false;
        // Exact framework types only: Value is virtual on Transform, so never call it on
        // an application subclass or on a group containing an unverified child.
        matrix = transform.Value;
        return Finite(matrix);
    }

    private static bool Finite(Matrix matrix) => double.IsFinite(matrix.M11) && double.IsFinite(matrix.M12)
        && double.IsFinite(matrix.M21) && double.IsFinite(matrix.M22) && double.IsFinite(matrix.OffsetX) && double.IsFinite(matrix.OffsetY);

    private static void AddOverlay(List<LayoutOverlay> overlays, List<string> notices, string kind, Rect rect, Matrix frame, string label)
    {
        if (rect.IsEmpty) return;
        if (!Finite(rect) || !Finite(frame))
        {
            notices.Add("Non-finite layout coordinates were omitted from geometry overlays.");
            return;
        }
        var points = new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft };
        frame.Transform(points);
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            notices.Add("A transform produced non-finite coordinates; that geometry overlay was omitted.");
            return;
        }
        overlays.Add(new(kind, points.Select(point => new LayoutPoint(point.X, point.Y)).ToArray(), label));
    }

    private static bool TryGeometryBounds(Geometry geometry, out Rect bounds)
    {
        int budget = 32;
        return TryGeometryBounds(geometry, out bounds, ref budget, 0);
    }

    private static bool TryGeometryBounds(Geometry geometry, out Rect bounds, ref int budget, int depth)
    {
        bounds = Rect.Empty;
        if (--budget < 0 || depth > 8) return false;
        Type type = geometry.GetType();
        if (type == typeof(RectangleGeometry)) bounds = Read<Rect>(geometry, RectangleGeometry.RectProperty);
        else if (type == typeof(EllipseGeometry))
        {
            var center = Read<Point>(geometry, EllipseGeometry.CenterProperty);
            double x = Math.Abs(Read<double>(geometry, EllipseGeometry.RadiusXProperty));
            double y = Math.Abs(Read<double>(geometry, EllipseGeometry.RadiusYProperty));
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(center.X) || !double.IsFinite(center.Y)) return false;
            bounds = new(center.X - x, center.Y - y, x * 2, y * 2);
        }
        else if (type == typeof(LineGeometry))
        {
            var start = Read<Point>(geometry, LineGeometry.StartPointProperty);
            var end = Read<Point>(geometry, LineGeometry.EndPointProperty);
            if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y)) return false;
            bounds = new(start, end);
        }
        else if (type == typeof(CombinedGeometry))
        {
            var first = geometry.GetValue(CombinedGeometry.Geometry1Property) as Geometry;
            var second = geometry.GetValue(CombinedGeometry.Geometry2Property) as Geometry;
            if (first is null || second is null || !TryGeometryBounds(first, out var firstBounds, ref budget, depth + 1)
                || !TryGeometryBounds(second, out var secondBounds, ref budget, depth + 1)) return false;
            bounds = firstBounds;
            switch (Read<GeometryCombineMode>(geometry, CombinedGeometry.GeometryCombineModeProperty))
            {
                case GeometryCombineMode.Intersect: bounds.Intersect(secondBounds); break;
                case GeometryCombineMode.Exclude: break; // A conservative bound; holes are not an outline.
                default: bounds.Union(secondBounds); break;
            }
        }
        else if (type == typeof(GeometryGroup))
        {
            var children = (GeometryCollection)geometry.GetValue(GeometryGroup.ChildrenProperty);
            if (children.Count > budget) return false;
            foreach (var child in children)
            {
                if (!TryGeometryBounds(child, out var childBounds, ref budget, depth + 1)) return false;
                bounds.Union(childBounds);
            }
        }
        else return false; // Do not enumerate path/stream contents or invoke custom Geometry.Bounds.
        if (bounds.IsEmpty) return true;
        if (!Finite(bounds) || !TryMatrix(geometry.GetValue(Geometry.TransformProperty) as Transform, out var matrix)) return false;
        bounds.Transform(matrix);
        return Finite(bounds);
    }
}
