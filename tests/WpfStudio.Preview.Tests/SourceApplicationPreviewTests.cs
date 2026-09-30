using System.IO;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class SourceApplicationPreviewTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("DataContext='A real source'", false)]
    public async Task MissingPreviewDataIsExplainedWithoutHidingBindingFailures(string context, bool unavailable)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var snapshot = await client.RenderAsync(new("C:/preview/View.xaml",
            $"<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' {context}><TextBlock Name='Subject' Text='{{Binding MisspelledProperty}}'/></Grid>", 1));
        Assert.True(snapshot.Success);
        Assert.Equal(unavailable, snapshot.Status!.Contains("no root DataContext"));
        var subject = Assert.Single(snapshot.Nodes, n => n.Name == "Subject");
        Assert.Contains(snapshot.Diagnostics, d => d.NodeId == subject.Id && d.Severity == "Error");
    }

    private static PreviewRequest Request(string source) => new("C:/preview/View.xaml", source, 1, 400, 300,
        Path.Combine(AppContext.BaseDirectory, "CompiledFixture", "WpfStudio.PreviewFixture.dll"),
        Path.Combine(AppContext.BaseDirectory, "CompiledFixture"));

    [Fact]
    public async Task DefaultSourceLoadsBuiltApplicationResourcesAndReusesHostWithoutAppStartup()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var request = Request("<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Name='Subject' Text='Unsaved text' Foreground='{StaticResource AppAccent}'/>");
        for (int version = 1; version <= 2; version++)
        {
            int? previous = client.ProcessId;
            var snapshot = await client.RenderAsync(request with { Version = version });
            Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
            if (previous is not null) Assert.Equal(previous, client.ProcessId);
            Assert.Contains(snapshot.Diagnostics, d => d.Message.Contains("Built application resources 'App.xaml' loaded"));
            var subject = Assert.Single(snapshot.Nodes, node => node.Name == "Subject");
            Assert.NotNull(subject.Source);
            var inspected = await client.InspectAsync(new(version, subject.Id));
            Assert.Contains(inspected.Properties, p => p.Name == "Text" && p.Value == "Unsaved text");
            Assert.Contains(inspected.Properties, p => p.Name == "Foreground" && p.Value == "#FF008080");
        }
        var withoutResources = await client.RenderAsync(request with { Version = 3, ApplicationResourcePath = null });
        Assert.False(withoutResources.Success);
        Assert.Contains(withoutResources.Diagnostics, d => d.Message.Contains("AppAccent"));
    }

    [Fact]
    public async Task SourceWindowRetainsChromeAttachedBehaviorAndAncestorBindings()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        const string source = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:WpfStudio.PreviewFixture"
                    x:Class="Ignored.UnsavedView" Title="Source window" local:FixtureWindowScope.RequireWindow="True">
              <WindowChrome.WindowChrome><WindowChrome CaptionHeight="32"/></WindowChrome.WindowChrome>
              <TextBlock Name="Subject" Text="{Binding Title, RelativeSource={RelativeSource AncestorType=Window}}"/>
            </Window>
            """;
        var snapshot = await client.RenderAsync(Request(source));
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        Assert.Contains(snapshot.Nodes, n => n.Type == "System.Windows.Window" && n.Source?.Start == 0);
        var subject = Assert.Single(snapshot.Nodes, n => n.Name == "Subject");
        Assert.Equal(source.IndexOf("<TextBlock", StringComparison.Ordinal), subject.Source!.Start);
        Assert.Contains((await client.InspectAsync(new(1, subject.Id))).Properties,
            p => p.Name == "Text" && p.Value == "Source window" && p.BindingStatus == "Active");
        Assert.DoesNotContain(snapshot.Diagnostics, d => d.Severity == "Error");
    }

    [Fact]
    public async Task DefaultSourceAllowsLibrariesWithNoApplicationResource()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var request = new PreviewRequest("C:/preview/View.xaml", "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'/>", 1,
            AssemblyPath: Path.Combine(AppContext.BaseDirectory, "DependencyFixture", "bin", "PreviewDependency.Controls.dll"),
            ProjectDirectory: Path.Combine(AppContext.BaseDirectory, "DependencyFixture"));
        var snapshot = await client.RenderAsync(request);
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        Assert.Contains(snapshot.Diagnostics, d => d.Severity == "Information" && d.Message.Contains("no 'App.xaml' application resource"));
        Assert.DoesNotContain(snapshot.Diagnostics, d => d.Severity == "Error");
    }
}
