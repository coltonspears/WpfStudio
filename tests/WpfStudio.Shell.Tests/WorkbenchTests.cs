using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed class WorkbenchTests
{
    [Theory]
    [InlineData("MainWindow.xaml.cs", "MainWindow.xaml")]
    [InlineData("Resources.Designer.cs", "Resources.resx")]
    [InlineData("ShellViewModel.Tools.cs", "ShellViewModel.cs")]
    [InlineData("ShellViewModel.Tools.Extra.cs", "ShellViewModel.Tools.cs")]
    [InlineData("appsettings.Development.json", "appsettings.json")]
    [InlineData("ShellViewModel.cs", null)]
    [InlineData("Unrelated.Part.cs", null)]
    public void NestingParentFollowsVisualStudioConventions(string fileName, string? expected)
    {
        var siblings = new HashSet<string>(["MainWindow.xaml", "MainWindow.xaml.cs", "Resources.resx", "Resources.Designer.cs", "ShellViewModel.cs",
            "ShellViewModel.Tools.cs", "ShellViewModel.Tools.Extra.cs", "appsettings.json", "appsettings.Development.json", "Unrelated.Part.cs"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, ExplorerNode.NestingParent(fileName, siblings));
    }

    [Fact]
    public void CodeBehindNestsUnderItsXamlUnlessDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "App");
        string[] files = [Path.Combine(root, "MainWindow.xaml"), Path.Combine(root, "MainWindow.xaml.cs"), Path.Combine(root, "App.cs")];

        var nested = ExplorerNode.FromFiles(root, files).ToArray();
        Assert.Equal(["App.cs", "MainWindow.xaml"], nested.Select(node => node.Name));
        Assert.Equal("MainWindow.xaml.cs", Assert.Single(nested[1].Children).Name);
        Assert.False(nested[1].IsFolder);

        var flat = ExplorerNode.FromFiles(root, files, nestRelated: false).ToArray();
        Assert.Equal(["App.cs", "MainWindow.xaml", "MainWindow.xaml.cs"], flat.Select(node => node.Name));
    }

    [Fact]
    public void FilteredProjectKeepsOnlyMatchingFilesAndDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "Shop");
        var project = new WorkspaceProject("shop", "Shop", Path.Combine(root, "Shop.csproj"), "net10.0", null, true,
            [new(Path.Combine(root, "Views", "CustomerView.xaml"), "CustomerView.xaml", "Page"), new(Path.Combine(root, "Models", "Order.cs"), "Order.cs", "Compile")],
            Dependencies: [new("Package", "CommunityToolkit.Mvvm", "8.4.2"), new("Package", "Dapper", "2.1.0")]);

        Assert.Null(ExplorerNode.ForFilteredProject(project, "nothing"));
        var node = ExplorerNode.ForFilteredProject(project, "customer")!;
        ExplorerNode.ExpandAll([node]);
        var views = Assert.Single(node.Children);
        Assert.Equal("Views", views.Name);
        Assert.Equal("CustomerView.xaml", Assert.Single(views.Children).Name);

        var byPackage = ExplorerNode.ForFilteredProject(project, "dapper")!;
        ExplorerNode.ExpandAll([byPackage]);
        var package = byPackage.Children.Single().Children.Single().Children.Single();
        Assert.Equal("Dapper", package.Name);

        var byFolder = ExplorerNode.ForFilteredProject(project, "models/")!;
        ExplorerNode.ExpandAll([byFolder]);
        Assert.Equal("Order.cs", byFolder.Children.Single().Children.Single().Name);
    }

    [Fact]
    public void ProblemTermsMatchCodeMessageFileAndProjectAndCanExclude()
    {
        var diagnostic = new WorkspaceDiagnostic("CS0103", "The name 'total' does not exist", "Error", @"C:\Shop\Order.cs", 4, 1, 0, 5, ProjectName: "Shop");
        Assert.True(ShellViewModel.ProblemMatches(diagnostic, "cs0103"));
        Assert.True(ShellViewModel.ProblemMatches(diagnostic, "total"));
        Assert.True(ShellViewModel.ProblemMatches(diagnostic, "order.cs"));
        Assert.True(ShellViewModel.ProblemMatches(diagnostic, "shop"));
        Assert.False(ShellViewModel.ProblemMatches(diagnostic, "-total"));
        Assert.True(ShellViewModel.ProblemMatches(diagnostic, "-CS8618"));
    }

    [Fact]
    public async Task ProblemsFilterBySeverityScopeAndText()
    {
        await using var test = new ShellTestContext();
        string first = await test.CreateFileAsync("First.cs", "class First { }");
        string second = await test.CreateFileAsync("Second.cs", "class Second { }");
        await test.Shell.OpenDocumentAsync(first);
        var firstEditor = test.Shell.ActiveDocument!;
        await test.Shell.OpenDocumentAsync(second);
        var secondEditor = test.Shell.ActiveDocument!;
        firstEditor.Diagnostics.Add(new("CS0103", "Missing name", "Error", first, 1, 1, 0, 1));
        secondEditor.Diagnostics.Add(new("CS0168", "Unused variable", "Warning", second, 1, 1, 0, 1));

        Assert.Equal(2, test.Shell.VisibleDiagnostics.Count);
        test.Shell.ShowWarningProblems = false;
        Assert.Equal("CS0103", Assert.Single(test.Shell.VisibleDiagnostics).Id);
        Assert.Equal("1 of 2 shown", test.Shell.ProblemFilterSummary);

        test.Shell.ShowWarningProblems = true;
        test.Shell.ProblemScope = "Current file";
        Assert.Equal("CS0168", Assert.Single(test.Shell.VisibleDiagnostics).Id);

        test.Shell.ProblemScope = "All files";
        test.Shell.ProblemFilter = "missing";
        Assert.Equal("CS0103", Assert.Single(test.Shell.VisibleDiagnostics).Id);

        test.Shell.ClearProblemFilterCommand.Execute(null);
        Assert.Equal(2, test.Shell.VisibleDiagnostics.Count);
        Assert.False(test.Shell.IsProblemFilterActive);
    }

    [Fact]
    public async Task CloseSavedTabsKeepsTabsWithUnsavedEdits()
    {
        await using var test = new ShellTestContext();
        string clean = await test.CreateFileAsync("Clean.cs", "class Clean { }");
        string dirty = await test.CreateFileAsync("Dirty.cs", "class Dirty { }");
        await test.Shell.OpenDocumentAsync(clean);
        await test.Shell.OpenDocumentAsync(dirty);
        test.Store.Find(dirty)!.Content = "class Edited { }";

        await test.Shell.CloseSavedDocumentsCommand.ExecuteAsync(null);

        Assert.Equal(dirty, Assert.Single(test.Shell.Documents).State.Path);
        Assert.Empty(test.Dialogs.SaveQuestions);
    }

    [Fact]
    public async Task CloseOtherAndCloseToTheRightKeepTheExpectedTabs()
    {
        await using var test = new ShellTestContext();
        var paths = new List<string>();
        foreach (var name in new[] { "A.cs", "B.cs", "C.cs", "D.cs" }) { var path = await test.CreateFileAsync(name, "class X { }"); paths.Add(path); await test.Shell.OpenDocumentAsync(path); }

        test.Shell.ActiveDocument = test.Shell.Documents[1];
        await test.Shell.CloseDocumentsToRightCommand.ExecuteAsync(null);
        Assert.Equal(["A.cs", "B.cs"], test.Shell.Documents.Select(d => Path.GetFileName(d.State.Path)));

        await test.Shell.CloseOtherDocumentsCommand.ExecuteAsync(null);
        Assert.Equal("B.cs", Path.GetFileName(Assert.Single(test.Shell.Documents).State.Path));
    }

    [Fact]
    public async Task CancellingASavePromptStopsCloseAllAndKeepsTheRemainingTabs()
    {
        await using var test = new ShellTestContext();
        string first = await test.CreateFileAsync("First.cs", "one");
        string second = await test.CreateFileAsync("Second.cs", "two");
        string third = await test.CreateFileAsync("Third.cs", "three");
        foreach (var path in new[] { first, second, third }) await test.Shell.OpenDocumentAsync(path);
        test.Store.Find(second)!.Content = "edited";
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Cancel);

        await test.Shell.CloseAllDocumentsCommand.ExecuteAsync(null);

        Assert.Equal([second, third], test.Shell.Documents.Select(d => d.State.Path));
    }

    [Fact]
    public async Task ClosingWithoutASolutionLeavesTheShellAsItWas()
    {
        await using var test = new ShellTestContext();
        Assert.True(await test.Shell.CloseWorkspaceAsync());
        Assert.False(test.Shell.HasWorkspace);
        Assert.Equal("No solution is open", test.Shell.Status);
    }

    [Fact]
    public void ReleaseNotesListTheNewestEntryFirst()
    {
        Assert.NotEmpty(ReleaseNoteCatalog.All);
        Assert.All(ReleaseNoteCatalog.All, note => Assert.NotEmpty(note.Highlights));
        Assert.Equal("Unreleased", ReleaseNoteCatalog.All[0].Date);
    }

    [Fact]
    public void FileGroupsIgnoreDuplicateFilesAndRoundTripToTheirModel()
    {
        var group = new FileGroupViewModel("Invoices", [@"C:\Shop\InvoiceView.xaml", @"C:\Shop\InvoiceViewModel.cs", @"c:\shop\invoiceview.xaml"]);
        Assert.Equal("2 files", group.Summary);
        group.Remove(group.Files[0]);
        Assert.Equal("1 file", group.Summary);
        var model = group.ToModel();
        Assert.Equal("Invoices", model.Name);
        Assert.Equal([@"C:\Shop\InvoiceViewModel.cs"], model.Files);
    }

    [Fact]
    public async Task FileGroupCommandsNeedAnOpenSolution()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("A.cs", "class A { }");
        await test.Shell.OpenDocumentAsync(path);
        await test.Shell.AddOpenTabsToFileGroupCommand.ExecuteAsync(null);
        Assert.Empty(test.Shell.FileGroups);
        Assert.Equal("Open a solution to pin file groups for it", test.Shell.Status);
    }
}
