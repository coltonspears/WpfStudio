using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Workspace;

namespace WpfStudio.Core.Tests;

public class ScaffoldingTests
{
    [Theory]
    [InlineData("net8.0-windows", "12.0", false)]
    [InlineData("net9.0-windows", "13.0", true)]
    [InlineData("net10.0-windows", "14.0", false)]
    public async Task GeneratedViewAndViewModelBuildForSelectedCompilerWithoutImplicitUsings(string framework, string language, bool explicitItems)
    {
        var directory = NewDirectory();
        var project = Path.Combine(directory, "Generated.csproj");
        await File.WriteAllTextAsync(project, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{framework}</TargetFramework><LangVersion>{language}</LangVersion><UseWPF>true</UseWPF><ImplicitUsings>disable</ImplicitUsings><EnableDefaultCompileItems>{(!explicitItems).ToString().ToLowerInvariant()}</EnableDefaultCompileItems><EnableDefaultPageItems>{(!explicitItems).ToString().ToLowerInvariant()}</EnableDefaultPageItems></PropertyGroup></Project>");
        var changes = await new ScaffoldingService().CreateAsync(project, directory, "CustomerView", ScaffoldKind.ViewAndViewModel, true);
        var viewModel = changes.Single(c => c.Path.EndsWith("ViewModel.cs")).After;
        Assert.Contains("using System.Threading.Tasks;", viewModel);
        if (language == "14.0") Assert.Contains("public partial string Title", viewModel);
        else Assert.DoesNotContain("public partial string Title", viewModel);
        await WriteChangesAsync(changes);
        await AssertBuildsAsync(project);
    }

    [Fact]
    public async Task ImportedLanguageRootNamespaceAndToolkitAreRespected()
    {
        var directory = NewDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.props"), "<Project><PropertyGroup><LangVersion>12.0</LangVersion><RootNamespace>Company.Desktop</RootNamespace></PropertyGroup><ItemGroup><PackageReference Include=\"CommunityToolkit.Mvvm\" Version=\"8.4.2\" /></ItemGroup></Project>");
        var project = Path.Combine(directory, "DifferentName.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>");
        var changes = await new ScaffoldingService().CreateAsync(project, directory, "Customer", ScaffoldKind.ViewModel);
        var viewModel = Assert.Single(changes).After;
        Assert.Contains("namespace Company.Desktop;", viewModel);
        Assert.DoesNotContain("public partial string", viewModel);
        await WriteChangesAsync(changes);
        await AssertBuildsAsync(project);
    }

    [Fact]
    public async Task CentralPackageManagementGetsAVersionAndProjectReferenceWithoutVersion()
    {
        var directory = NewDirectory();
        var central = Path.Combine(directory, "Directory.Packages.props");
        await File.WriteAllTextAsync(central, "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        var project = Path.Combine(directory, "Central.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
        var changes = await new ScaffoldingService().CreateAsync(project, directory, "Central", ScaffoldKind.ViewModel);
        var packageReference = XDocument.Parse(changes.Single(c => c.Path == project).After).Descendants("PackageReference").Single();
        Assert.Null(packageReference.Attribute("Version"));
        Assert.Contains(changes, c => c.Path == central && c.After.Contains("PackageVersion"));
        await WriteChangesAsync(changes);
        await AssertBuildsAsync(project);
    }

    [Fact]
    public async Task AssetBuildActionsAvoidDefaultItemDuplicatesAndUseAssemblyNameInPackUri()
    {
        var directory = NewDirectory();
        var project = Path.Combine(directory, "Filename.csproj");
        var projectText = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><AssemblyName>Actual.Assembly</AssemblyName></PropertyGroup></Project>";
        await File.WriteAllTextAsync(project, projectText);
        var asset = Path.Combine(directory, "my image.png");
        await File.WriteAllBytesAsync(asset, [1, 2, 3]);
        var resourceChange = ScaffoldingService.SetBuildAction(project, projectText, asset, "Resource");
        await File.WriteAllTextAsync(project, resourceChange.After);
        await AssertBuildsAsync(project);
        var noneChange = ScaffoldingService.SetBuildAction(project, resourceChange.After, asset, "None");
        await File.WriteAllTextAsync(project, noneChange.After);
        await AssertBuildsAsync(project);
        Assert.Equal("pack://application:,,,/Actual.Assembly;component/my%20image.png", ScaffoldingService.PackUri(project, asset));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("Bad Name")]
    public async Task InvalidCSharpNamesAreRejected(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ScaffoldingService().CreateAsync("unused.csproj", ".", name, ScaffoldKind.UserControl));
    }

    [Fact]
    public async Task ChangingLinkedAssetBuildActionPreservesMetadataAcrossSlashStyles()
    {
        var directory = NewDirectory();
        var projectDirectory = Path.Combine(directory, "App");
        Directory.CreateDirectory(projectDirectory);
        var sharedDirectory = Path.Combine(directory, "Shared");
        Directory.CreateDirectory(sharedDirectory);
        var asset = Path.Combine(sharedDirectory, "icon.png");
        await File.WriteAllBytesAsync(asset, [1, 2, 3]);
        var project = Path.Combine(projectDirectory, "App.csproj");
        var text = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup><ItemGroup><Content Include=\"../Shared/icon.png\" Link=\"Images/icon.png\"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory><CustomMetadata>keep me</CustomMetadata></Content></ItemGroup><ItemGroup Condition=\"'$(Configuration)' == 'QA'\"><Content Update=\"..\\Shared\\icon.png\"><CopyToOutputDirectory>Always</CopyToOutputDirectory></Content></ItemGroup></Project>";
        var change = ScaffoldingService.SetBuildAction(project, text, asset, "Resource");
        var xml = XDocument.Parse(change.After);
        Assert.DoesNotContain(xml.Descendants("Content"), item => item.Attribute("Include") != null || item.Attribute("Update") != null);
        Assert.Single(xml.Descendants("Resource"), item => item.Attribute("Include") != null);
        var updates = xml.Descendants("Resource").Where(item => item.Attribute("Update") != null).ToArray();
        Assert.Equal(2, updates.Length);
        Assert.Equal("Images/icon.png", (string?)updates[0].Attribute("Link"));
        Assert.Equal("PreserveNewest", updates[0].Element("CopyToOutputDirectory")?.Value);
        Assert.Equal("keep me", updates[0].Element("CustomMetadata")?.Value);
        Assert.Equal("Always", updates[1].Element("CopyToOutputDirectory")?.Value);
        Assert.Contains("'$(Configuration)' == 'QA'", (string?)updates[1].Attribute("Condition"));
        await File.WriteAllTextAsync(project, change.After);
        await AssertBuildsAsync(project);
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static async Task WriteChangesAsync(IReadOnlyList<FileChange> changes)
    {
        foreach (var change in changes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
            await File.WriteAllTextAsync(change.Path, change.After);
        }
    }
    private static async Task AssertBuildsAsync(string project)
    {
        var messages = new List<string>();
        var result = await new BuildService().RunAsync(new BuildRequest(project), new DirectProgress<BuildOutputEvent>(output => { lock (messages) messages.Add(output.Text); }));
        Assert.True(result.ExitCode == 0, string.Join(Environment.NewLine, messages));
    }
    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
