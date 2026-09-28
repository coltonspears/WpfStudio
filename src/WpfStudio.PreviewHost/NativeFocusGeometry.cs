using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

/// <summary>Maps only a cached render box; never asks application code to measure, arrange or supply bounds.</summary>
internal static class NativeFocusGeometry
{
    internal static PreviewBounds? Capture(UIElement element, Visual root, out string? status)
    {
        status = "The focused layout box is unavailable.";
        try
        {
            var size = element.RenderSize;
            if (!Finite(size.Width) || !Finite(size.Height) || size.Width <= 0 || size.Height <= 0) return null;
            var chain = new List<Visual>();
            Visual? current = element;
            while (current is not null && chain.Count < 64)
            {
                chain.Add(current);
                if (current is UIElement layout && (!layout.IsMeasureValid || !layout.IsArrangeValid))
                { status = "The focused layout box is pending layout."; return null; }
                if (ReferenceEquals(current, root)) break;
                current = VisualTreeHelper.GetParent(current) as Visual;
            }
            if (chain.Count == 0 || !ReferenceEquals(chain[^1], root))
            { status = "The focused object has no bounded 2D path to this presentation root."; return null; }

            var matrix = Matrix.Identity;
            int transforms = 64;
            foreach (var visual in chain)
            {
                var effect = VisualTreeHelper.GetEffect(visual);
                if (effect is not null && effect.GetType() != typeof(BlurEffect) && effect.GetType() != typeof(DropShadowEffect))
                { status = "Custom effect coordinate mapping is not evaluated for focus scrolling."; return null; }
                if (ReferenceEquals(visual, root)) break;
                if (!TryMatrix(VisualTreeHelper.GetTransform(visual), ref transforms, 0, out var transform))
                { status = "The focused visual transform cannot be mapped passively."; return null; }
                var offset = VisualTreeHelper.GetOffset(visual);
                if (!Finite(offset.X) || !Finite(offset.Y)) return null;
                transform.Translate(offset.X, offset.Y);
                matrix.Append(transform);
                if (!Finite(matrix)) return null;
            }
            var bounds = new Rect(new Point(), size);
            bounds.Transform(matrix);
            if (bounds.IsEmpty || !Finite(bounds.X) || !Finite(bounds.Y) || !Finite(bounds.Width) || !Finite(bounds.Height)) return null;
            // Verify attachment and cached layout after capture without traversing
            // descendants or evaluating clipping/effect ink extents.
            for (int index = 0; index + 1 < chain.Count; index++)
                if (!ReferenceEquals(VisualTreeHelper.GetParent(chain[index]), chain[index + 1])) return null;
            if (size != element.RenderSize || !element.IsMeasureValid || !element.IsArrangeValid) return null;
            status = null;
            return new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            status = "The focused cached layout box changed during observation.";
            return null;
        }
    }

    private static bool TryMatrix(Transform? transform, ref int budget, int depth, out Matrix matrix)
    {
        matrix = Matrix.Identity;
        if (transform is null) return true;
        if (--budget < 0 || depth > 8) return false;
        // Even an exact framework transform can have an application-defined
        // AnimationTimeline behind a scalar property. Value would evaluate that
        // clock. This flag is cached; omit the box instead of advancing animation.
        if (transform.HasAnimatedProperties) return false;
        var type = transform.GetType();
        if (type == typeof(TransformGroup))
        {
            var children = ((TransformGroup)transform).Children;
            if (children.Count > budget) return false;
            foreach (var child in children)
            {
                if (!TryMatrix(child, ref budget, depth + 1, out var part)) return false;
                matrix.Append(part);
                if (!Finite(matrix)) return false;
            }
            return true;
        }
        if (type != typeof(MatrixTransform) && type != typeof(TranslateTransform) && type != typeof(ScaleTransform) &&
            type != typeof(RotateTransform) && type != typeof(SkewTransform)) return false;
        matrix = transform.Value; // Exact framework types only; Value is otherwise virtual.
        return Finite(matrix);
    }

    private static bool Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= 1_000_000;
    private static bool Finite(Matrix matrix) => Finite(matrix.M11) && Finite(matrix.M12) && Finite(matrix.M21) &&
        Finite(matrix.M22) && Finite(matrix.OffsetX) && Finite(matrix.OffsetY);
}
