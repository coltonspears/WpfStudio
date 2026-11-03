using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Behaviors;
using WpfStudio.App.Controls.Diff;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Git;
using WpfStudio.Runtime.Design;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    /// <summary>
    /// Each retained pane comes from its view model's template and keeps the content id its saved layouts use. Tab switches,
    /// closing and reopening, and a layout round trip move the same views; their native child windows (the preview surface,
    /// the terminal's WebView2) must survive those moves rather than be rebuilt.
    /// </summary>
    private async Task VerifyRetainedPanesAsync(string layoutPath, DockingManager manager, ShellViewModel shell)
    {
        LayoutContent Placed(string id) => manager.Layout.Descendents().OfType<LayoutContent>().Single(item => item.ContentId == id);
        async Task<FrameworkElement> ShowAsync(string id)
        {
            shell.ShowToolCommand.Execute(id); await Idle();
            return Assert.IsAssignableFrom<FrameworkElement>(Placed(id).Content);
        }
        Assert.Equal(["Packages", "Git", "Profiler", "Designer", "LiveInspection"], shell.RetainedPanes.Select(pane => pane.ContentId));
        var ids = shell.RetainedPanes.Select(pane => pane.ContentId).Append("Terminal").ToArray();
        var views = new Dictionary<string, FrameworkElement>();
        foreach (var pane in shell.RetainedPanes)
        {
            var view = await ShowAsync(pane.ContentId);
            Assert.Same(pane.Model, view.DataContext);
            Assert.Equal(pane.Title, Placed(pane.ContentId).Title);
            Assert.True(view.IsVisible, pane.ContentId);
            views[pane.ContentId] = view;
        }
        views["Terminal"] = await ShowAsync("Terminal");
        await shell.Terminal.NewSessionCommand.ExecuteAsync(null);
        // A machine without the WebView2 runtime shows a message instead, and there is no browser to move.
        var browser = await TerminalBrowserAsync(views["Terminal"]);
        var hosts = new Dictionary<string, System.Windows.Interop.HwndHost[]>();
        foreach (var id in ids)
        {
            await ShowAsync(id);
            hosts[id] = Descendants<System.Windows.Interop.HwndHost>(views[id]).ToArray();
            output.WriteLine($"{id}: {hosts[id].Length} native child window(s)");
        }
        async Task AssertUnchangedAsync(string step)
        {
            foreach (var id in ids)
            {
                Assert.Same(views[id], await ShowAsync(id));
                Assert.True(views[id].IsVisible, $"{id} after {step}");
                Assert.Equal(hosts[id], Descendants<System.Windows.Interop.HwndHost>(views[id]));
            }
            if (browser is not null) { await ShowAsync("Terminal"); Assert.NotNull(browser.CoreWebView2); }
        }
        await AssertUnchangedAsync("switching tabs");

        await ShowAsync("Git");
        await shell.CloseActiveCommand.ExecuteAsync(null); await Idle();
        Assert.DoesNotContain(manager.Layout.Descendents().OfType<LayoutDocument>(), document => document.ContentId == "Git");
        await AssertUnchangedAsync("reopening a closed workbench");

        shell.SaveLayoutCommand.Execute(null);
        var saved = File.ReadAllText(layoutPath);
        foreach (var id in ids) Assert.Contains($"ContentId=\"{id}\"", saved);
        DockingBehavior.RestoreLayout(manager); await Idle();
        await AssertUnchangedAsync("restoring the layout");

        shell.ResetLayoutCommand.Execute(null); await Idle();
        await AssertUnchangedAsync("resetting the layout");
        await shell.Terminal.CloseSessionCommand.ExecuteAsync(null);
    }

    private static async Task<Microsoft.Web.WebView2.Wpf.WebView2?> TerminalBrowserAsync(FrameworkElement terminal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Descendants<Microsoft.Web.WebView2.Wpf.WebView2>(terminal).FirstOrDefault() is { CoreWebView2: not null } browser) return browser;
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>The popups' cards get their templates and brushes from the application's resources.</summary>
    private static async Task VerifyEditorPopupsAsync(string root)
    {
        var now = DateTimeOffset.Now;
        var commit = new BlameLine("0123456789abcdef", "Ada Lovelace", "ada@example.invalid", now.AddDays(-3), "Fix the parser");
        var nothing = new RelayCommand(() => { });
        var history = BlameHistoryCard.Create("ParseAsync", 4, 9, new([new(commit, 2)], 1), now, nothing, nothing);
        var change = LineChangeCard.Create(new(LineChangeKind.Modified, 1, 1, "committed text\nsecond line"), true, nothing, nothing);
        foreach (var (card, name, label) in new (object, string, string)[] { (history, "blame-history", "Show change"), (change, "line-change", "Revert change") })
        {
            var view = new ContentPresenter { Content = card };
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            await Idle();
            Assert.Contains(Descendants<Button>(view), button => (string)button.Content == label);
            Screenshot(view, Path.Combine(root, $"artifacts/screenshots/{name}.png"));
        }
    }

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
