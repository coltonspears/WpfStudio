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
        _ = new TitleBarController(this, MaximizeButton, Root);
        DataContextChanged += (_, _) => { if (DataContext is ShellViewModel shell) shell.Theme.Track(this); };
    }
}
