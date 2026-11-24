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
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is ShellViewModel previous) previous.ShutdownRequested -= CloseWithoutAsking;
            if (args.NewValue is not ShellViewModel shell) return;
            shell.Theme.Track(this);
            shell.ShutdownRequested += CloseWithoutAsking;
        };
    }

    /// <summary>For an update: the shell has already asked about unsaved work, so the close guard must not ask again.</summary>
    private void CloseWithoutAsking()
    {
        Behaviors.Interaction.SetCloseGuard(this, null);
        Close();
    }
}
