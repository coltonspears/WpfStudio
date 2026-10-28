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
    public void VisualStudioMSBuildRestoresPackagesConfigAndReceivesTheSolutionFolder()
    {
        var solution = Path.Combine(Path.GetTempPath(), "Shop", "Shop.sln");
        var build = BuildService.CreateFrameworkMSBuildArguments(new BuildRequest("App.csproj", BuildOperation.Build, "Release", SolutionPath: solution));
        Assert.Equal("App.csproj", build[0]);
        Assert.Contains("-restore", build);
        Assert.Contains("-p:RestorePackagesConfig=true", build);
        Assert.Contains("-t:Build", build);
        Assert.Contains("-p:Configuration=Release", build);
        Assert.Contains("-nodeReuse:false", build);
        Assert.Contains("-p:SolutionDir=" + Path.Combine(Path.GetTempPath(), "Shop") + Path.DirectorySeparatorChar, build);

        Assert.Contains("-t:Rebuild", BuildService.CreateFrameworkMSBuildArguments(new BuildRequest("App.csproj", BuildOperation.Rebuild)));
        Assert.Contains("-t:Restore", BuildService.CreateFrameworkMSBuildArguments(new BuildRequest("App.csproj", BuildOperation.Restore)));
        var clean = BuildService.CreateFrameworkMSBuildArguments(new BuildRequest("App.csproj", BuildOperation.Clean));
        Assert.Contains("-t:Clean", clean);
        Assert.DoesNotContain("-restore", clean);
        // A solution build defines SolutionDir itself.
        Assert.DoesNotContain(BuildService.CreateFrameworkMSBuildArguments(new BuildRequest(solution, SolutionPath: solution)), argument => argument.StartsWith("-p:SolutionDir=", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildService.CreateFrameworkMSBuildArguments(new BuildRequest("App.csproj", BuildOperation.Test)));
    }

    [Fact]
    public async Task LegacyLaunchEvaluationSeesTheSolutionDirTheBuildUsed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "App"));
        var project = Path.Combine(directory, "App", "App.csproj");
        await File.WriteAllTextAsync(project, """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <AssemblyName>App</AssemblyName>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                <OutputPath>$(SolutionDir)bin\$(Configuration)\</OutputPath>
              </PropertyGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """);
        // MSBuild before Visual Studio 2022 17.8 rejects -getProperty; evaluation then uses the .NET SDK with the same SolutionDir.
        var oldMSBuild = Path.Combine(directory, OperatingSystem.IsWindows() ? "old-msbuild.cmd" : "old-msbuild.sh");
        await File.WriteAllTextAsync(oldMSBuild, OperatingSystem.IsWindows() ? "@exit /b 1\r\n" : "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(oldMSBuild, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var fromSolution = await ProjectDiscovery.EvaluateAsync(project, "Debug", null, default, Path.Combine(directory, "Shop.sln"), oldMSBuild);
        var alone = await ProjectDiscovery.EvaluateAsync(project, "Debug", null, default);

        Assert.Equal(Path.Combine(directory, "bin", "Debug", "App.exe"), fromSolution.OutputPath);
        Assert.Equal(Path.Combine(directory, "App", "bin", "Debug", "App.exe"), alone.OutputPath);
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
