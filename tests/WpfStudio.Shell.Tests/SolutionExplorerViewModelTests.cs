using System.Collections.ObjectModel;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

/// <summary>The Solution Explorer tree and the related-file locator, without a shell.</summary>
public sealed class SolutionExplorerViewModelTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Shop");
    private static WorkspaceProject Project(string name, params string[] files) =>
        new(name.ToLowerInvariant(), name, Path.Combine(Root, name, name + ".csproj"), "net10.0", null, true,
            files.Select(file => new WorkspaceFile(Path.Combine(Root, name, file), Path.GetFileName(file), file.EndsWith(".xaml") ? "Page" : "Compile")).ToArray());

    [Fact]
    public void TreeShowsProjectsUnderTheSolutionFiltersAndRestoresTheStateAfterwards()
    {
        var host = new Host { WorkspacePath = Path.Combine(Root, "Shop.sln") };
        var projects = new ObservableCollection<WorkspaceProject> { Project("Shop", @"Views\CustomerView.xaml", @"Models\Order.cs"), Project("Shop.Tests", "OrderTests.cs") };
        var explorer = new SolutionExplorerViewModel(host, projects);
        Assert.True(explorer.ShowPlaceholder);

        explorer.Rebuild(keepState: false);
        var solution = Assert.Single(explorer.Nodes);
        Assert.Equal(ExplorerNodeKind.Solution, solution.Kind);
        Assert.Equal(["Shop", "Shop.Tests"], solution.Children.Select(node => node.Name));
        Assert.False(explorer.ShowPlaceholder);
        solution.IsExpanded = true; solution.Children[1].IsExpanded = true;

        explorer.Filter = "customer";
        Assert.True(explorer.IsFiltered);
        ExplorerNode.ExpandAll(explorer.Nodes);
        var filtered = Assert.Single(explorer.Nodes);
        Assert.Equal("Shop", Assert.Single(filtered.Children).Name);

        explorer.Filter = "nothing-matches";
        Assert.Empty(explorer.Nodes);
        Assert.True(explorer.ShowNoMatches);

        explorer.ClearFilterCommand.Execute(null);
        var restored = Assert.Single(explorer.Nodes);
        Assert.True(restored.IsExpanded);
        Assert.True(restored.Children[1].IsExpanded);
        Assert.False(restored.Children[0].IsExpanded);
    }

    [Fact]
    public void TogglingNestingRebuildsAndReportsThroughTheHost()
    {
        var host = new Host { WorkspacePath = Path.Combine(Root, "Shop", "Shop.csproj") };
        var projects = new ObservableCollection<WorkspaceProject> { Project("Shop", "MainWindow.xaml", "MainWindow.xaml.cs") };
        var explorer = new SolutionExplorerViewModel(host, projects);
        explorer.Rebuild(keepState: false);
        var project = Assert.Single(explorer.Nodes);
        Assert.Equal(ExplorerNodeKind.Project, project.Kind);
        ExplorerNode.ExpandAll(explorer.Nodes);
        Assert.Contains(project.Children, node => node.Name == "MainWindow.xaml" && node.Children.Any(child => child.Name == "MainWindow.xaml.cs"));

        explorer.ToggleNestingCommand.Execute(null);
        Assert.False(explorer.NestRelatedFiles);
        Assert.Equal("Related files listed separately in Solution Explorer", host.Status);
        ExplorerNode.ExpandAll(explorer.Nodes);
        Assert.Contains(Assert.Single(explorer.Nodes).Children, node => node.Name == "MainWindow.xaml.cs");
    }

    [Fact]
    public async Task ContextCommandsSetTheStartupProjectAndOpenItsProjectFile()
    {
        var host = new Host { WorkspacePath = Path.Combine(Root, "Shop.sln") };
        var projects = new ObservableCollection<WorkspaceProject> { Project("Shop", "Program.cs"), Project("Shop.Tests", "Tests.cs") };
        var explorer = new SolutionExplorerViewModel(host, projects);
        explorer.Rebuild(keepState: false);
        explorer.SelectedNode = explorer.Nodes[0].Children[1];
        explorer.SetStartupProjectCommand.Execute(null);
        Assert.Equal("Shop.Tests", host.Startup?.Name);

        explorer.SelectedNode = null;
        await explorer.EditProjectCommand.ExecuteAsync(null);
        Assert.Equal([projects[1].ProjectPath], host.Opened);
    }

    [Fact]
    public void RelatedFilesFollowConventionsAndRoles()
    {
        string view = Path.Combine(Root, "Views", "CustomerView.xaml"), code = view + ".cs", model = Path.Combine(Root, "ViewModels", "CustomerViewModel.cs");
        string[] files = [view, code, model, Path.Combine(Root, "Program.cs")];
        bool Exists(string path) => files.Contains(path, StringComparer.OrdinalIgnoreCase);

        Assert.Equal([code, model], RelatedFileLocator.Conventional(view, files, Exists));
        Assert.Equal([model, view], RelatedFileLocator.Conventional(code, files, Exists));
        Assert.Equal([view], RelatedFileLocator.Conventional(model, files, Exists));
        Assert.Empty(RelatedFileLocator.Conventional(Path.Combine(Root, "Program.cs"), files, Exists));

        Assert.Equal("Code-behind", RelatedFileLocator.RoleFor(view, code));
        Assert.Equal("View model", RelatedFileLocator.RoleFor(view, model));
        Assert.Equal("View", RelatedFileLocator.RoleFor(model, view));
        Assert.Equal("Related", RelatedFileLocator.RoleFor(Path.Combine(Root, "A.cs"), Path.Combine(Root, "B.cs")));

        Assert.Equal("CustomerViewModel", RelatedFileLocator.DataContextType("<UserControl d:DataContext=\"{d:DesignInstance Type=vm:CustomerViewModel}\">"));
        Assert.Equal("ShellViewModel", RelatedFileLocator.DataContextType("<Window><Window.DataContext><local:ShellViewModel /></Window.DataContext></Window>"));
        Assert.Null(RelatedFileLocator.DataContextType("<Grid />"));
    }

    [Fact]
    public void LocationsAreRelativeToTheWorkspaceAndTruncated()
    {
        Assert.Equal("Shop  ›  Views", RelatedFileLocator.LocationFor(Path.Combine(Root, "Views", "CustomerView.xaml"), Root));
        Assert.Equal("Shop", RelatedFileLocator.LocationFor(Path.Combine(Root, "Program.cs"), Root));
        Assert.Equal("…  ›  c  ›  d  ›  e", RelatedFileLocator.LocationFor(Path.Combine(Root, "a", "b", "c", "d", "e", "Deep.cs"), Root));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Elsewhere").TrimEnd(Path.DirectorySeparatorChar), RelatedFileLocator.RelativeDirectory(Path.Combine(Path.GetTempPath(), "Elsewhere", "File.cs"), Root));
    }

    private sealed class Host : ISolutionExplorerHost
    {
        public string? WorkspacePath { get; set; }
        public string Status { get; private set; } = "";
        public string? StartupProjectPath { get; set; }
        public WorkspaceProject? Startup { get; private set; }
        public void SetStartupProject(WorkspaceProject project) { Startup = project; StartupProjectPath = project.ProjectPath; }
        public event Action? DocumentsChanged;
        public void ChangeDocuments() => DocumentsChanged?.Invoke();
        public IReadOnlyList<string> WorkspaceFiles => [];
        public IReadOnlyList<string> OpenDocumentPaths => [];
        public IReadOnlyDictionary<string, string> OpenDocumentTexts() => new Dictionary<string, string>();
        public (string Path, int Line)? ActiveLocation => null;
        public string? SelectedExplorerFile => null;
        public IUserDialogService Dialogs => new FakeDialogs();
        public CancellationToken Lifetime => CancellationToken.None;
        public Task GuardAsync(Func<Task> action) => action();
        public void ReportStatus(string status) => Status = status;
        public Task NavigateAsync(string path, int line, int column) => Task.CompletedTask;
        public List<string> Opened { get; } = [];
        public Task OpenDocumentAsync(string path) { Opened.Add(path); return Task.CompletedTask; }
        public Task<bool> CloseDocumentAsync(string path) => Task.FromResult(true);
        public void ShowTool(string toolId) { }
    }
}
