using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

/// <summary>Reads existing WPF layout state on its dispatcher; never runs layout or application CLR wrappers.</summary>
public static partial class LayoutReader
{
    private const int MaximumAncestors = 64;
    private const int MaximumClipOverlays = 8;

    public static LayoutSnapshot Capture(DependencyObject target, Visual coordinateRoot)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(coordinateRoot);
        if (!target.CheckAccess() || !coordinateRoot.CheckAccess())
            return new(false, [], [], [], "Layout must be read on the target and coordinate root's owning dispatcher.");
        if (target is not UIElement element)
            return new(false, [], [], [], "This object has no UIElement layout state or visual layout box.");

        var facts = new List<LayoutFact>();
        var overlays = new List<LayoutOverlay>();
        var notices = new List<string>();
        try
        {
            var desired = element.DesiredSize;
            var render = element.RenderSize;
            bool measureValid = element.IsMeasureValid, arrangeValid = element.IsArrangeValid;
            var visibility = Read<Visibility>(element, UIElement.VisibilityProperty);
            facts.Add(new("Coordinate frame", "Coordinate-root DIPs", "Overlay vertices use the supplied root visual's local coordinates. One DIP is 1/96 inch; values are not screen pixels."));
            facts.Add(new("Desired size", SizeText(desired), "Cached result of the last measure pass; it can include margin and layout-transform effects."));
            facts.Add(new("Render size", SizeText(render), "The element's local layout box before its visual/render transform; this is not its complete painted or visible region."));
            facts.Add(new("Measure valid", measureValid.ToString()));
            facts.Add(new("Arrange valid", arrangeValid.ToString()));
            facts.Add(new("Visibility", visibility.ToString()));
            facts.Add(new("Is visible", Read<bool>(element, UIElement.IsVisibleProperty).ToString(), "WPF effective visibility; it does not prove that pixels are unobscured."));
            facts.Add(new("Clip to bounds", Read<bool>(element, UIElement.ClipToBoundsProperty).ToString(), "This flag alone is not the complete clip. Cached visual clips are reported separately."));
            facts.Add(new("Local clip", GeometryName(element.GetValue(UIElement.ClipProperty) as Geometry), "Effective Clip dependency-property value, which may come from a style or binding."));
            var renderTransform = element.GetValue(UIElement.RenderTransformProperty) as Transform;
            facts.Add(new("Render transform", TransformText(renderTransform), "Applied after layout; does not change the parent-assigned layout slot."));
            var origin = Read<Point>(element, UIElement.RenderTransformOriginProperty);
            facts.Add(new("Render transform origin", PointText(origin), "Relative to the element's render box."));
            facts.Add(new("Snaps to device pixels", Read<bool>(element, UIElement.SnapsToDevicePixelsProperty).ToString()));
            var dpi = VisualTreeHelper.GetDpi(element);
            facts.Add(new("DPI", $"{Number(dpi.PixelsPerInchX)} × {Number(dpi.PixelsPerInchY)}", $"Scale {Number(dpi.DpiScaleX)} × {Number(dpi.DpiScaleY)}; overlays remain in DIPs."));
            if (element is Border)
            {
                facts.Add(new("Padding", InsetsText(Read<Thickness>(element, Border.PaddingProperty)), "Left, top, right, bottom; internal space around content."));
                facts.Add(new("Border thickness", InsetsText(Read<Thickness>(element, Border.BorderThicknessProperty))));
            }
            else if (element is Control)
            {
                facts.Add(new("Padding", InsetsText(Read<Thickness>(element, Control.PaddingProperty)), "The control template determines how this value is used."));
                facts.Add(new("Border thickness", InsetsText(Read<Thickness>(element, Control.BorderThicknessProperty)), "The control template determines how this value is used."));
            }
            else if (element is TextBlock)
                facts.Add(new("Padding", InsetsText(Read<Thickness>(element, TextBlock.PaddingProperty)), "Left, top, right, bottom around text."));

            Thickness margin = default;
            Rect slot = Rect.Empty;
            bool simpleLayout = false;
            if (element is FrameworkElement framework)
            {
                double width = Read<double>(element, FrameworkElement.WidthProperty), height = Read<double>(element, FrameworkElement.HeightProperty);
                double minWidth = Read<double>(element, FrameworkElement.MinWidthProperty), minHeight = Read<double>(element, FrameworkElement.MinHeightProperty);
                margin = Read<Thickness>(element, FrameworkElement.MarginProperty);
                var horizontal = Read<HorizontalAlignment>(element, FrameworkElement.HorizontalAlignmentProperty);
                var vertical = Read<VerticalAlignment>(element, FrameworkElement.VerticalAlignmentProperty);
                var layoutTransform = element.GetValue(FrameworkElement.LayoutTransformProperty) as Transform;
                simpleLayout = TryMatrix(layoutTransform, out var layoutMatrix) && layoutMatrix.IsIdentity;
                slot = LayoutInformation.GetLayoutSlot(framework); // Cached PreviousArrangeRect; no virtual layout callback.
                facts.Add(new("Actual size", $"{Number(Read<double>(element, FrameworkElement.ActualWidthProperty))} × {Number(Read<double>(element, FrameworkElement.ActualHeightProperty))}", "FrameworkElement's actual width and height, in local DIPs."));
                facts.Add(new("Width", double.IsNaN(width) ? "Auto" : Number(width)));
                facts.Add(new("Height", double.IsNaN(height) ? "Auto" : Number(height)));
                facts.Add(new("Minimum size", $"{Number(minWidth)} × {Number(minHeight)}"));
                facts.Add(new("Maximum size", $"{Number(Read<double>(element, FrameworkElement.MaxWidthProperty))} × {Number(Read<double>(element, FrameworkElement.MaxHeightProperty))}"));
                facts.Add(new("Margin", InsetsText(margin), "Left, top, right, bottom in parent layout DIPs."));
                facts.Add(new("Horizontal alignment", horizontal.ToString()));
                facts.Add(new("Vertical alignment", vertical.ToString()));
                facts.Add(new("Layout slot", RectText(slot), "The cached parent-assigned arrange rectangle, in parent coordinates; it can be larger than the child's render box."));
                facts.Add(new("Layout transform", TransformText(layoutTransform), "Participates in layout; its requested transform need not equal the final visual transform."));
                facts.Add(new("Use layout rounding", Read<bool>(element, FrameworkElement.UseLayoutRoundingProperty).ToString()));
                if (horizontal == HorizontalAlignment.Stretch && double.IsFinite(width))
                    notices.Add("Width is explicit; Stretch does not by itself make the element fill the available width.");
                if (vertical == VerticalAlignment.Stretch && double.IsFinite(height))
                    notices.Add("Height is explicit; Stretch does not by itself make the element fill the available height.");
                if (simpleLayout && Finite(slot) && (minWidth > slot.Width || minHeight > slot.Height))
                    notices.Add("A minimum dimension exceeds the cached layout slot; the render box can extend beyond its allocation or be clipped.");
                if (Finite(slot) && (slot.Width == 0 || slot.Height == 0))
                    notices.Add("The cached parent layout slot has a zero dimension.");
            }
            else notices.Add("This UIElement is not a FrameworkElement; width, margin, alignment and parent layout-slot facts are unavailable.");

            if (visibility == Visibility.Collapsed) notices.Add("Collapsed removes the element from layout; retained geometry must not be read as a current visible box.");
            else if (visibility == Visibility.Hidden) notices.Add("Hidden reserves layout space but does not render the element.");
            if (Finite(render) && (render.Width == 0 || render.Height == 0)) notices.Add("The cached render box has a zero dimension.");
            if (!Finite(render) || !Finite(desired)) notices.Add("A cached size is empty or non-finite; no non-finite numbers are included in overlay coordinates.");
            notices.Add("The original measure constraint is not exposed by this capture. Clip bounds, effects, opacity, occlusion and overlapping windows do not establish an exact visible region.");

            var chain = Ancestors(element, coordinateRoot);
            bool pending = !measureValid || !arrangeValid || chain?.OfType<UIElement>().Any(item => !item.IsMeasureValid || !item.IsArrangeValid) == true;
            bool collapsed = visibility == Visibility.Collapsed || chain?.OfType<UIElement>().Any(item => Read<Visibility>(item, UIElement.VisibilityProperty) == Visibility.Collapsed) == true;
            string? status = null;
            Dictionary<Visual, Matrix>? frames = null;
            if (chain is null) status = "The coordinate root is not within the bounded 2D ancestor chain; overlays are unavailable.";
            else if (pending) status = "Layout is pending; facts are cached values and overlays are withheld until layout completes.";
            else if (collapsed) status = "The selected element or an ancestor is Collapsed; overlays are withheld.";
            else if (!TryFrames(chain, notices, out frames)) status = "A visual transform or effect cannot be mapped passively; overlays are unavailable.";
            if (status is not null) notices.Add(status);

            if (frames is not null && Finite(render))
            {
                AddOverlay(overlays, notices, "render", new Rect(new Point(), render), frames[element], "Render box · coordinate-root DIPs");
                if (chain is { Count: > 1 } && element is FrameworkElement)
                {
                    var parent = chain[1];
                    AddOverlay(overlays, notices, "slot", slot, frames[parent], "Parent layout slot · mapped to coordinate-root DIPs");
                    if (simpleLayout && TryMatrix(VisualTreeHelper.GetTransform(element), out var visualMatrix) && visualMatrix.IsIdentity
                        && Finite(margin) && margin.Left >= 0 && margin.Top >= 0 && margin.Right >= 0 && margin.Bottom >= 0)
                    {
                        var offset = VisualTreeHelper.GetOffset(element);
                        var marginBox = new Rect(offset.X - margin.Left, offset.Y - margin.Top,
                            render.Width + margin.Left + margin.Right, render.Height + margin.Top + margin.Bottom);
                        AddOverlay(overlays, notices, "margin", marginBox, frames[parent], "Render box plus declared margin · mapped from parent DIPs; not the assigned slot");
                    }
                    else notices.Add("A margin outline is omitted for transformed or negative-margin layout; inflating the transformed render box would be misleading.");
                }
                else if (element is FrameworkElement) notices.Add("The selected element is the coordinate root; its parent layout slot and margin have no mapping inside this frame.");
            }

            int ancestorClips = 0, clipOverlays = 0;
            foreach (var visual in chain ?? [element])
            {
                var clip = VisualTreeHelper.GetClip(visual); // Cached combined clip; never calls virtual GetLayoutClip.
                if (ReferenceEquals(visual, element)) facts.Add(new("Cached visual clip", GeometryName(clip), "The cached combined visual clip can include layout and explicit clipping. Its outline is a bounding approximation."));
                if (clip is null) continue;
                bool local = ReferenceEquals(visual, element);
                if (!local) ancestorClips++;
                if (frames is null) continue;
                if (clipOverlays >= MaximumClipOverlays) { notices.Add("Additional ancestor clip outlines were omitted after the clip budget was reached."); break; }
                if (TryGeometryBounds(clip, out var bounds))
                {
                    AddOverlay(overlays, notices, "clip-bounds", bounds, frames[visual], local
                        ? "Selected cached visual clip · bounding approximation in coordinate-root DIPs"
                        : "Ancestor cached visual clip · bounding approximation in coordinate-root DIPs");
                    clipOverlays++;
                }
                else notices.Add($"{(local ? "Selected" : "Ancestor")} cached {GeometryName(clip)} bounds were omitted; custom or complex geometry is not evaluated.");
            }
            facts.Add(new("Ancestor clipping", ancestorClips == 0 ? "No cached clips observed" : $"{ancestorClips} cached clip(s) observed",
                "Only the bounded visual chain through the coordinate root is considered; this is not a visibility verdict."));

            // Passive property reads should not reenter application code. Still reject geometry
            // if the visible parent chain or cached sizes changed during this observation.
            var currentChain = Ancestors(element, coordinateRoot);
            if (element.DesiredSize != desired || element.RenderSize != render || element.IsMeasureValid != measureValid
                || element.IsArrangeValid != arrangeValid || chain is not null && (currentChain is null || !chain.SequenceEqual(currentChain))
                || !pending && currentChain?.OfType<UIElement>().Any(item => !item.IsMeasureValid || !item.IsArrangeValid) == true)
            {
                overlays.Clear();
                status = "Layout changed during capture; refresh to obtain a consistent geometry snapshot.";
                notices.Add(status);
            }
            return new(true, facts.OrderBy(fact => fact.Name switch
            { "Desired size" => 0, "Render size" => 1, "Layout slot" => 2, "Actual size" => 3, _ => 4 }).ToArray(),
                overlays, notices.Distinct().Take(24).ToArray(), status);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new(false, facts, [], notices.Distinct().Take(24).ToArray(), $"Cached layout state could not be read ({TypeName(exception)}).");
        }
    }

    private static List<Visual>? Ancestors(Visual target, Visual root)
    {
        var chain = new List<Visual>();
        for (Visual? visual = target; visual is not null && chain.Count < MaximumAncestors; visual = VisualTreeHelper.GetParent(visual) as Visual)
        {
            chain.Add(visual);
            if (ReferenceEquals(visual, root)) return chain;
        }
        return null;
    }

    private static T Read<T>(DependencyObject target, DependencyProperty property) => (T)target.GetValue(property);
    private static string Number(double value) => double.IsPositiveInfinity(value) ? "Unbounded" : double.IsNegativeInfinity(value) ? "−Infinity"
        : double.IsNaN(value) ? "Unavailable (NaN)" : value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string SizeText(Size size) => size.IsEmpty ? "Empty" : $"{Number(size.Width)} × {Number(size.Height)} DIPs";
    private static string PointText(Point point) => $"{Number(point.X)}, {Number(point.Y)}";
    private static string InsetsText(Thickness value) => $"{Number(value.Left)}, {Number(value.Top)}, {Number(value.Right)}, {Number(value.Bottom)}";
    private static string RectText(Rect rect) => rect.IsEmpty ? "Empty" : $"x={Number(rect.X)}, y={Number(rect.Y)}, width={Number(rect.Width)}, height={Number(rect.Height)}";
    private static string GeometryName(Geometry? geometry) => geometry is null ? "None" : TypeName(geometry);
    private static string TypeName(object value)
    {
        string name = value.GetType().Name;
        return name.Length <= 256 ? name : name[..256] + "…";
    }
    private static bool Finite(Size size) => !size.IsEmpty && double.IsFinite(size.Width) && double.IsFinite(size.Height);
    private static bool Finite(Rect rect) => !rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);
    private static bool Finite(Thickness value) => double.IsFinite(value.Left) && double.IsFinite(value.Top) && double.IsFinite(value.Right) && double.IsFinite(value.Bottom);
}
