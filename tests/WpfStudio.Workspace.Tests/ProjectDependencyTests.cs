using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public class ProjectDependencyTests
{
    private const string LegacyProject = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="Newtonsoft.Json, Version=13.0.0.0, Culture=neutral, PublicKeyToken=30ad4fe6b2a6aeed, processorArchitecture=MSIL">
              <HintPath>..\packages\Newtonsoft.Json.13.0.3\lib\net45\Newtonsoft.Json.dll</HintPath>
            </Reference>
            <Reference Include="PresentationFramework" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\Library\Library.csproj" />
          </ItemGroup>
          <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
        </Project>
        """;
    private const string PackagesConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <packages>
          <package id="Newtonsoft.Json" version="13.0.3" targetFramework="net472" />
        </packages>
        """;

    [Fact]
    public void LegacyProjectsAndTheirDependentsNeedVisualStudioMSBuild()
    {
        var directory = CreateDirectory();
        var legacy = Write(directory, "Legacy/Legacy.csproj", LegacyProject);
        var library = Write(directory, "Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>");
        var importer = Write(directory, "Importer/Importer.csproj", "<Project><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" /><Import Project=\"Sdk.targets\" Sdk=\"Microsoft.NET.Sdk\" /></Project>");
        var consumer = Write(directory, "Consumer/Consumer.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Legacy\\Legacy.csproj\" /></ItemGroup></Project>");
        var sdkSolution = Write(directory, "Sdk.slnx", "<Solution><Project Path=\"Library/Library.csproj\" /><Project Path=\"Importer/Importer.csproj\" /></Solution>");
        var mixedSolution = Write(directory, "Mixed.slnx", "<Solution><Project Path=\"Library/Library.csproj\" /><Project Path=\"Legacy/Legacy.csproj\" /></Solution>");

        Assert.False(ProjectDiscovery.IsSdkStyle(legacy));
        Assert.True(ProjectDiscovery.IsSdkStyle(library));
        Assert.True(ProjectDiscovery.IsSdkStyle(importer));
        Assert.True(ProjectDiscovery.RequiresFrameworkMSBuild(legacy));
        Assert.True(ProjectDiscovery.RequiresFrameworkMSBuild(consumer));
        Assert.True(ProjectDiscovery.RequiresFrameworkMSBuild(mixedSolution));
        Assert.False(ProjectDiscovery.RequiresFrameworkMSBuild(library));
        Assert.False(ProjectDiscovery.RequiresFrameworkMSBuild(sdkSolution));
    }

    [Theory]
    [InlineData("net48", true)]
    [InlineData("net472", true)]
    [InlineData("net8.0-windows", false)]
    [InlineData("netstandard2.0", false)]
    [InlineData("netcoreapp3.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognizesNetFrameworkTargets(string? framework, bool expected) => Assert.Equal(expected, ProjectDiscovery.IsNetFrameworkTarget(framework));

    [Fact]
    public void LegacyProjectsTargetNetFrameworkWithoutATargetFramework()
    {
        var legacy = new WorkspaceProject("1", "Legacy", "Legacy.csproj", "", null, true, [], IsSdkStyle: false, TargetFrameworkMoniker: ".NETFramework,Version=v4.7.2");
        var multiTarget = new WorkspaceProject("2", "Multi", "Multi.csproj", "", null, true, []);
        Assert.True(ProjectDiscovery.TargetsNetFramework(legacy, ""));
        Assert.True(ProjectDiscovery.TargetsNetFramework(multiTarget, "net48"));
        Assert.False(ProjectDiscovery.TargetsNetFramework(multiTarget, "net10.0-windows"));
    }

    [Fact]
    public void DeclaredDependenciesOfALegacyProjectIncludePackagesConfig()
    {
        var directory = CreateDirectory();
        var project = Write(directory, "Legacy/Legacy.csproj", LegacyProject);
        Write(directory, "Legacy/packages.config", PackagesConfig);

        var dependencies = ProjectDiscovery.ReadDeclaredDependencies(project);

        Assert.Contains(new ProjectDependency("Framework", ".NET Framework", "4.7.2"), dependencies);
        Assert.Contains(new ProjectDependency("Package", "Newtonsoft.Json", "13.0.3"), dependencies);
        Assert.Contains(new ProjectDependency("Project", "Library", Path: Path.Combine(directory, "Library", "Library.csproj")), dependencies);
        Assert.Contains(new ProjectDependency("Assembly", "Newtonsoft.Json", "13.0.0.0",
            Path.Combine(directory, "packages", "Newtonsoft.Json.13.0.3", "lib", "net45", "Newtonsoft.Json.dll")), dependencies);
        Assert.Contains(new ProjectDependency("Assembly", "PresentationFramework"), dependencies);
    }

    [Fact]
    public async Task EvaluatedDependenciesNameFrameworksPackagesAndProjects()
    {
        var directory = CreateDirectory();
        Write(directory, "Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>");
        var app = Write(directory, "App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" /><ProjectReference Include=\"..\\Library\\Library.csproj\" /></ItemGroup></Project>");
        var library = Write(directory, "Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup></Project>");
        var multiTarget = Write(directory, "Multi/Multi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net48;net10.0</TargetFrameworks></PropertyGroup></Project>");

        var evaluated = await ProjectDiscovery.EvaluateAsync(app, "Debug", null, default);
        Assert.Equal(".NETCoreApp,Version=v10.0", evaluated.TargetFrameworkMoniker);
        Assert.Contains(new ProjectDependency("Framework", "Microsoft.NETCore.App"), evaluated.Dependencies!);
        Assert.Contains(new ProjectDependency("Package", "Newtonsoft.Json", "13.0.3"), evaluated.Dependencies!);
        Assert.Contains(new ProjectDependency("Project", "Library", Path: library), evaluated.Dependencies!);
        // Dependency items are not project files.
        Assert.DoesNotContain(evaluated.Files, file => file.Name is "Newtonsoft.Json" or "Library.csproj" or "Microsoft.NETCore.App");

        // The SDK's implicit NETStandard.Library reference is not listed as the project's own package.
        Assert.Equal([new ProjectDependency("Framework", ".NET Standard", "2.0")], (await ProjectDiscovery.EvaluateAsync(library, "Debug", null, default)).Dependencies!);

        var multi = await ProjectDiscovery.EvaluateAsync(multiTarget, "Debug", null, default);
        Assert.Null(multi.TargetFrameworkMoniker);
        Assert.Equal([new ProjectDependency("Framework", "net10.0"), new ProjectDependency("Framework", "net48")], multi.Dependencies!);
    }

    [Fact]
    public async Task WorkspaceSnapshotDescribesLegacyAndSdkProjects()
    {
        var directory = CreateDirectory();
        var legacy = Write(directory, "Legacy/Legacy.csproj", LegacyProject);
        Write(directory, "Legacy/packages.config", PackagesConfig);
        var library = Write(directory, "Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var solution = Write(directory, "Mixed.slnx", "<Solution><Project Path=\"Legacy/Legacy.csproj\" /><Project Path=\"Library/Library.csproj\" /></Solution>");
        await using var client = new WorkspaceClient();

        var snapshot = await client.LoadAsync(new LoadWorkspaceRequest(solution));

        var legacyModel = snapshot.Projects.Single(project => project.ProjectPath == legacy);
        Assert.False(legacyModel.IsSdkStyle);
        Assert.Equal(".NETFramework,Version=v4.7.2", legacyModel.TargetFrameworkMoniker);
        Assert.Contains(new ProjectDependency("Package", "Newtonsoft.Json", "13.0.3"), legacyModel.Dependencies!);
        Assert.Contains(new ProjectDependency("Project", "Library", Path: library), legacyModel.Dependencies!);
        var libraryModel = snapshot.Projects.Single(project => project.ProjectPath == library);
        Assert.True(libraryModel.IsSdkStyle);
        Assert.Contains(new ProjectDependency("Framework", "Microsoft.NETCore.App"), libraryModel.Dependencies!);
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Write(string directory, string relativePath, string text)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
