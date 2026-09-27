using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public class WpfIndexTests
{
    private const string Namespaces = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    [Fact]
    public async Task LinkedXamlIsIndexedOnceAndOnlyUnambiguousResourcesAreResolved()
    {
        var fixture = await CreateAsync(new Dictionary<string, string>
        {
            ["Shared.xaml"] = $"<ResourceDictionary {Namespaces}><SolidColorBrush x:Key=\"Accent\" Color=\"Blue\" /></ResourceDictionary>",
            ["App.xaml"] = $"<Application {Namespaces}><Application.Resources><SolidColorBrush x:Key=\"AppAccent\" Color=\"Red\" /></Application.Resources></Application>",
            ["SecondApp.xaml"] = $"<Application {Namespaces}><Application.Resources><SolidColorBrush x:Key=\"AppAccent\" Color=\"Green\" /></Application.Resources></Application>",
            ["View.xaml"] = $"<UserControl {Namespaces}><UserControl.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"Shared.xaml\" /></ResourceDictionary.MergedDictionaries><SolidColorBrush x:Key=\"Local\" Color=\"Pink\" /></ResourceDictionary></UserControl.Resources><StackPanel><Border Background=\"{{StaticResource Local}}\"/><Border Background=\"{{StaticResource Accent}}\"/><Border Background=\"{{StaticResource AppAccent}}\"/></StackPanel></UserControl>"
        });
        var first = fixture.Projects[0] with { Files = fixture.Projects[0].Files.Where(file => file.Name != "SecondApp.xaml").ToArray() };
        var second = first with
        {
            Id = "2", Name = "Second", ProjectPath = Path.Combine(Path.GetDirectoryName(first.ProjectPath)!, "Second.csproj"),
            Files = fixture.Projects[0].Files.Where(file => file.Name != "App.xaml").ToArray()
        };
        var index = await new WpfIndexService().IndexAsync(fixture with { Projects = [first, second] });
        Assert.Single(index.Resources, resource => resource.Key == "Accent");
        Assert.Single(index.Resources, resource => resource.Key == "Local");
        Assert.DoesNotContain(index.Diagnostics, diagnostic => diagnostic.Id == "XAML002");
        Assert.EndsWith("Shared.xaml", Assert.Single(index.Usages, usage => usage.Key == "Accent").ResolvedPath);
        Assert.EndsWith("View.xaml", Assert.Single(index.Usages, usage => usage.Key == "Local").ResolvedPath);
        Assert.Null(Assert.Single(index.Usages, usage => usage.Key == "AppAccent").ResolvedPath);
        var changes = new WpfIndexService().RenameResource(index, Assert.Single(index.Resources, resource => resource.Key == "Accent"), "Renamed");
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, change => change.After.Contains("{StaticResource Renamed}"));
    }

    [Fact]
    public async Task LaterMergedDictionariesWinButPrimaryAndLocalResourcesTakePrecedence()
    {
        var fixture = await CreateAsync(new Dictionary<string, string>
        {
            ["First.xaml"] = $"<ResourceDictionary {Namespaces}><SolidColorBrush x:Key=\"Accent\" Color=\"Red\" /></ResourceDictionary>",
            ["Last.xaml"] = $"<ResourceDictionary {Namespaces}><SolidColorBrush x:Key=\"Accent\" Color=\"Blue\" /></ResourceDictionary>",
            ["App.xaml"] = $"<Application {Namespaces}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"First.xaml\"/><ResourceDictionary Source=\"Last.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["View.xaml"] = $"<UserControl {Namespaces}><StackPanel><Border Background=\"{{StaticResource Accent}}\"/><Border Background=\"{{StaticResource Accent}}\"><Border.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"Green\" /></Border.Resources></Border></StackPanel></UserControl>"
        });
        var service = new WpfIndexService();
        var index = await service.IndexAsync(fixture);
        var usages = index.Usages.Where(u => u.Path.EndsWith("View.xaml")).ToArray();
        Assert.Equal(2, usages.Length);
        Assert.EndsWith("Last.xaml", usages[0].ResolvedPath);
        Assert.EndsWith("View.xaml", usages[1].ResolvedPath);
        Assert.DoesNotContain(index.Diagnostics, d => d.Id == "XAML002");
        var app = fixture.Projects[0].Files.Single(f => f.Name == "App.xaml").Path;
        var content = await File.ReadAllTextAsync(app);
        await File.WriteAllTextAsync(app, content.Replace("</ResourceDictionary.MergedDictionaries>", "</ResourceDictionary.MergedDictionaries><SolidColorBrush x:Key=\"Accent\" Color=\"Purple\"/>"));
        index = await service.IndexAsync(fixture);
        Assert.EndsWith("App.xaml", index.Usages.First(u => u.Path.EndsWith("View.xaml")).ResolvedPath);
    }

    [Fact]
    public async Task RenamingEntityEncodedKeysUsesRawSourceOffsetsAndSkipsLiteralText()
    {
        var text = $"<UserControl {Namespaces}><UserControl.Resources><SolidColorBrush x:Key=\"A&amp;B\" Color=\"Blue\" /></UserControl.Resources><StackPanel><Border Background=\"{{StaticResource A&amp;B}}\"/><TextBlock Text=\"{{}}{{StaticResource A&amp;B}}\"/><Border Tag=\"{{Binding ConverterParameter=A&amp;B, Converter={{StaticResource A&amp;B}}}}\" /></StackPanel></UserControl>";
        var fixture = await CreateAsync(new Dictionary<string, string> { ["View.xaml"] = text });
        var service = new WpfIndexService();
        var index = await service.IndexAsync(fixture);
        Assert.Equal(2, index.Usages.Count);
        var changes = service.RenameResource(index, Assert.Single(index.Resources), "Renamed");
        var changed = Assert.Single(changes).After;
        Assert.Contains("x:Key=\"Renamed\"", changed);
        Assert.Contains("Background=\"{StaticResource Renamed}\"", changed);
        Assert.Contains("ConverterParameter=A&amp;B, Converter={StaticResource Renamed}", changed);
        Assert.Contains("Text=\"{}{StaticResource A&amp;B}\"", changed);
        XDocument.Parse(changed);
    }

    [Fact]
    public async Task CyclesAndDuplicateKeysAreReportedWhileDynamicKeysRemainUnknown()
    {
        var fixture = await CreateAsync(new Dictionary<string, string>
        {
            ["A.xaml"] = $"<ResourceDictionary {Namespaces}><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"B.xaml\"/></ResourceDictionary.MergedDictionaries><SolidColorBrush x:Key=\"Duplicate\"/><SolidColorBrush x:Key=\"Duplicate\"/></ResourceDictionary>",
            ["B.xaml"] = $"<ResourceDictionary {Namespaces}><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"A.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary>",
            ["View.xaml"] = $"<UserControl {Namespaces}><Border Background=\"{{DynamicResource RuntimeProvided}}\"/></UserControl>"
        });
        var index = await new WpfIndexService().IndexAsync(fixture);
        Assert.Contains(index.Diagnostics, d => d.Id == "XAML002");
        Assert.Contains(index.Diagnostics, d => d.Id == "XAML003");
        Assert.DoesNotContain(index.Diagnostics, d => d.Message.Contains("RuntimeProvided"));
    }

    [Fact]
    public async Task MalformedXmlProducesADiagnosticWithoutInstantiatingMarkup()
    {
        var fixture = await CreateAsync(new Dictionary<string, string> { ["Broken.xaml"] = "<UserControl>", ["External.xaml"] = "<!DOCTYPE x [<!ENTITY ext SYSTEM 'file:///C:/secret'>]><x>&ext;</x>" });
        var index = await new WpfIndexService().IndexAsync(fixture);
        Assert.Equal(2, index.Diagnostics.Count(d => d.Id == "XAML001"));
    }

    private static async Task<WorkspaceSnapshot> CreateAsync(Dictionary<string, string> files)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var items = new List<WorkspaceFile>();
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, text);
            items.Add(new WorkspaceFile(path, name, "Page"));
        }
        var project = Path.Combine(directory, "App.csproj");
        return new WorkspaceSnapshot(project, "10.0", [new WorkspaceProject("1", "App", project, "net10.0-windows", null, true, items)], []);
    }
}
