using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlResourceMetadataIntegrationTests
{
    [Fact]
    public async Task EvaluatedResourceAliasesKeepLinkAndDeploymentSemanticsSeparate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-ResourceMetadata-" + Guid.NewGuid().ToString("N"));
        string projectDirectory = Path.Combine(directory, "Project");
        Directory.CreateDirectory(projectDirectory);
        string project = Path.Combine(projectDirectory, "Fixture.csproj");
        try
        {
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultItems>false</EnableDefaultItems>
                    <AliasRoot>Pages</AliasRoot>
                  </PropertyGroup>
                  <ItemGroup>
                    <Page Include="Views/Page.xaml" Link="Ignored.xaml" LogicalName="$(AliasRoot)/Alias.xaml" />
                    <Resource Include="../Shared/Linked.xaml" Link="Themes/Linked.xaml" />
                    <Resource Include="../Shared/External.xaml" />
                    <Content Include="Assets/Copy.xaml" TargetPath="Data/Copy.xaml" CopyToOutputDirectory="PreserveNewest" />
                    <Content Include="Assets/Uncopied.xaml" TargetPath="Data/Uncopied.xaml" />
                    <Resource Include="Assets/Unmapped.xaml" LogicalName="../Unmapped.xaml" />
                  </ItemGroup>
                </Project>
                """);
            var evaluated = await ProjectDiscovery.EvaluateAsync(project, "Release", null, default);
            Assert.Equal("Pages/Alias.xaml", Resource("Page.xaml").ResourcePath);
            Assert.Equal("Themes/Linked.xaml", Resource("Linked.xaml").ResourcePath);
            Assert.Equal("External.xaml", Resource("External.xaml").ResourcePath);
            Assert.Equal("Data/Copy.xaml", Resource("Copy.xaml").ResourcePath);
            Assert.Null(Resource("Uncopied.xaml").ResourcePath);
            Assert.NotNull(Resource("Uncopied.xaml").Status);
            Assert.Null(Resource("Unmapped.xaml").ResourcePath);
            Assert.All(evaluated.XamlResources, item => Assert.True(Path.IsPathFullyQualified(item.Path)));

            ProjectDiscovery.EvaluatedXamlResource Resource(string name) =>
                Assert.Single(evaluated.XamlResources, item => Path.GetFileName(item.Path) == name);
        }
        finally { Directory.Delete(directory, true); }
    }
}
