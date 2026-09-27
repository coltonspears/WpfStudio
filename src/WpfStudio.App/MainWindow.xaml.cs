using System.Windows;
using System.Windows.Input;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App;

public partial class MainWindow : Window
{
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();
        // View-only chrome: caption buttons, maximized bounds and Snap Layouts on the maximize button.
        _ = new TitleBarController(this, MaximizeButton, Root);
        DataContextChanged += (_, _) =>
        {
            if (_shell != null) _shell.SearchFocusRequested -= FocusSearch;
            _shell = DataContext as ShellViewModel;
            if (_shell != null) _shell.SearchFocusRequested += FocusSearch;
        };
    }

    private void FocusSearch() => Dispatcher.BeginInvoke(() => { SearchBox.Focus(); Keyboard.Focus(SearchBox); SearchBox.SelectAll(); });
}
