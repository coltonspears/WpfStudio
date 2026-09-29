using WpfStudio.Contracts;
using WpfStudio.PreviewHost;

namespace WpfStudio.Preview.Tests;

/// <summary>Designer-facing host behavior: artboard sizing from the root and authored-element picking.</summary>
[Collection("WPF preview")]
public sealed class PreviewArtboardTests(PreviewFixture fixture)
{
    private const string Namespace = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private PreviewEngine Engine => fixture.Engine;
    private static PreviewRequest Request(string text, bool sizeToRoot, long version = 1) =>
        new("C:/preview/Artboard.xaml", text, version, 400, 300, SizeToRoot: sizeToRoot);

    [Theory]
    [InlineData("<Window {0} Width='500' Height='350'><Grid/></Window>", true, 500, 350)]
    [InlineData("<UserControl {0} Width='250'><Grid/></UserControl>", true, 250, 300)]
    [InlineData("<Grid {0}/>", true, 400, 300)]
    [InlineData("<Window {0} Width='500' Height='350'><Grid/></Window>", false, 400, 300)]
    [InlineData("<Border {0} Width='9000' Height='12'/>", true, 4096, 32)]
    public async Task ArtboardUsesTheRootSizeWhenRequested(string markup, bool sizeToRoot, int width, int height)
    {
        var snapshot = await Engine.RenderAsync(Request(string.Format(markup, Namespace), sizeToRoot), default);
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        Assert.Equal((width, height), (snapshot.PixelWidth, snapshot.PixelHeight));
    }

    [Fact]
    public async Task DesignTimeSizeDrivesTheArtboard()
    {
        string text = $"<UserControl {Namespace} xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' mc:Ignorable='d' d:DesignWidth='320' d:DesignHeight='180'><Grid/></UserControl>";
        var snapshot = await Engine.RenderAsync(Request(text, true), default);
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        Assert.Equal((320, 180), (snapshot.PixelWidth, snapshot.PixelHeight));
    }

    [Fact]
    public async Task PickSelectsTheAuthoredElementUnlessTheExactPartIsRequested()
    {
        string text = $"<Grid {Namespace}><Button x:Name='Save' Width='160' Height='40' HorizontalAlignment='Left' VerticalAlignment='Top'>Save changes</Button></Grid>";
        var snapshot = await Engine.RenderAsync(Request(text, false, 7), default);
        Assert.True(snapshot.Success);
        var button = Assert.Single(snapshot.Nodes, node => node.Name == "Save");
        double x = button.Bounds!.X + button.Bounds.Width / 2, y = button.Bounds.Y + button.Bounds.Height / 2;

        var authored = await Engine.PickAsync(new(7, x, y, PreferAuthored: true), default);
        Assert.Equal(button.Id, authored.Node?.Id);

        var exact = await Engine.PickAsync(new(7, x, y), default);
        Assert.NotNull(exact.Node);
        Assert.NotEqual(button.Id, exact.Node!.Id);
        Assert.Null(exact.Node.Source);
    }
}
