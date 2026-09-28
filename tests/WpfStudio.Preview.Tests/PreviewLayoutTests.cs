using System.Diagnostics;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class PreviewLayoutTests
{
    [Fact]
    public async Task ExplicitPreviewHostReportsRootDipLayoutForTransformedMarginAndClipAndRejectsStaleNodes()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        const string xaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Border Width="250" Height="160" HorizontalAlignment="Left" VerticalAlignment="Top" Margin="20,25,0,0" ClipToBounds="True">
                <Button Name="LayoutTarget" Width="130" Height="42" Margin="10,12,14,16" HorizontalAlignment="Left" VerticalAlignment="Top" RenderTransformOrigin="0.5,0.5">
                  <Button.RenderTransform><RotateTransform Angle="15" /></Button.RenderTransform>
                  <Button.Clip><RectangleGeometry Rect="4,3,100,30" /></Button.Clip>
                </Button>
              </Border>
              <Border Name="MarginTarget" Width="40" Height="20" Margin="8,9,10,11" VerticalAlignment="Bottom" HorizontalAlignment="Left" />
            </Grid>
            """;
        var snapshot = await client.RenderAsync(new("C:/preview/Layout.xaml", xaml, 1, 400, 300));
        Assert.True(snapshot.Success, snapshot.Status);
        using var process = Process.GetProcessById(client.ProcessId!.Value);
        Assert.Equal(PreviewHostUnderTest.ExecutablePath, process.MainModule!.FileName, ignoreCase: true);
        var node = Assert.Single(snapshot.Nodes, node => node.Name == "LayoutTarget");
        var inspection = await client.InspectAsync(new(snapshot.Version, node.Id));
        var layout = Assert.IsType<LayoutSnapshot>(inspection.Layout);
        Assert.True(layout.Available, layout.Status);
        Assert.NotEmpty(layout.Facts);
        Assert.Contains(layout.Overlays, overlay => overlay.Kind == "slot");
        Assert.DoesNotContain(layout.Overlays, overlay => overlay.Kind == "margin");
        Assert.Contains(layout.Notices, notice => notice.Contains("margin outline is omitted", StringComparison.Ordinal));
        Assert.Contains(layout.Overlays, overlay => overlay.Kind == "clip-bounds");
        var render = Assert.Single(layout.Overlays, overlay => overlay.Kind == "render");
        Assert.Equal(4, render.Points.Count);
        Assert.True(Math.Abs(render.Points[0].Y - render.Points[1].Y) > 1, "A rotated render rectangle must remain a polygon, not an axis-aligned bounding rectangle.");
        Assert.Equal(node.Bounds!.X, render.Points.Min(point => point.X), 5);
        Assert.Equal(node.Bounds.Y, render.Points.Min(point => point.Y), 5);
        Assert.Equal(node.Bounds.Width, render.Points.Max(point => point.X) - render.Points.Min(point => point.X), 5);
        Assert.Equal(node.Bounds.Height, render.Points.Max(point => point.Y) - render.Points.Min(point => point.Y), 5);
        Assert.All(layout.Overlays.SelectMany(overlay => overlay.Points), point =>
        {
            Assert.True(double.IsFinite(point.X));
            Assert.True(double.IsFinite(point.Y));
        });
        var marginTarget = Assert.Single(snapshot.Nodes, node => node.Name == "MarginTarget");
        var marginInspection = await client.InspectAsync(new(snapshot.Version, marginTarget.Id));
        Assert.Contains(marginInspection.Layout!.Overlays, overlay => overlay.Kind == "margin");
        Assert.Contains(layout.Facts, fact => fact.Name == "Margin" && fact.Value == "10, 12, 14, 16");

        var changed = await client.SetPropertyAsync(new(snapshot.Version, node.Id, "Width", "180"));
        Assert.True(changed.Success, changed.Error);
        Assert.NotNull(changed.Inspection.Layout);
        var changedRender = Assert.Single(changed.Inspection.Layout.Overlays, overlay => overlay.Kind == "render");
        Assert.True(changedRender.Points.Max(point => point.X) - changedRender.Points.Min(point => point.X) > node.Bounds.Width);

        Assert.True((await client.RenderAsync(new("C:/preview/Layout.xaml", "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'/>", 2))).Success);
        var stale = await client.InspectAsync(new(snapshot.Version, node.Id));
        Assert.Null(stale.Node);
        Assert.Null(stale.Layout);
    }
}
