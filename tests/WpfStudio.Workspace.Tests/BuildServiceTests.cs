using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public class BuildServiceTests
{
    [Fact]
    public async Task ToolOutputLinesAreBoundedWithoutLosingFollowingLines()
    {
        using var reader = new StringReader(new string('x', BoundedLineReader.MaximumLength * 4) + "\r\n\nnext line\r\ntail");
        var lines = new List<string>();
        await foreach (var line in BoundedLineReader.ReadLinesAsync(reader)) lines.Add(line);
        Assert.Equal(4, lines.Count);
        Assert.Equal(new string('x', BoundedLineReader.MaximumLength) + " [line truncated]", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("next line", lines[2]);
        Assert.Equal("tail", lines[3]);
    }

    [Fact]
    public void ParsesCompilerLocationAndSeverity()
    {
        var diagnostic = BuildService.ParseDiagnostic(@"C:\repo\View.cs(12,8): error CS0103: The name 'Wrong' does not exist [C:\repo\App.csproj]", @"C:\repo");
        Assert.NotNull(diagnostic);
        Assert.Equal(12, diagnostic.Line);
        Assert.Equal(8, diagnostic.Column);
        Assert.Equal("CS0103", diagnostic.Id);
        Assert.Equal("Error", diagnostic.Severity);
        Assert.DoesNotContain("csproj", diagnostic.Message);
    }

    [Fact]
    public void ArgumentsPreserveSpacesWithoutAShell()
    {
        var args = BuildService.CreateArguments(new BuildRequest(@"C:\My app\App.csproj", BuildOperation.Run, "Release", "net10.0-windows", "Desktop", "--name \"two words\" \"\""));
        Assert.Contains(@"C:\My app\App.csproj", args);
        Assert.Contains("two words", args);
        Assert.Equal("", args[^1]);
        Assert.Contains("--launch-profile", args);
    }

    [Fact]
    public void RebuildUsesMsBuildTargetAndRestoreUsesProperty()
    {
        Assert.Contains("-t:Rebuild", BuildService.CreateArguments(new BuildRequest("App.csproj", BuildOperation.Rebuild)));
        var args = BuildService.CreateArguments(new BuildRequest("App.csproj", BuildOperation.Restore, "Release", "net10.0-windows"));
        Assert.Contains("-p:TargetFramework=net10.0-windows", args);
        Assert.DoesNotContain("--framework", args);
    }

    [Fact]
    public void SolutionXmlUsesDeclaredProjects()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpfstudio-{Guid.NewGuid():N}.slnx");
        try
        {
            File.WriteAllText(path, "<Solution><Folder Name='/src/'><Project Path='src/App.csproj' /></Folder></Solution>");
            Assert.Single(ProjectDiscovery.GetProjectPaths(path));
            Assert.EndsWith(Path.Combine("src", "App.csproj"), ProjectDiscovery.GetProjectPaths(path)[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadsSolutionSpecificBuildConfigurations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpfstudio-{Guid.NewGuid():N}.sln");
        try
        {
            File.WriteAllText(path, "Global\nGlobalSection(SolutionConfigurationPlatforms) = preSolution\n  Debug|Any CPU = Debug|Any CPU\n  Shipping|x64 = Shipping|x64\nEndGlobalSection\nEndGlobal");
            Assert.Equal(["Debug", "Shipping"], ProjectDiscovery.GetDeclaredConfigurations(path));
        }
        finally { File.Delete(path); }
    }
}
