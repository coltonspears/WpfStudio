using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed class SolutionExplorerTests
{
    [Fact]
    public void ProjectNodeGroupsItsDependenciesByKind()
    {
        var project = new WorkspaceProject("app", "App", Path.Combine(Path.GetTempPath(), "App", "App.csproj"), "net10.0-windows", null, true, [], Dependencies:
        [
            new("Framework", "Microsoft.NETCore.App"),
            new("Package", "CommunityToolkit.Mvvm", "8.4.2"),
            new("Project", "Controls", Path: Path.Combine(Path.GetTempPath(), "Controls", "Controls.csproj")),
        ]);

        var node = ExplorerNode.ForProject(project);
        node.IsExpanded = true;

        Assert.True(node.CanBuild);
        var dependencies = node.Children[0];
        Assert.Equal("Dependencies", dependencies.Name);
        Assert.False(dependencies.CanBuild);
        dependencies.IsExpanded = true;
        Assert.Equal(["Frameworks", "Packages", "Projects"], dependencies.Children.Select(group => group.Name));
        var packages = dependencies.Children[1];
        packages.IsExpanded = true;
        var package = Assert.Single(packages.Children);
        Assert.Equal(ExplorerNodeKind.Dependency, package.Kind);
        Assert.Equal("8.4.2", package.Detail);
        Assert.Equal("Package", package.IconKind);
        Assert.Equal(project.ProjectPath, package.ProjectPath);
    }

    [Fact]
    public void LegacyProjectsListReferencesAndSolutionsContainTheirProjects()
    {
        var legacy = new WorkspaceProject("legacy", "Legacy", Path.Combine(Path.GetTempPath(), "Legacy", "Legacy.csproj"), "", null, true, [],
            IsSdkStyle: false, Dependencies: [new("Assembly", "PresentationFramework")]);
        var projectNode = ExplorerNode.ForProject(legacy);

        var solution = ExplorerNode.ForSolution(Path.Combine(Path.GetTempPath(), "Shop.sln"), [projectNode]);

        Assert.Equal("Solution 'Shop'", solution.Name);
        Assert.Equal("1 project", solution.Detail);
        Assert.True(solution.CanBuild);
        Assert.True(solution.IsExpanded);
        Assert.Same(projectNode, Assert.Single(solution.Children));
        projectNode.IsExpanded = true;
        Assert.Equal("References", projectNode.Children[0].Name);
    }

    [Fact]
    public void RebuiltTreeKeepsExpandedAndSelectedNodes()
    {
        var project = new WorkspaceProject("app", "App", Path.Combine(Path.GetTempPath(), "App", "App.csproj"), "net10.0", null, true, [],
            Dependencies: [new("Package", "CommunityToolkit.Mvvm", "8.4.2")]);
        var before = ExplorerNode.ForSolution(Path.Combine(Path.GetTempPath(), "App.sln"), [ExplorerNode.ForProject(project)]);
        var beforeProject = before.Children[0];
        beforeProject.IsExpanded = true;
        beforeProject.Children[0].IsExpanded = true;
        beforeProject.Children[0].Children[0].IsSelected = true;

        var state = ExplorerNode.CaptureState([before]);
        var after = ExplorerNode.ForSolution(Path.Combine(Path.GetTempPath(), "App.sln"), [ExplorerNode.ForProject(project)]);
        ExplorerNode.RestoreState([after], state);

        var dependencies = after.Children[0].Children[0];
        Assert.True(after.Children[0].IsExpanded);
        Assert.True(dependencies.IsExpanded);
        Assert.False(dependencies.Children[0].IsExpanded);
        Assert.True(dependencies.Children[0].IsSelected);
    }

    [Fact]
    public async Task ContextMenuBuildsNeedASolutionOrProjectNode()
    {
        await using var test = new ShellTestContext();
        test.Shell.SelectedNode = new ExplorerNode("App.xaml", Path.Combine(test.Root, "App.xaml"), false);

        await test.Shell.BuildSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Select a solution or project in Solution Explorer", test.Shell.Status);
        Assert.False(test.Shell.IsBusy);
    }

    [Fact]
    public async Task DebuggingANetFrameworkApplicationOffersToRunItWithoutTheDebugger()
    {
        await using var test = new ShellTestContext();
        var legacy = new WorkspaceProject("legacy", "Legacy", Path.Combine(test.Root, "Legacy.csproj"), "", null, true, [],
            IsSdkStyle: false, TargetFrameworkMoniker: ".NETFramework,Version=v4.8");
        test.Shell.Projects.Add(legacy);
        test.Shell.StartupProject = legacy;
        test.Dialogs.Confirmations.Enqueue(false);

        await test.Shell.DebugCommand.ExecuteAsync(null);

        Assert.Empty(test.Dialogs.Confirmations);
        Assert.False(test.Shell.Debugger.IsActive);
        Assert.False(test.Shell.IsBusy);
    }
}
