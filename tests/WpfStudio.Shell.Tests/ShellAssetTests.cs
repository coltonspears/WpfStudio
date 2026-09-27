using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Shell.Tests;

public sealed class ShellAssetTests
{
    private const string ProjectText = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>";

    [Fact]
    public async Task ChangingLibraryAssetEditsOwningProjectEvenWhenStartupIsDifferent()
    {
        await using var test = new ShellTestContext();
        var (startup, library, asset) = await CreateProjectsAsync(test);
        test.Shell.SelectedWpfItem = new WpfItem("Asset", "logo.png", asset, 1);
        test.Shell.SelectedBuildAction = "Content";

        Task operation = test.Shell.SetBuildActionCommand.ExecuteAsync(null);
        await WaitForPreviewAsync(test);
        Assert.Equal(library.ProjectPath, Assert.Single(test.Shell.PreviewChanges).Path);
        test.Shell.AcceptPreviewCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("<Content Include=\"Assets\\logo.png\"", test.Store.Find(library.ProjectPath)!.Content);
        Assert.Null(test.Store.Find(startup.ProjectPath));
        Assert.Equal(ProjectText, await File.ReadAllTextAsync(startup.ProjectPath));
        Assert.Equal(ProjectText, await File.ReadAllTextAsync(library.ProjectPath));
    }

    [Fact]
    public async Task PackUriUsesOwningLibraryAssemblyRatherThanStartupAssembly()
    {
        await using var test = new ShellTestContext();
        var (_, _, asset) = await CreateProjectsAsync(test);
        test.Shell.SelectedWpfItem = new WpfItem("Asset", "logo.png", asset, 1);

        await test.Shell.ShowPackUriCommand.ExecuteAsync(null);

        Assert.Equal("pack://application:,,,/Company.Controls;component/Assets/logo.png", test.Shell.Status);
        Assert.Contains(test.Shell.Status, test.Shell.Output);
    }

    [Fact]
    public async Task AmbiguousLinkedAssetRequiresSelectedProjectWithoutChangingAnything()
    {
        await using var test = new ShellTestContext();
        var (startup, library, asset) = await CreateProjectsAsync(test, shared: true);
        test.Shell.SelectedWpfItem = new WpfItem("Asset", "logo.png", asset, 1);

        await test.Shell.SetBuildActionCommand.ExecuteAsync(null);
        Assert.False(test.Shell.IsPreviewOpen);
        Assert.Contains("multiple projects", test.Shell.Status);
        await test.Shell.ShowPackUriCommand.ExecuteAsync(null);
        Assert.Contains("multiple projects", test.Shell.Status);
        Assert.Null(test.Store.Find(startup.ProjectPath));
        Assert.Null(test.Store.Find(library.ProjectPath));
    }

    [Fact]
    public async Task LinkedAssetPackUriUsesEvaluatedLogicalPath()
    {
        await using var test = new ShellTestContext();
        var (_, library, asset) = await CreateProjectsAsync(test, shared: true);
        test.Shell.SelectedNode = new ExplorerNode("logo.png", asset, false, projectPath: library.ProjectPath);
        await test.Shell.ShowPackUriCommand.ExecuteAsync(null);
        Assert.Equal("pack://application:,,,/Company.Controls;component/Images/logo.png", test.Shell.Status);
    }

    [Fact]
    public async Task LinkedAssetNodeSelectsItsProjectForBuildAction()
    {
        await using var test = new ShellTestContext();
        var (startup, library, asset) = await CreateProjectsAsync(test, shared: true);
        test.Shell.SelectedNode = new ExplorerNode("logo.png", asset, false, projectPath: library.ProjectPath);
        test.Shell.SelectedBuildAction = "Resource";

        Task operation = test.Shell.SetBuildActionCommand.ExecuteAsync(null);
        await WaitForPreviewAsync(test);
        Assert.Equal(library.ProjectPath, Assert.Single(test.Shell.PreviewChanges).Path);
        test.Shell.AcceptPreviewCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("<Resource Include=\"..\\Shared\\logo.png\"", test.Store.Find(library.ProjectPath)!.Content);
        Assert.Null(test.Store.Find(startup.ProjectPath));
    }

    private static async Task<(WorkspaceProject Startup, WorkspaceProject Library, string Asset)> CreateProjectsAsync(ShellTestContext test, bool shared = false)
    {
        string startupPath = Path.GetFullPath(await test.CreateFileAsync("Application/Application.csproj", ProjectText));
        string libraryPath = Path.GetFullPath(await test.CreateFileAsync("Controls/Controls.csproj", ProjectText));
        string asset = Path.GetFullPath(await test.CreateFileAsync(shared ? "Shared/logo.png" : "Controls/Assets/logo.png", "asset fixture"));
        var item = new WorkspaceFile(asset, "logo.png", "Resource", LogicalPath: shared ? "Images/logo.png" : null);
        var startup = new WorkspaceProject("app", "Application", startupPath, "net10.0-windows", null, true, shared ? [item] : [], "Company.Application");
        var library = new WorkspaceProject("controls", "Controls", libraryPath, "net10.0-windows", null, false, [item], "Company.Controls");
        test.Shell.Projects.Add(startup);
        test.Shell.Projects.Add(library);
        test.Shell.StartupProject = startup;
        // Startup selection evaluates launch targets asynchronously. Let it finish before
        // assertions or fixture deletion so the test also exercises the actual selection path.
        for (int i = 0; i < 200 && test.Shell.TargetFramework == null; i++) await Task.Delay(50);
        Assert.Equal("net10.0-windows", test.Shell.TargetFramework);
        return (startup, library, asset);
    }

    private static async Task WaitForPreviewAsync(ShellTestContext test)
    {
        for (int i = 0; i < 200 && !test.Shell.IsPreviewOpen; i++) await Task.Delay(10);
        Assert.True(test.Shell.IsPreviewOpen, test.Shell.Status);
    }
}
