using System.Text;
using System.Text.Json;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class PreviewScenarioCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WpfStudio-Scenarios-" + Guid.NewGuid().ToString("N"));
    public PreviewScenarioCatalogTests() => Directory.CreateDirectory(_directory);
    private string View => Path.Combine(_directory, "Views", "Customer.xaml");

    [Fact]
    public async Task ReadsOnlyTheCurrentViewWithoutLoadingItsFactoryType()
    {
        await Write("""
            {
              "version": 1,
              "views": [
                {"path":"Views/Customer.xaml","scenarios":[
                  {"name":"Populated","dataContextFactory":{"typeName":"NotBuilt.PreviewData","methodName":"Populated"}},
                  {"name":"With services","viewFactory":{"typeName":"NotBuilt.PreviewData","methodName":"CreateView"},
                    "dataContextFactory":{"typeName":"NotBuilt.PreviewData","methodName":"Loading"}}
                ]},
                {"path":"Other.xaml","scenarios":[{"name":"Unrelated","viewFactory":{"typeName":"Other.Factory","methodName":"Create"}}]}
              ]
            }
            """);
        var result = await PreviewScenarioCatalog.LoadAsync(_directory, View);
        Assert.Empty(result.Warnings);
        Assert.Equal(["Populated", "With services"], result.Scenarios.Select(item => item.Name));
        Assert.Equal("NotBuilt.PreviewData", result.Scenarios[0].DataContextFactory!.TypeName);
        Assert.Equal("CreateView", result.Scenarios[1].ViewFactory!.MethodName);
        Assert.Equal(Path.Combine(_directory, PreviewScenarioCatalog.FileName), result.Path);
        Assert.False(File.Exists(View)); // Identity selection never requires reading the view.
    }

    [Fact]
    public async Task LinkedPathsBomCommentsAndTrailingCommasAreSupported()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, PreviewScenarioCatalog.FileName), """
            { // A linked view is declared relative to the project.
              "version":1,
              "views":[{"path":"../Shared/Customer.xaml","scenarios":[
                {"name":"Empty","dataContextFactory":{"typeName":"App.Samples","methodName":"Empty",}},
              ]},],
            }
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var result = await PreviewScenarioCatalog.LoadAsync(_directory, Path.GetFullPath("../Shared/Customer.xaml", _directory));
        Assert.Empty(result.Warnings);
        Assert.Equal("Empty", Assert.Single(result.Scenarios).Name);
    }

    [Fact]
    public async Task MissingConfigurationAndUnknownViewsReturnNoImplicitScenario()
    {
        var absent = await PreviewScenarioCatalog.LoadAsync(_directory, View);
        Assert.Empty(absent.Scenarios); Assert.Empty(absent.Warnings);
        var noProject = await PreviewScenarioCatalog.LoadAsync(null, View);
        Assert.Null(noProject.Path); Assert.Empty(noProject.Scenarios); Assert.Empty(noProject.Warnings);
        await Write("""{"version":1,"views":[]} """);
        var empty = await PreviewScenarioCatalog.LoadAsync(_directory, View);
        Assert.Empty(empty.Scenarios); Assert.Empty(empty.Warnings);
    }

    [Theory]
    [InlineData("{", "Invalid JSON")]
    [InlineData("{\"version\":2,\"views\":[]}", "version 1")]
    [InlineData("{\"version\":1,\"Version\":1,\"views\":[]}", "Unknown configuration")]
    [InlineData("{\"version\":1,\"version\":1,\"views\":[]}", "Duplicate configuration")]
    [InlineData("{\"version\":1,\"views\":{}}", "array")]
    public async Task MalformedOrUnknownSchemaReturnsAnExplanation(string json, string message)
    {
        await Write(json);
        var result = await PreviewScenarioCatalog.LoadAsync(_directory, View);
        Assert.Empty(result.Scenarios);
        Assert.Contains(message, Assert.Single(result.Warnings));
    }

    [Theory]
    [InlineData("{\"name\":\"No factory\"}")]
    [InlineData("{\"name\":\"Typo\",\"dataContextFacotry\":{\"typeName\":\"App.Data\",\"methodName\":\"Make\"}}")]
    [InlineData("{\"name\":\"Null factory\",\"viewFactory\":null}")]
    [InlineData("{\"name\":\" Missing trim\",\"viewFactory\":{\"typeName\":\"App.Data\",\"methodName\":\"Make\"}}")]
    [InlineData("{\"name\":\"Missing type\",\"viewFactory\":{\"methodName\":\"Make\"}}")]
    [InlineData("{\"name\":\"default\",\"viewFactory\":{\"typeName\":\"App.Data\",\"methodName\":\"Make\"}}")]
    public async Task InvalidScenarioDoesNotReturnAPartialCatalog(string invalid)
    {
        await Write("{\"version\":1,\"views\":[{\"path\":\"Views/Customer.xaml\",\"scenarios\":[" +
            "{\"name\":\"Valid first\",\"viewFactory\":{\"typeName\":\"App.Data\",\"methodName\":\"Make\"}}," + invalid + "]}]}");
        var result = await PreviewScenarioCatalog.LoadAsync(_directory, View);
        Assert.Empty(result.Scenarios); Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task DuplicateCanonicalPathsAndNamesAreRejected()
    {
        await Write("""{"version":1,"views":[{"path":"Views/Customer.xaml","scenarios":[]},{"path":"Views/../Views/Customer.xaml","scenarios":[]}]}""");
        Assert.Contains("view path", Assert.Single((await PreviewScenarioCatalog.LoadAsync(_directory, View)).Warnings));
        await Write(Catalog(Enumerable.Repeat(new { name = "Same", dataContextFactory = Factory() }, 2)));
        Assert.Contains("more than once", Assert.Single((await PreviewScenarioCatalog.LoadAsync(_directory, View)).Warnings));
    }

    [Theory]
    [InlineData("/Outside.xaml")]
    [InlineData("C:/Outside.xaml")]
    [InlineData("**/*.xaml")]
    [InlineData("Views/Customer.cs")]
    public async Task ViewPathsAreExplicitRelativeXamlPaths(string path)
    {
        await Write(JsonSerializer.Serialize(new { version = 1, views = new[] { new { path, scenarios = System.Array.Empty<object>() } } }));
        Assert.NotEmpty((await PreviewScenarioCatalog.LoadAsync(_directory, View)).Warnings);
    }

    [Fact]
    public async Task BudgetsAndCancellationDoNotProducePartialResults()
    {
        await Write(new string(' ', 262145));
        Assert.Contains("256 KiB", Assert.Single((await PreviewScenarioCatalog.LoadAsync(_directory, View)).Warnings));
        await Write(Catalog(Enumerable.Range(0, 65).Select(index => new { name = "Scenario " + index, dataContextFactory = Factory() })));
        Assert.Contains("at most 64", Assert.Single((await PreviewScenarioCatalog.LoadAsync(_directory, View)).Warnings));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PreviewScenarioCatalog.LoadAsync(_directory, View, cancellation.Token));
    }

    [Fact]
    public async Task ReloadReflectsChangedOrRemovedConfiguration()
    {
        await Write(Catalog(new[] { new { name = "Loading", dataContextFactory = Factory() } }));
        Assert.Equal("Loading", Assert.Single((await PreviewScenarioCatalog.LoadAsync(_directory, View)).Scenarios).Name);
        await Write(Catalog(new[] { new { name = "Error", dataContextFactory = Factory() } }));
        Assert.Equal("Error", Assert.Single((await PreviewScenarioCatalog.LoadAsync(_directory, View)).Scenarios).Name);
        File.Delete(Path.Combine(_directory, PreviewScenarioCatalog.FileName));
        Assert.Empty((await PreviewScenarioCatalog.LoadAsync(_directory, View)).Scenarios);
    }

    [Fact]
    public async Task FingerprintDetectsSameSizeChangesEvenWithRestoredFileTime()
    {
        await Write(Catalog(new[] { new { name = "First", dataContextFactory = Factory() } }));
        string path = Path.Combine(_directory, PreviewScenarioCatalog.FileName);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var before = await PreviewScenarioCatalog.LoadAsync(_directory, View);
        long length = new FileInfo(path).Length;
        await Write(Catalog(new[] { new { name = "Other", dataContextFactory = Factory() } }));
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal(length, new FileInfo(path).Length);
        var after = await PreviewScenarioCatalog.LoadAsync(_directory, View);
        Assert.NotNull(before.Fingerprint);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    private static object Factory() => new { typeName = "App.Samples", methodName = "Create" };
    private static string Catalog<T>(IEnumerable<T> scenarios) => JsonSerializer.Serialize(new
    { version = 1, views = new[] { new { path = "Views/Customer.xaml", scenarios } } });
    private Task Write(string text) => File.WriteAllTextAsync(Path.Combine(_directory, PreviewScenarioCatalog.FileName), text);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
