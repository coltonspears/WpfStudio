using System.IO;
using System.Security.Cryptography;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class CompiledPreviewTests
{
    private static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "CompiledFixture", "WpfStudio.PreviewFixture.dll");
    private static PreviewRequest Request(string view) => new("C:/project/UnsavedView.xaml", "<InvalidUnfinishedBuffer", 74,
        400, 300, AssemblyPath, AppContext.BaseDirectory, PreviewMode.Compiled, "WpfStudio.PreviewFixture." + view);

    [Theory]
    [InlineData("FixtureView", "Constructor in design mode / Loaded handler")]
    [InlineData("FixturePage", "Compiled page constructor")]
    [InlineData("FixtureWindow", "Compiled window ancestor")]
    public async Task BuiltViewsUseCodeBehindAndCompiledApplicationResourcesWithoutAppStartup(string view, string expectedText)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var snapshot = await client.RenderAsync(Request(view));
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(snapshot.PngBytes!);
        Assert.All(snapshot.Nodes, n => Assert.Null(n.Source));
        Assert.NotNull(snapshot.Build);
        Assert.Equal(AssemblyPath, snapshot.Build.AssemblyPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AssemblyPath))), snapshot.Build.AssemblySha256);
        Assert.True(Guid.TryParse(snapshot.Build.ModuleVersionId, out _));
        Assert.Equal("App.xaml", snapshot.Build.ApplicationResourcePath);
        var message = Assert.Single(snapshot.Nodes, n => n.Name == "Message");
        var inspection = await client.InspectAsync(new(snapshot.Version, message.Id));
        Assert.All(inspection.Properties, p => Assert.False(p.CanWriteSource));
        Assert.Contains(inspection.Properties, p => p.Name == "Text" && p.Value == expectedText);
        Assert.Contains(inspection.Properties, p => p.Name == "Foreground" && p.Value == "#FF008080" && p.ValueSource == "Style");
        Assert.NotNull(message.Bounds);
        Assert.True(message.Bounds!.Width > 0 && message.Bounds.Height > 0);
    }

    [Fact]
    public async Task FailedCompiledActivationReturnsDiagnosticsAndSourceModeRemainsAvailable()
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var failed = await client.RenderAsync(Request("FailureView"));
        Assert.False(failed.Success);
        Assert.Contains(failed.Diagnostics, d => d.Message.Contains("Deliberate compiled constructor failure", StringComparison.Ordinal));
        var source = await client.RenderAsync(new("C:/project/Source.xaml", "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'/>", 75, 400, 300));
        Assert.True(source.Success);
        Assert.Null(source.Build);
        Assert.Contains(source.Nodes, n => n.Source is not null);
    }

    [Theory]
    [InlineData(false, "AppAbsolute.xaml")]
    [InlineData(true, "AppAbsolute.xaml")]
    [InlineData(false, null)]
    public async Task AbsoluteApplicationPackUrisResolveAgainstTheCompiledProject(bool sourceFirst, string? resourcePath)
    {
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        int? previousProcess = null;
        if (sourceFirst)
        {
            var source = await client.RenderAsync(new("C:/project/Source.xaml", "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'/>", 73, 400, 300));
            Assert.True(source.Success);
            previousProcess = client.ProcessId;
        }
        var snapshot = await client.RenderAsync(Request("FixtureAbsoluteView") with { ApplicationResourcePath = resourcePath });
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(client.ProcessId);
        if (sourceFirst) Assert.NotEqual(previousProcess, client.ProcessId);
        Assert.Equal(resourcePath, snapshot.Build!.ApplicationResourcePath);
        var message = Assert.Single(snapshot.Nodes, n => n.Name == "Message");
        var inspection = await client.InspectAsync(new(snapshot.Version, message.Id));
        Assert.Contains(inspection.Properties, p => p.Name == "Text" && p.Value == "Absolute application resource stream");
        Assert.Contains(inspection.Properties, p => p.Name == "Foreground" && p.Value == "#FF008080");
    }
}
