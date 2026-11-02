using WpfStudio.App.Features.WpfExplorer;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Shell.Tests;

/// <summary>WPF Explorer over a hand-built index, without a shell.</summary>
public sealed class WpfExplorerViewModelTests
{
    private const string Dictionary = @"C:\Shop\Themes\Colors.xaml", View = @"C:\Shop\Views\MainView.xaml";
    private static WpfIndexSnapshot Index()
    {
        var declaration = new ResourceDeclaration("AccentBrush", Dictionary, 3, 120, 40, "ResourceDictionary");
        return new(
            [new WpfItem("Window", "MainWindow", View, 1), new WpfItem("ViewModel", "MainViewModel", @"C:\Shop\MainViewModel.cs", 1), new WpfItem("SolidColorBrush", "AccentBrush", Dictionary, 3, "AccentBrush"), new WpfItem("Asset", "logo.png", @"C:\Shop\Assets\logo.png", 0)],
            [declaration],
            [new ResourceUsage("AccentBrush", View, 12, 400, 30, false, Dictionary, 120), new ResourceUsage("AccentBrush", View, 20, 700, 30, true, null, null)],
            [new WorkspaceDiagnostic("XAML001", "Unresolved key", "Warning", View, 20, 1, 700, 30)],
            new Dictionary<string, string> { [Dictionary] = "<ResourceDictionary />", [View] = "<Window />" });
    }

    [Fact]
    public void ItemsFollowTheCategoryAndTextFiltersAndKeepTheSelection()
    {
        var explorer = new WpfExplorerViewModel();
        Assert.Equal("Open a WPF project to explore its components.", explorer.Summary);
        explorer.SetIndex(Index());
        Assert.Equal("4 components and resources · 1 named keys", explorer.Summary);
        Assert.Equal("4 items shown", explorer.ResultSummary);

        explorer.SelectedItem = explorer.Items.Single(item => item.Key == "AccentBrush");
        explorer.Category = "Keyed resources";
        Assert.Equal("AccentBrush", Assert.Single(explorer.Items).Name);
        Assert.NotNull(explorer.SelectedItem);
        Assert.True(explorer.CanRenameResource);

        explorer.Category = WpfExplorerViewModel.AllCategory; explorer.Filter = "main";
        Assert.Equal(["MainWindow", "MainViewModel"], explorer.Items.Select(item => item.Name));
        Assert.Null(explorer.SelectedItem);
        Assert.False(explorer.HasSelection);
    }

    [Fact]
    public void SelectingAKeyedResourceListsItsResolvedReferences()
    {
        var explorer = new WpfExplorerViewModel();
        explorer.SetIndex(Index());
        explorer.SelectedItem = explorer.Items.Single(item => item.Key == "AccentBrush");
        var reference = Assert.Single(explorer.References);
        Assert.Equal((View, 12, "StaticResource AccentBrush"), (reference.Path, reference.Line, reference.Text));
        Assert.Equal("1 resolved XAML reference(s); 1 same-key reference(s) need runtime context.", explorer.UsageSummary);
        Assert.Contains("StaticResource or DynamicResource", explorer.SelectedDescription);

        explorer.SelectedItem = explorer.Items.Single(item => item.Kind == "Asset");
        Assert.True(explorer.HasAssetSelection);
        Assert.Empty(explorer.References);
    }

    [Fact]
    public void ResourceLookupUsesDeclarationsThenResolvedUsagesAndOnlyCurrentText()
    {
        var explorer = new WpfExplorerViewModel();
        explorer.SetIndex(Index());
        Assert.True(explorer.IsIndexCurrent(View, "<Window />"));
        Assert.False(explorer.IsIndexCurrent(View, "<Window Title=\"edited\" />"));
        Assert.Equal("AccentBrush", explorer.ResourceAt(Dictionary, 130)?.Key);
        Assert.Equal("AccentBrush", explorer.ResourceAt(View, 410)?.Key);
        Assert.Null(explorer.ResourceAt(View, 710));
        Assert.Null(explorer.ResourceAt(View, 5));

        explorer.SetIssues(Index().Diagnostics);
        Assert.Equal("Issues (1)", explorer.IssueTitle);
    }
}
