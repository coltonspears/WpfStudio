using System.Windows;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // View-only chrome: caption buttons, maximized bounds and Snap Layouts on the maximize button.
        _ = new TitleBarController(this, TitleBar.MaximizeButton, Root);
        // A UserControl's names stay in its own scope; these are looked up on the window.
        RegisterName(nameof(TitleBar.MaximizeButton), TitleBar.MaximizeButton);
        RegisterName(nameof(SolutionExplorer.ExplorerTree), SolutionExplorer.ExplorerTree);
        RegisterName(nameof(WorkspaceSearch.SearchBox), WorkspaceSearch.SearchBox);
        DataContextChanged += (_, _) => { if (DataContext is ShellViewModel shell) shell.Theme.Track(this); };
    }
}
