using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.PreviewHost;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class PreviewLayoutEditingTests(PreviewFixture fixture)
{
    private const string Namespaces = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:p='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'";
    private PreviewEngine Engine => fixture.Engine;
    private static PreviewRequest Request(string text, long version = 1901) => new("C:/preview/Layout.xaml", text, version, 400, 300);

    [Theory]
    [InlineData("Canvas", "Canvas.Left='23' Canvas.Top='31'", 26, 36)]
    [InlineData("Grid", "HorizontalAlignment='Left' VerticalAlignment='Top'", 3, 5)]
    public async Task CapturesExactAuthoredGeometryCanonicalPropertiesAndStableValidation(string panel, string placement, double x, double y)
    {
        string text = $"<{panel} {Namespaces}><Button x:Name='Subject' Width='80' Height='40' Margin='3,5,7,11' {placement}>Text &amp; more</Button><Border Width='20' Height='10'/></{panel}>";
        var rendered = await Render(text);
        var context = await Context(rendered);
        Assert.True(context.Available, context.Status);
        Assert.NotNull(context.Token);
        Assert.Equal(panel, context.ParentKind);
        Assert.Equal(new PreviewBounds(x, y, 80, 40), context.Bounds);
        Assert.Equal(new PreviewBounds(0, 0, 400, 300), context.ParentBounds);
        Assert.Equal(new PreviewLayoutInsets(3, 5, 7, 11), context.Margin);
        Assert.Equal(text.IndexOf("<Button", StringComparison.Ordinal), context.Element!.Start);
        Assert.Equal(0, context.Parent!.Start);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), context.SourceHash);
        Assert.Equal(rendered.Surface, context.Surface);
        var width = Assert.Single(context.EditProperties!, item => item.Name == "Width");
        Assert.Equal("System.Windows.FrameworkElement", width.OwnerType);
        Assert.Equal("PresentationFramework", width.OwnerAssembly);
        Assert.False(width.IsAttached);
        Assert.Equal("Content", width.ContentProperty);
        Assert.True(width.CanWriteSource);
        if (panel == "Canvas")
        {
            var left = Assert.Single(context.EditProperties!, item => item.Name == "Canvas.Left");
            Assert.True(left.IsAttached);
            Assert.Equal("System.Windows.Controls.Canvas", left.OwnerType);
            Assert.Equal(23, context.CanvasLeft);
        }
        Assert.NotEmpty(context.Siblings!);
        Assert.Equal(context.Token, (await Context(rendered)).Token);
        var request = new PreviewLayoutValidationRequest(rendered.Version, context.NodeId, context.Token!, context.Surface, new(Width: 90));
        var validation = await Engine.ValidateLayoutEditAsync(request, default);
        Assert.True(validation.Success, validation.Error);
        Assert.Equal(request, validation.Request);
        Assert.Equal(80, (await Context(rendered)).Width); // Validation never mutates the live object.
    }

    [Fact]
    public async Task WindowReplacementStillAllowsExactDescendantPanelChildren()
    {
        var rendered = await Render($"<Window {Namespaces}><Grid><Canvas Margin='10,20,0,0'><Border x:Name='Subject' Canvas.Left='4' Canvas.Top='7' Width='20' Height='30'/></Canvas></Grid></Window>");
        var context = await Context(rendered);
        Assert.True(context.Available, context.Status);
        Assert.Equal(new PreviewBounds(14, 27, 20, 30), context.Bounds);
        Assert.Equal(10, context.ParentBounds!.X);
        Assert.Equal(20, context.ParentBounds.Y);
        var root = Assert.Single(rendered.Nodes, node => node.ParentId is null);
        Assert.False((await Engine.InspectAsync(new(rendered.Version, root.Id), default)).LayoutEditing!.Available);
    }

    [Theory]
    [InlineData("<StackPanel><Border x:Name='Subject' Width='50' Height='40'/></StackPanel>", "Canvas and Grid")]
    [InlineData("<Grid FlowDirection='RightToLeft'><Border x:Name='Subject' Width='50' Height='40'/></Grid>", "Right-to-left")]
    [InlineData("<Grid><Grid.RenderTransform><ScaleTransform ScaleX='2'/></Grid.RenderTransform><Border x:Name='Subject' Width='50' Height='40'/></Grid>", "1:1")]
    [InlineData("<Grid><Border x:Name='Subject' Width='50' Height='40'><Border.RenderTransform><TranslateTransform X='5'/></Border.RenderTransform></Border></Grid>", "identity")]
    [InlineData("<Grid><Border x:Name='Subject' Width='{Binding ActualWidth, RelativeSource={RelativeSource AncestorType=Grid}}' Height='40'/></Grid>", "expression")]
    [InlineData("<Grid><Border x:Name='Subject' Width='50' d:Width='60' Height='40'/></Grid>", "design-time")]
    [InlineData("<Grid><Button><Button.Template><ControlTemplate TargetType='Button'><Canvas><Border x:Name='Subject' Width='50' Height='40'/></Canvas></ControlTemplate></Button.Template></Button></Grid>", "Template")]
    public async Task UnsupportedSourceAndCoordinateContextsRemainExplicit(string content, string reason)
    {
        var rendered = await Render($"<Grid {Namespaces}>{content}</Grid>");
        var context = await Context(rendered);
        Assert.False(context.Available);
        Assert.Null(context.Token);
        Assert.Contains(reason, context.Status!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidationRejectsForgedSurfaceTokenVersionAndInvalidProposals()
    {
        var rendered = await Render($"<Grid {Namespaces}><Border x:Name='Subject' Width='80' Height='40' MinWidth='20' MaxWidth='100'/></Grid>");
        var context = await Context(rendered);
        Assert.True(context.Available, context.Status);
        var valid = new PreviewLayoutValidationRequest(rendered.Version, context.NodeId, context.Token!, context.Surface);
        foreach (var request in new[]
        {
            valid with { Version = rendered.Version + 1 }, valid with { Token = "forged" }, valid with { NodeId = "forged" },
            valid with { Surface = null }, valid with { Values = new(Width: 101) }, valid with { Values = new(Height: -1) },
            valid with { Values = new(CanvasLeft: 3) }, valid with { Values = new(GridColumn: 2) },
            valid with { Values = new(HorizontalAlignment: "NoSuchAlignment") }
        }) Assert.False((await Engine.ValidateLayoutEditAsync(request, default)).Success);
        Assert.True((await Engine.ValidateLayoutEditAsync(valid, default)).Success);
    }

    [Fact]
    public async Task LayoutChangesTemporaryOverridesAndRerenderInvalidatePriorObservation()
    {
        string text = $"<Grid {Namespaces}><p:LayoutAuthoringProbe x:Name='Subject' Width='80' Height='40'/></Grid>";
        var rendered = await Render(text);
        var context = await Context(rendered);
        Assert.True(context.Available, context.Status);
        var request = new PreviewLayoutValidationRequest(rendered.Version, context.NodeId, context.Token!, context.Surface);
        fixture.OnDispatcher(() => { LayoutAuthoringProbe.Last!.Margin = new(3); return true; });
        Assert.False((await Engine.ValidateLayoutEditAsync(request, default)).Success);
        await Engine.CaptureAsync(new(rendered.Version), default);
        var changed = await Context(rendered);
        Assert.True(changed.Available, changed.Status);
        Assert.NotEqual(context.Token, changed.Token);
        var edited = await Engine.SetPropertyAsync(new(rendered.Version, context.NodeId, "Width", "90"), default);
        Assert.True(edited.Success, edited.Error);
        Assert.False(edited.Inspection.LayoutEditing!.Available);
        Assert.Contains("override", edited.Inspection.LayoutEditing.Status!, StringComparison.OrdinalIgnoreCase);
        await Render(text, rendered.Version); // Same editor version, different objects and surface.
        Assert.False((await Engine.ValidateLayoutEditAsync(request, default)).Success);
    }

    [Fact]
    public async Task ReparentingCannotBorrowAnotherAuthoredPanelAndObservationDoesNotRunLayout()
    {
        var rendered = await Render($"<Grid {Namespaces}><Canvas x:Name='First'><p:LayoutAuthoringProbe x:Name='Subject' Width='80' Height='40'/></Canvas><Canvas x:Name='Other'/></Grid>");
        var initial = fixture.OnDispatcher(() => (LayoutAuthoringProbe.Last!.Measures, LayoutAuthoringProbe.Last.Arranges, LayoutAuthoringProbe.Last.Clips));
        var context = await Context(rendered);
        Assert.True(context.Available, context.Status);
        Assert.True((await Engine.ValidateLayoutEditAsync(new(rendered.Version, context.NodeId, context.Token!, context.Surface), default)).Success);
        var after = fixture.OnDispatcher(() => (LayoutAuthoringProbe.Last!.Measures, LayoutAuthoringProbe.Last.Arranges, LayoutAuthoringProbe.Last.Clips));
        Assert.Equal(initial, after);
        fixture.OnDispatcher(() =>
        {
            var child = LayoutAuthoringProbe.Last!;
            var first = (Canvas)child.Parent;
            var root = (Grid)first.Parent;
            first.Children.Remove(child);
            ((Canvas)root.Children[1]).Children.Add(child);
            return true;
        });
        await Engine.CaptureAsync(new(rendered.Version), default);
        var moved = await Context(rendered);
        Assert.False(moved.Available);
        Assert.Contains("authored parent", moved.Status!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveAnimationsDoNotProduceAnEditableContext()
    {
        var rendered = await Render($"<Grid {Namespaces}><p:LayoutAuthoringProbe x:Name='Subject' Width='80' Height='40'/></Grid>");
        fixture.OnDispatcher(() =>
        {
            var clock = (AnimationClock)new DoubleAnimation(80, 90, TimeSpan.FromMinutes(1)).CreateClock(true);
            LayoutAuthoringProbe.Last!.ApplyAnimationClock(FrameworkElement.WidthProperty, clock);
            clock.Controller!.Begin();
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(1), TimeSeekOrigin.BeginTime);
            Assert.True(DependencyPropertyHelper.GetValueSource(LayoutAuthoringProbe.Last, FrameworkElement.WidthProperty).IsAnimated);
            return true;
        });
        var context = await Context(rendered);
        Assert.False(context.Available);
        Assert.Null(context.Token);
    }

    [Theory]
    [InlineData("Canvas", "Canvas.Left='23' Canvas.Top='31'", XamlLayoutHandle.Move)]
    [InlineData("Canvas", "Canvas.Right='23' Canvas.Bottom='31'", XamlLayoutHandle.Move)]
    [InlineData("Canvas", "Canvas.Right='23' Canvas.Bottom='31'", XamlLayoutHandle.TopLeft)]
    [InlineData("Canvas", "Canvas.Right='23' Canvas.Bottom='31'", XamlLayoutHandle.BottomRight)]
    [InlineData("Canvas", "", XamlLayoutHandle.Move)]
    [InlineData("Grid", "HorizontalAlignment='Left' VerticalAlignment='Top'", XamlLayoutHandle.Move)]
    [InlineData("Grid", "HorizontalAlignment='Center' VerticalAlignment='Center'", XamlLayoutHandle.TopLeft)]
    [InlineData("Grid", "HorizontalAlignment='Right' VerticalAlignment='Bottom'", XamlLayoutHandle.BottomRight)]
    [InlineData("Grid", "", XamlLayoutHandle.TopLeft)]
    [InlineData("Grid", "", XamlLayoutHandle.BottomRight)]
    [InlineData("Grid", "Width='Auto' Height='Auto'", XamlLayoutHandle.Move)]
    [InlineData("Grid", "Width='Auto' Height='Auto'", XamlLayoutHandle.BottomRight)]
    public async Task PlannedGestureRerendersAtItsProposedBounds(string panel, string placement, XamlLayoutHandle handle)
    {
        string dimensions = placement.Contains("Width=", StringComparison.Ordinal) ? "" : "Width='80' Height='40'";
        string text = $"<{panel} {Namespaces}><Border x:Name='Subject' {dimensions} Margin='3,5,7,11' {placement} Background='Blue'/></{panel}>";
        var rendered = await Render(text);
        var context = await Context(rendered);
        Assert.True(context.Available, context.Status);
        var draft = XamlLayoutEditService.Calculate(context, new(handle, 13, 9, BypassSnapping: true));
        Assert.True(draft.Success, draft.Error);
        var proposal = XamlLayoutEditService.CreateEdit(new("C:/preview/Layout.xaml", text, rendered.Version,
            rendered.Version, context.SourceHash!, context, draft.Bounds!, handle));
        Assert.True(proposal.Success, proposal.Error);
        string after = WorkspaceEditTransaction.ApplyTextEdits(text, proposal.Edit!.Edits);
        var actual = (await Context(await Render(after, rendered.Version + 1))).Bounds!;
        Assert.Equal(draft.Bounds!.X, actual.X, 5);
        Assert.Equal(draft.Bounds.Y, actual.Y, 5);
        Assert.Equal(draft.Bounds.Width, actual.Width, 5);
        Assert.Equal(draft.Bounds.Height, actual.Height, 5);
    }

    private async Task<PreviewSnapshot> Render(string text, long version = 1901)
    {
        var rendered = await Engine.RenderAsync(Request(text, version), default);
        Assert.True(rendered.Success, string.Join("\n", rendered.Diagnostics.Select(item => item.Message)));
        return rendered;
    }

    private async Task<PreviewLayoutEditContext> Context(PreviewSnapshot rendered)
    {
        var node = Assert.Single(rendered.Nodes, item => item.Name == "Subject");
        return Assert.IsType<PreviewLayoutEditContext>((await Engine.InspectAsync(new(rendered.Version, node.Id), default)).LayoutEditing);
    }
}

public sealed class LayoutAuthoringProbe : Border
{
    public static LayoutAuthoringProbe? Last { get; private set; }
    public int Measures { get; private set; }
    public int Arranges { get; private set; }
    public int Clips { get; private set; }
    public LayoutAuthoringProbe() => Last = this;
    protected override Size MeasureOverride(Size constraint) { Measures++; return base.MeasureOverride(constraint); }
    protected override Size ArrangeOverride(Size arrangeSize) { Arranges++; return base.ArrangeOverride(arrangeSize); }
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) { Clips++; return base.GetLayoutClip(layoutSlotSize); }
}
