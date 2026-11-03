using System.Windows;
using WpfStudio.App.Services;

namespace WpfStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // View-only chrome: caption buttons, maximized bounds and Snap Layouts on the maximize button.
        _ = new TitleBarController(this, MaximizeButton, Root);
    }
}
