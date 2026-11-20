using WpfStudio.App.Features.Workbench;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

/// <summary>
/// Find in files through the shell: what each scope covers once a solution is loaded, and how the file lists and the file
/// types narrow it. What a line matches and which files a list names are covered where they are decided, in the Core suite.
/// </summary>
public sealed class FindInFilesTests
{
    [Fact]
    public Task ASearchLooksWhereItsScopeAndItsFileListsSay() => ShellTestContext.OnUiThreadAsync(async test =>
    {
        var shell = test.Shell;
        shell.SearchDelay = TimeSpan.Zero;
        // In a folder of its own: the shell's settings and logs are kept beside it, not in it.
        var solution = await test.CreateFileAsync("ws/Fixture.slnx", "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"Library/Library.csproj\" /></Solution>");
        var app = await test.CreateFileAsync("ws/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await test.CreateFileAsync("ws/App/Program.cs", "System.Console.WriteLine(\"needle\");");
        await test.CreateFileAsync("ws/App/Views/Main.xaml", "<Grid Tag=\"needle\" />");
        var library = await test.CreateFileAsync("ws/Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var store = await test.CreateFileAsync("ws/Library/OrderStore.cs", "namespace Demo; public sealed class OrderStore { } // needle");
        await test.CreateFileAsync("ws/Library/notes.md", "A needle in the notes.");
        await test.CreateFileAsync("ws/README.md", "needle");
        foreach (var project in new[] { app, library }) Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        await shell.LoadWorkspaceAsync(solution);
        Assert.NotNull(shell.Workspace);
        string[] Files() => shell.SearchResults.Select(result => result.FileName).ToArray();

        Assert.Equal(["Entire solution", "Current project", "Open documents", "Current document", "App", "Library"], shell.SearchScopes.Select(scope => scope.Label));
        shell.SearchQuery = "needle";
        await shell.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["Program.cs", "Main.xaml", "notes.md", "OrderStore.cs", "README.md"], Files());
        Assert.Equal("5 matches for “needle” in 5 files", shell.SearchResultsTitle);
        Assert.Equal([".md", ".cs", ".xaml"], shell.SearchFileTypes.Select(type => type.Extension));

        // A project is its own files and the loose ones in its folder. The change searches by itself.
        shell.SearchScope = shell.SearchScopes.Single(scope => scope.Label == "Library");
        await shell.SearchCompletion;
        Assert.Equal(["notes.md", "OrderStore.cs"], Files());
        Assert.Equal("2 matches for “needle” in 2 files · project Library", shell.SearchResultsTitle);
        shell.SearchInclude = "*.cs";
        await shell.SearchCompletion;
        Assert.Equal(["OrderStore.cs"], Files());

        // One of the file types among the matches, then all of them again.
        shell.SearchInclude = ""; shell.SearchScope = SearchScope.Solution;
        await shell.SearchCompletion;
        shell.SearchFileTypes.Single(type => type.Extension == ".md").IsSelected = true;
        await shell.SearchCompletion;
        Assert.Equal(["notes.md", "README.md"], Files());
        Assert.Equal("2 matches for “needle” in 2 files · only .md", shell.SearchResultsTitle);
        Assert.Equal(3, shell.SearchFileTypes.Count);
        shell.SearchFileTypes.Single(type => type.IsSelected).IsSelected = false;
        await shell.SearchCompletion;
        Assert.Equal(5, shell.SearchResults.Count);

        // A folder chosen in Solution Explorer.
        shell.Explorer.SelectedNode = new ExplorerNode("Views", Path.Combine(Path.GetDirectoryName(app)!, "Views"), true);
        shell.FindInSelectedCommand.Execute(null);
        await shell.SearchCompletion;
        Assert.Equal("Folder: " + Path.Combine("App", "Views"), shell.SearchScope?.Label);
        Assert.Equal(["Main.xaml"], Files());
        Assert.True(shell.IsSearchFocusRequested);

        // The current project is the active document's, and a document is searched as it stands in its editor.
        await shell.OpenDocumentAsync(store);
        shell.SearchScope = SearchScope.CurrentProject;
        await shell.SearchCompletion;
        Assert.Equal(["notes.md", "OrderStore.cs"], Files());
        shell.ActiveDocument!.State.Content = "// nothing to find here";
        shell.SearchScope = SearchScope.CurrentDocument;
        await shell.SearchCompletion;
        Assert.Empty(shell.SearchResults);
        Assert.Equal("No match for “needle” in 1 file · OrderStore.cs", shell.SearchResultsTitle);
        Assert.False(shell.IsSearching);
    });

    [Fact]
    public Task WithNoSolutionTheOpenDocumentsAreWhatIsSearched() => ShellTestContext.OnUiThreadAsync(async test =>
    {
        var shell = test.Shell;
        shell.SearchDelay = TimeSpan.Zero;
        var path = await test.CreateFileAsync("Loose.txt", "first\nthe needle\n");
        await shell.OpenDocumentAsync(path);
        shell.SearchQuery = "needle";
        await shell.SearchCompletion;
        var row = Assert.Single(shell.SearchResults);
        Assert.Equal((path, 2, 5), (row.Path, row.Line, row.Column));
        Assert.Equal("1 match for “needle” in 1 file · open documents", shell.SearchResultsTitle);
        Assert.False(shell.IsSearching);
        Assert.False(shell.SearchCancelCommand.CanExecute(null));

        // An emptied box takes its matches with it.
        shell.SearchQuery = "";
        await shell.SearchCompletion;
        Assert.Empty(shell.SearchResults);
        Assert.Equal("", shell.SearchResultsTitle);
    });

    [Fact]
    public Task TheEditorsSelectionIsWhatToFind() => ShellTestContext.OnUiThreadAsync(async test =>
    {
        var shell = test.Shell;
        shell.SearchDelay = TimeSpan.Zero;
        await shell.OpenDocumentAsync(await test.CreateFileAsync("Order.cs", "class Order\n{\n    int count;\n}\n"));
        var editor = shell.ActiveDocument!;
        editor.UpdateSelection(editor.State.Content.IndexOf("count", StringComparison.Ordinal), "count".Length);
        var shown = new List<string>();
        shell.ToolRequested += shown.Add;
        shell.FindInFilesCommand.Execute(null);
        await shell.SearchCompletion;
        Assert.Equal("count", shell.SearchQuery);
        Assert.Equal([Workbenches.Search], shown);
        Assert.True(shell.IsSearchFocusRequested);
        Assert.Equal(3, Assert.Single(shell.SearchResults).Line);

        // Pressed again with the caret in the box, the shortcut does not put the same selection back over what was typed since.
        shell.SearchQuery = "total";
        shell.FindInFilesCommand.Execute(null);
        Assert.Equal("total", shell.SearchQuery);

        // Several lines are a passage, not something to look for.
        editor.UpdateSelection(0, editor.State.Content.Length);
        shell.FindInFilesCommand.Execute(null);
        Assert.Equal("total", shell.SearchQuery);
    });
}
