using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WpfStudio.App.Controls.Diff;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    /// <summary>Selecting in the explorer reaches its view model, which starts with nothing selected, and a focus request lands once the pane is up.</summary>
    private static async Task VerifySelectionAndFocusRequestsAsync(Window window, ShellViewModel shell)
    {
        var node = shell.Explorer.Nodes[0];
        node.IsSelected = true; await Idle();
        Assert.Same(node, shell.Explorer.SelectedNode);

        var search = (TextBox)window.FindName("SearchBox");
        shell.FindInWorkspaceCommand.Execute(null); await Idle();
        Assert.True(search.IsVisible);
        Assert.Same(search, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(search)));
        Assert.False(shell.IsSearchFocusRequested);
    }

    private static async Task VerifyDesignerPaneFilterAsync(IUiDispatcher dispatcher)
    {
        await using var first = new DesignerViewModel(new PreviewClient(null), dispatcher);
        await using var second = new DesignerViewModel(new PreviewClient(null), dispatcher);
        var pane = new DesignerPane { DataContext = first };
        var firstView = CollectionViewSource.GetDefaultView(first.Properties);
        var secondView = CollectionViewSource.GetDefaultView(second.Properties);
        Assert.NotNull(firstView.Filter);

        pane.DataContext = second;
        Assert.Null(firstView.Filter);
        var property = new PreviewProperty("Width", "System.Double", "10", "Local", false, false, false, true);
        first.PropertyFilter = "no such property";
        Assert.True(secondView.Filter(property));
        second.PropertyFilter = "no such property";
        Assert.False(secondView.Filter(property));

        pane.DataContext = null;
        Assert.Null(secondView.Filter);
    }

    private static async Task VerifyLargeDiffsAsync()
    {
        static string Lines(string text) => string.Join('\n', Enumerable.Range(0, 30_000).Select(index => $"{text} {index}"));
        static async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddMinutes(1);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "The diff did not arrive.");
                await Task.Delay(20);
            }
        }
        var view = new DiffView { Before = Lines("same"), After = Lines("same") + "\nlast" };
        await UntilAsync(() => view.Diff is { NewLineCount: 30_001 });

        // A large comparison still running gives way to the next one, and its result never replaces it.
        view.Before = Lines("first");
        view.After = Lines("second");
        await Idle();
        view.Before = "old";
        view.After = "new";
        await UntilAsync(() => view.Diff is { OldLineCount: 1 });
        await Task.Delay(500);
        Assert.Equal(1, view.Diff!.OldLineCount);
    }
}
