using System.IO;
using System.Text.RegularExpressions;
using WpfStudio.App.Controls;
using WpfStudio.App.Features.Workbench;

namespace WpfStudio.Shell.Tests;

public sealed class WorkbenchIdTests
{
    private static string MainWindowXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "WpfStudio.App", "MainWindow.xaml");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("MainWindow.xaml was not found above the test output.");
    }

    [Fact]
    public void MainWindowOnlyUsesKnownIdsAsStrings()
    {
        var xaml = MainWindowXaml();
        var ids = Regex.Matches(xaml, @"ContentId=""([^""{]+)""|ShowToolCommand\}"" CommandParameter=""([^""{]+)""").Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Distinct().ToArray();
        Assert.Contains(Workbenches.Search, ids);
        Assert.Contains(Workbenches.Welcome, ids);
        Assert.All(ids, id => Assert.Contains(id, Workbenches.All));
    }

    // Saved layout files contain these exact strings.
    [Theory]
    [InlineData(Workbenches.Explorer, "Explorer")]
    [InlineData(Workbenches.FileGroups, "FileGroups")]
    [InlineData(Workbenches.WpfTools, "WpfTools")]
    [InlineData(Workbenches.Assistant, "ColtonGPT")]
    [InlineData(Workbenches.Output, "Output")]
    [InlineData(Workbenches.Problems, "Problems")]
    [InlineData(Workbenches.TaskList, "TaskList")]
    [InlineData(Workbenches.Search, "Search")]
    [InlineData(Workbenches.Terminal, "Terminal")]
    [InlineData(Workbenches.Debugger, "Debugger")]
    [InlineData(Workbenches.Database, "Database")]
    [InlineData(Workbenches.Designer, "Designer")]
    [InlineData(Workbenches.LiveInspection, "LiveInspection")]
    [InlineData(Workbenches.Packages, "Packages")]
    [InlineData(Workbenches.Git, "Git")]
    [InlineData(Workbenches.Profiler, "Profiler")]
    [InlineData(Workbenches.Welcome, "Welcome")]
    public void ValuesStayCompatibleWithSavedLayouts(string id, string saved) => Assert.Equal(saved, id);

    [Fact]
    public void OnlyPackagesGitAndProfilerAreDocumentTabs() =>
        Assert.Equal([Workbenches.Packages, Workbenches.Git, Workbenches.Profiler], Workbenches.All.Where(id => Workbenches.IsDocument(id)));

    [Theory]
    [InlineData("WPF", Workbenches.WpfTools)]
    [InlineData("Resources", Workbenches.WpfTools)]
    [InlineData("SQL Server", Workbenches.Database)]
    [InlineData(Workbenches.Output, Workbenches.Output)]
    public void OlderToolNamesResolve(string name, string id) => Assert.Equal(id, Workbenches.Canonical(name));

    [Fact]
    public void TheGlyphConverterSharesTheIds()
    {
        Assert.Equal("Bug", ContentGlyphConverter.KindFor(Workbenches.Debugger));
        Assert.Equal("Chat", ContentGlyphConverter.KindFor(Workbenches.Assistant));
    }
}
