using WpfStudio.App.Features.Workbench;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

/// <summary>The command palette against a fake host, without a shell.</summary>
public sealed class CommandPaletteViewModelTests
{
    [Fact]
    public async Task CommandModeRanksByFuzzyScoreAndExecutesTheSelection()
    {
        var host = new Host();
        var ran = new List<string>();
        host.Commands = [Entry("Build", "Build", ran), Entry("Build", "Rebuild", ran), Entry("View", "Output", ran)];
        var palette = new CommandPaletteViewModel(host);

        palette.OpenCommands();
        Assert.True(palette.IsOpen);
        Assert.True(palette.IsCommandMode);
        Assert.Equal(3, palette.Results.Count);
        palette.Query = ">rbld";
        Assert.Equal("Rebuild", Assert.Single(palette.Results).Label);
        palette.Query = ">zzzz";
        Assert.Empty(palette.Results);

        palette.Query = ">bu";
        Assert.Equal("Build", palette.Results[0].Label);
        palette.NextCommand.Execute(null);
        Assert.Equal("Rebuild", palette.Selected?.Label);
        await palette.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(["Rebuild"], ran);
        Assert.False(palette.IsOpen);
    }

    [Fact]
    public void FileModeListsOpenDocumentsFirstThenWorkspaceFilesByName()
    {
        var host = new Host
        {
            Files = [@"C:\Shop\Views\CustomerView.xaml", @"C:\Shop\ViewModels\CustomerViewModel.cs", @"C:\Shop\Program.cs"],
            OpenPaths = [@"C:\Shop\Program.cs"],
        };
        var palette = new CommandPaletteViewModel(host);
        palette.OpenFiles();
        Assert.False(palette.IsCommandMode);
        Assert.Equal("Program.cs", palette.Results[0].Label);
        palette.Query = "cvm";
        Assert.Equal("CustomerViewModel.cs", palette.Results[0].Label);
        palette.Query = ">";
        Assert.True(palette.IsCommandMode);
    }

    [Fact]
    public async Task SymbolModeQueriesTheHostAfterAPauseAndNavigatesOnExecute()
    {
        var host = new Host { Symbols = [new("Customer", "Shop.Models", "Class", new(@"C:\Shop\Customer.cs", 120, 8, 7, 14))] };
        var palette = new CommandPaletteViewModel(host);
        palette.OpenSymbols();
        Assert.Empty(palette.Results);
        palette.Query = "#cust";
        await Task.Delay(400);
        var entry = Assert.Single(palette.Results);
        Assert.Equal("Customer", entry.Label);
        Assert.Contains("Customer.cs:7", entry.Detail);
        await palette.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal([(@"C:\Shop\Customer.cs", 7, 14)], host.Navigations);
    }

    [Fact]
    public void FuzzyScoreFavoursPrefixesAndWordStarts()
    {
        Assert.Null(CommandPaletteViewModel.FuzzyScore("Build", "xyz"));
        Assert.NotNull(CommandPaletteViewModel.FuzzyScore("CounterViewModel.cs", "cvm"));
        Assert.True(CommandPaletteViewModel.FuzzyScore("Build", "bu") > CommandPaletteViewModel.FuzzyScore("Rebuild", "bu"));
        Assert.Equal(0, CommandPaletteViewModel.FuzzyScore("anything", ""));
    }

    private static PaletteEntry Entry(string category, string label, List<string> ran) =>
        new(label, "", () => { ran.Add(label); return Task.CompletedTask; }, category);

    private sealed class Host : ICommandPaletteHost
    {
        public void PaletteOpenChanged() { }
        public List<string> Files { get; set; } = [];
        public List<string> OpenPaths { get; set; } = [];
        public List<PaletteEntry> Commands { get; set; } = [];
        public List<SymbolSearchResult> Symbols { get; set; } = [];
        public List<(string Path, int Line, int Column)> Navigations { get; } = [];
        public event Action? DocumentsChanged;
        public void ChangeDocuments() => DocumentsChanged?.Invoke();
        public IEnumerable<PaletteEntry> CommandEntries() => Commands;
        public PaletteEntry FileEntry(string path) => new(Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", () => Task.CompletedTask, IconKind: "File");
        public string WorkspaceRoot => @"C:\Shop";
        public bool IsSymbolSearchAvailable => true;
        public Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(string query, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SymbolSearchResult>>(Symbols.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
        public string? WorkspacePath => @"C:\Shop\Shop.sln";
        public IReadOnlyList<string> WorkspaceFiles => Files;
        public IReadOnlyList<string> OpenDocumentPaths => OpenPaths;
        public IReadOnlyDictionary<string, string> OpenDocumentTexts() => new Dictionary<string, string>();
        public (string Path, int Line)? ActiveLocation => null;
        public string? SelectedExplorerFile => null;
        public IUserDialogService Dialogs => new FakeDialogs();
        public CancellationToken Lifetime => CancellationToken.None;
        public Task GuardAsync(Func<Task> action) => action();
        public void ReportStatus(string status) { }
        public Task NavigateAsync(string path, int line, int column) { Navigations.Add((path, line, column)); return Task.CompletedTask; }
        public Task OpenDocumentAsync(string path) => Task.CompletedTask;
        public Task<bool> CloseDocumentAsync(string path) => Task.FromResult(true);
    }
}
