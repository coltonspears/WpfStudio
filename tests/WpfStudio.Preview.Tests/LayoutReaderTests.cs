using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class LayoutReaderTests(PreviewFixture fixture)
{
    [Fact]
    public void SlotRenderAndMarginUseTheirOwnCoordinateSpaces() => fixture.OnDispatcher(() =>
    {
        var child = new Border { Width = 80, Height = 30, Margin = new(10, 5, 20, 15),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Padding = new(2, 3, 4, 5), BorderThickness = new(1) };
        var root = Arrange(child);

        var snapshot = LayoutReader.Capture(child, root);

        Assert.True(snapshot.Available, snapshot.Status);
        Assert.Null(snapshot.Status);
        Assert.Equal("110 × 50 DIPs", Fact(snapshot, "Desired size"));
        Assert.Equal("80 × 30 DIPs", Fact(snapshot, "Render size"));
        Assert.Equal("10, 5, 20, 15", Fact(snapshot, "Margin"));
        Assert.Equal("2, 3, 4, 5", Fact(snapshot, "Padding"));
        Assert.Equal("1, 1, 1, 1", Fact(snapshot, "Border thickness"));
        Assert.Equal(new[] { "Desired size", "Render size", "Layout slot" }, snapshot.Facts.Take(3).Select(fact => fact.Name));
        AssertRectangle(Overlay(snapshot, "render"), new Rect(10, 5, 80, 30));
        AssertRectangle(Overlay(snapshot, "slot"), new Rect(0, 0, 240, 140));
        AssertRectangle(Overlay(snapshot, "margin"), new Rect(0, 0, 110, 50));
        return true;
    });

    [Fact]
    public void RenderTransformAndParentTransformMatchWpfMappingWithoutChangingSlot() => fixture.OnDispatcher(() =>
    {
        var child = new Border { Width = 45, Height = 20, Margin = new(7), RenderTransform = new RotateTransform(30), RenderTransformOrigin = new(.5, .5) };
        var parent = new Grid { Margin = new(13, 9, 5, 4), RenderTransform = new ScaleTransform(1.2, .8) };
        parent.Children.Add(child);
        var root = Arrange(parent);

        var snapshot = LayoutReader.Capture(child, root);

        Assert.True(snapshot.Available, snapshot.Status);
        var expected = new[] { new Point(), new Point(45, 0), new Point(45, 20), new Point(0, 20) }
            .Select(child.TransformToAncestor(root).Transform).ToArray();
        AssertPoints(Overlay(snapshot, "render").Points, expected);
        var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(child);
        AssertPoints(Overlay(snapshot, "slot").Points, new[] { slot.TopLeft, slot.TopRight, slot.BottomRight, slot.BottomLeft }
            .Select(parent.TransformToAncestor(root).Transform).ToArray());
        Assert.DoesNotContain(snapshot.Overlays, overlay => overlay.Kind == "margin");
        Assert.Contains(snapshot.Notices, notice => notice.Contains("margin outline", StringComparison.OrdinalIgnoreCase));
        return true;
    });

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void RightToLeftMirroringAgreesWithWpfVisualCoordinateMapping(FlowDirection childDirection) => fixture.OnDispatcher(() =>
    {
        var child = new Border { Width = 70, Height = 30, Margin = new(3, 7, 17, 9),
            FlowDirection = childDirection, HorizontalAlignment = HorizontalAlignment.Right };
        var parent = new Grid { FlowDirection = FlowDirection.RightToLeft, Margin = new(11, 5, 19, 13) };
        parent.Children.Add(child);
        var root = Arrange(parent);

        var snapshot = LayoutReader.Capture(child, root);

        Assert.True(snapshot.Available, snapshot.Status);
        Assert.Null(snapshot.Status);
        AssertPoints(Overlay(snapshot, "render").Points, new[] { new Point(), new Point(70, 0), new Point(70, 30), new Point(0, 30) }
            .Select(child.TransformToAncestor(root).Transform).ToArray());
        var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(child);
        AssertPoints(Overlay(snapshot, "slot").Points, new[] { slot.TopLeft, slot.TopRight, slot.BottomRight, slot.BottomLeft }
            .Select(parent.TransformToAncestor(root).Transform).ToArray());
        return true;
    });

    [Fact]
    public void CaptureDoesNotRunLayoutCallbacksOrApplicationClrWrappers() => fixture.OnDispatcher(() =>
    {
        var child = new LayoutProbe();
        child.SetValue(FrameworkElement.WidthProperty, 80d);
        var root = Arrange(child);
        var before = (child.Measures, child.Arranges, child.ClipCalls);

        var snapshot = LayoutReader.Capture(child, root);

        Assert.True(snapshot.Available, snapshot.Status);
        Assert.Equal(before, (child.Measures, child.Arranges, child.ClipCalls));
        Assert.Equal(0, child.ApplicationGetterCalls);
        Assert.Equal("80", Fact(snapshot, "Width"));
        Assert.Contains(snapshot.Overlays, overlay => overlay.Kind == "clip-bounds");
        return true;
    });

    [Fact]
    public void PendingLayoutKeepsFactsAndWithholdsGeometryWithoutUpdatingLayout() => fixture.OnDispatcher(() =>
    {
        var child = new LayoutProbe();
        var root = Arrange(child);
        child.InvalidateMeasure();
        var before = (child.Measures, child.Arranges, child.ClipCalls);

        var snapshot = LayoutReader.Capture(child, root);

        Assert.True(snapshot.Available);
        Assert.Contains("pending", snapshot.Status!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("False", Fact(snapshot, "Measure valid"));
        Assert.Empty(snapshot.Overlays);
        Assert.Equal(before, (child.Measures, child.Arranges, child.ClipCalls));
        return true;
    });

    [Fact]
    public void AncestorClipBoundsAreReportedAsAnApproximation() => fixture.OnDispatcher(() =>
    {
        var child = new Border { Width = 380, Height = 80, Margin = new(12, 8, 10, 6) };
        var root = Arrange(child, 320, 180);
        root.ClipToBounds = true;
        root.Arrange(new Rect(0, 0, 320, 180));

        var snapshot = LayoutReader.Capture(child, root);

        Assert.True(snapshot.Available, snapshot.Status);
        var ancestor = Assert.Single(snapshot.Overlays, overlay => overlay.Kind == "clip-bounds" && overlay.Label.StartsWith("Ancestor"));
        Assert.Contains("approximation", ancestor.Label);
        AssertRectangle(ancestor, new Rect(0, 0, 320, 180));
        Assert.Contains(snapshot.Notices, notice => notice.Contains("exact visible region"));
        return true;
    });

    [Fact]
    public void ComplexClipOmissionDoesNotHideIndependentRenderGeometry() => fixture.OnDispatcher(() =>
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 0), true, true);
            context.LineTo(new Point(30, 0), true, false);
            context.LineTo(new Point(15, 20), true, false);
        }
        var child = new Border { Width = 80, Height = 30, Clip = geometry };
        var root = Arrange(child);

        var snapshot = LayoutReader.Capture(child, root);

        Assert.True(snapshot.Available, snapshot.Status);
        Assert.Contains(snapshot.Overlays, overlay => overlay.Kind == "render");
        Assert.Contains(snapshot.Notices, notice => notice.Contains("geometry is not evaluated"));
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandardEffectsKeepLayoutBoxesAndExplicitlyExcludeTheirInk(bool shadow) => fixture.OnDispatcher(() =>
    {
        var child = new Border { Width = 80, Height = 30, Effect = shadow ? new DropShadowEffect() : new BlurEffect() };
        var root = Arrange(child);

        var snapshot = LayoutReader.Capture(child, root);

        Assert.True(snapshot.Available, snapshot.Status);
        Assert.Contains(snapshot.Overlays, overlay => overlay.Kind == "render");
        Assert.Contains(snapshot.Notices, notice => notice.Contains("ink extents are excluded"));
        return true;
    });

    [Theory]
    [InlineData(Visibility.Hidden)]
    [InlineData(Visibility.Collapsed)]
    public void VisibilityExplainsHiddenSpaceAndCollapsedGeometry(Visibility visibility) => fixture.OnDispatcher(() =>
    {
        var child = new Border { Width = 80, Height = 30, Visibility = visibility };
        var root = Arrange(child);
        var snapshot = LayoutReader.Capture(child, root);
        Assert.True(snapshot.Available);
        Assert.Equal(visibility.ToString(), Fact(snapshot, "Visibility"));
        Assert.Contains(snapshot.Notices, notice => notice.StartsWith(visibility.ToString()));
        if (visibility == Visibility.Collapsed) Assert.Empty(snapshot.Overlays);
        return true;
    });

    [Fact]
    public void AutoUnboundedAndFiniteGeometrySerializeWithStrictJson() => fixture.OnDispatcher(() =>
    {
        var child = new Border { Margin = new(-2, 0, 0, 0) };
        var root = Arrange(child);
        var snapshot = LayoutReader.Capture(child, root);
        Assert.Equal("Auto", Fact(snapshot, "Width"));
        Assert.Equal("Unbounded × Unbounded", Fact(snapshot, "Maximum size"));
        Assert.DoesNotContain(snapshot.Overlays, overlay => overlay.Kind == "margin");
        Assert.All(snapshot.Overlays.SelectMany(overlay => overlay.Points), point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Assert.True(parsed.RootElement.GetProperty("Available").GetBoolean());
        return true;
    });

    [Fact]
    public void NonvisualUnrelatedAndOverBudgetAncestryAreExplicitlyUnavailable() => fixture.OnDispatcher(() =>
    {
        var child = new Border { Width = 10, Height = 10 };
        var root = Arrange(child);
        Assert.False(LayoutReader.Capture(new DependencyObject(), root).Available);
        var unrelated = LayoutReader.Capture(child, new Grid());
        Assert.Empty(unrelated.Overlays);
        Assert.Contains("ancestor chain", unrelated.Status!);

        UIElement current = new Border { Width = 10, Height = 10 };
        var leaf = current;
        for (int index = 0; index < 70; index++) current = new Border { Child = current };
        var deep = Arrange(current);
        var bounded = LayoutReader.Capture(leaf, deep);
        Assert.Empty(bounded.Overlays);
        Assert.Contains("bounded", bounded.Status!);
        return true;
    });

    private static Grid Arrange(UIElement child, double width = 240, double height = 140)
    {
        var root = new Grid { Width = width, Height = height };
        root.Children.Add(child);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        return root;
    }

    private static string Fact(LayoutSnapshot snapshot, string name) => Assert.Single(snapshot.Facts, fact => fact.Name == name).Value;
    private static LayoutOverlay Overlay(LayoutSnapshot snapshot, string kind) => Assert.Single(snapshot.Overlays, overlay => overlay.Kind == kind);
    private static void AssertRectangle(LayoutOverlay overlay, Rect rect) => AssertPoints(overlay.Points, [rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft]);
    private static void AssertPoints(IReadOnlyList<LayoutPoint> actual, IReadOnlyList<Point> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].X, actual[index].X, 6);
            Assert.Equal(expected[index].Y, actual[index].Y, 6);
        }
    }

    private sealed class LayoutProbe : FrameworkElement
    {
        public int Measures, Arranges, ClipCalls, ApplicationGetterCalls;
        public new double Width { get { ApplicationGetterCalls++; throw new InvalidOperationException("Application getter must not run."); } }
        protected override Size MeasureOverride(Size availableSize) { Measures++; return new Size(20, 10); }
        protected override Size ArrangeOverride(Size finalSize) { Arranges++; return finalSize; }
        protected override Geometry GetLayoutClip(Size layoutSlotSize) { ClipCalls++; return new RectangleGeometry(new Rect(0, 0, 12, 8)); }
    }
}
