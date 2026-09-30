using System.IO;
using System.Text.Json;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class PreviewDependencyTests
{
    [Theory]
    [InlineData("/PreviewDependency.Controls;component/Theme/Shared.xaml")]
    [InlineData("pack://application:,,,/PreviewDependency.Controls;component/Theme/Shared.xaml")]
    public async Task ReferencedAssemblyPackDictionaryLoadsInSourcePreview(string resourceUri)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "CompiledFixture");
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        string source = $$"""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="{{resourceUri}}"/>
              </ResourceDictionary.MergedDictionaries></ResourceDictionary></Grid.Resources>
              <TextBlock Name="Subject" Text="Shared theme" Foreground="{StaticResource DependencyAccent}"/>
            </Grid>
            """;
        var snapshot = await client.RenderAsync(new("C:/preview/View.xaml", source, 1, 400, 300,
            Path.Combine(directory, "WpfStudio.PreviewFixture.dll"), directory, ApplicationResourcePath: null));
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        var node = Assert.Single(snapshot.Nodes, node => node.Name == "Subject");
        Assert.Contains((await client.InspectAsync(new(snapshot.Version, node.Id))).Properties, property => property.Name == "Foreground" && property.Value == "#FF2468AC");
    }

    [Fact]
    public async Task DependencyRebuildRestartsSourceHostWhileOrdinarySourceEditsReuseIt()
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio-PreviewDependency-").FullName;
        try
        {
            CopyFixture(Path.Combine(AppContext.BaseDirectory, "DependencyFixture"), directory);
            await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
            const string source = "<control:DependencyButton xmlns:control='clr-namespace:PreviewDependency.Controls;assembly=PreviewDependency.Controls'/>";
            var request = new PreviewRequest("C:/preview/View.xaml", source, 1, 400, 300,
                Path.Combine(directory, "bin", "PreviewDependency.Controls.dll"), directory, ApplicationResourcePath: null);
            Assert.True((await client.RenderAsync(request)).Success);
            int first = client.ProcessId!.Value;
            Assert.True((await client.RenderAsync(request with { Text = source + " ", Version = 2 })).Success);
            Assert.Equal(first, client.ProcessId);
            string dependency = Path.Combine(directory, "bin", "PreviewDependency.Models.dll");
            File.WriteAllBytes(dependency, File.ReadAllBytes(dependency)); // No lock on the real project's outputs.
            File.SetLastWriteTimeUtc(dependency, DateTime.UtcNow.AddMinutes(1));
            Assert.True((await client.RenderAsync(request with { Version = 3 })).Success);
            Assert.NotEqual(first, client.ProcessId);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(directory), ignoreCase: true);
            Directory.Delete(directory, true);
        }
    }

    private static void CopyFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string child in Directory.EnumerateDirectories(source)) CopyFixture(child, Path.Combine(destination, Path.GetFileName(child)));
    }

    [Fact]
    public async Task MissingRestoredPackageReportsHowToRecoverAndCanLoadAfterRestore()
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio-MissingPreviewDependency-").FullName;
        try
        {
            CopyFixture(Path.Combine(AppContext.BaseDirectory, "DependencyFixture"), directory);
            string assets = Path.Combine(directory, "obj", "project.assets.json");
            string originalAssets = File.ReadAllText(assets);
            File.WriteAllText(assets, JsonSerializer.Serialize(new
            {
                packageFolders = new Dictionary<string, object> { [Path.Combine(directory, "empty-cache")] = new { } }
            }));
            await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
            var request = new PreviewRequest("C:/preview/View.xaml",
                "<control:DependencyButton xmlns:control='clr-namespace:PreviewDependency.Controls;assembly=PreviewDependency.Controls'/>",
                1, 400, 300, Path.Combine(directory, "bin", "PreviewDependency.Controls.dll"), directory,
                ApplicationResourcePath: null);
            var failed = await client.RenderAsync(request);
            Assert.False(failed.Success);
            Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Message.Contains("CommunityToolkit.Mvvm")
                && diagnostic.Message.Contains("Restore and rebuild"));
            int first = client.ProcessId!.Value;
            File.WriteAllText(assets, originalAssets);
            var restored = await client.RenderAsync(request with { Version = 2 });
            Assert.True(restored.Success, string.Join("\n", restored.Diagnostics.Select(d => d.Message)));
            Assert.NotEqual(first, client.ProcessId);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(directory), ignoreCase: true);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("clr-namespace:PreviewDependency.Controls;assembly=PreviewDependency.Controls")]
    [InlineData("urn:wpfstudio:dependency-controls")]
    public async Task ColdSourcePreviewResolvesReferencedControlBeforeSuppressingItsEvents(string xmlns)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "CompiledFixture");
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        string source = $"<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:controls='{xmlns}'><controls:DependencyButton Name='Subject' Click='HandlerOmittedInSource'/></Grid>";
        var snapshot = await client.RenderAsync(new("C:/preview/View.xaml", source, 1, 400, 300,
            Path.Combine(directory, "WpfStudio.PreviewFixture.dll"), directory, ApplicationResourcePath: null));
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        var button = Assert.Single(snapshot.Nodes, node => node.Name == "Subject");
        Assert.Equal(source.IndexOf("<controls:DependencyButton", StringComparison.Ordinal), button.Source!.Start);
        Assert.Contains(snapshot.Diagnostics, d => d.Message.Contains("HandlerOmittedInSource"));
        var properties = await client.InspectAsync(new(snapshot.Version, button.Id));
        Assert.Contains(properties.Properties, p => p.Name == "Content" && p.Value == "Transitive package loaded");
    }

    [Fact]
    public async Task ClassLibraryPreviewResolvesRestoredTransitivePackagesAbsentFromItsOutput()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "DependencyFixture");
        Assert.False(File.Exists(Path.Combine(directory, "bin", "CommunityToolkit.Mvvm.dll")), "Fixture must retain the SDK's ordinary class-library output.");
        await using var client = new PreviewClient(PreviewHostUnderTest.ExecutablePath);
        const string source = "<control:DependencyButton xmlns:control='clr-namespace:PreviewDependency.Controls;assembly=PreviewDependency.Controls' Name='Subject'/>";
        var snapshot = await client.RenderAsync(new("C:/preview/View.xaml", source, 1, 400, 300,
            Path.Combine(directory, "bin", "PreviewDependency.Controls.dll"), directory, ApplicationResourcePath: null));
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        var properties = await client.InspectAsync(new(snapshot.Version, snapshot.Nodes[0].Id));
        Assert.Contains(properties.Properties, p => p.Name == "Content" && p.Value == "Transitive package loaded");
    }
}
