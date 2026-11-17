using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.App.Features.DevOps;

public partial class PullRequestsView : UserControl
{
    public PullRequestsView() => InitializeComponent();

    /// <summary>The More button opens its menu on a left click, below itself.</summary>
    private void ShowMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
