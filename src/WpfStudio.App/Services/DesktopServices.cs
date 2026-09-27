using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

public sealed class UiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
}

public sealed class DesktopDialogs : IFileDialogService, IUserDialogService
{
    public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = initialDirectory ?? "", CheckFileExists = true };
        return Task.FromResult(dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null);
    }
    public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = suggestedFileName ?? "", OverwritePrompt = true };
        return Task.FromResult(dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null);
    }
    public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory ?? "" };
        return Task.FromResult(dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null);
    }
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(MessageBox.Show(Application.Current.MainWindow, $"Save changes to {documentName}?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch { MessageBoxResult.Yes => SaveDecision.Save, MessageBoxResult.No => SaveDecision.Discard, _ => SaveDecision.Cancel });
    public Task ShowErrorAsync(string title, string message) { MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error); return Task.CompletedTask; }
    public Task<string?> PromptAsync(string title, string message, string defaultValue = "")
    {
        // This small presentation-only dialog lives behind the application dialog boundary.
        var input = new TextBox { Text = defaultValue, Margin = new Thickness(0, 12, 0, 16), MinWidth = 380 };
        var ok = new Button { Content = "Continue", IsDefault = true, MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 540 });
        stack.Children.Add(input); stack.Children.Add(buttons);
        var window = new Window { Title = title, Content = stack, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow, Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceBrush"), Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush") };
        ok.Click += (_, _) => window.DialogResult = true;
        window.ContentRendered += (_, _) => { input.Focus(); input.SelectAll(); };
        return Task.FromResult(window.ShowDialog() == true ? input.Text : null);
    }
}
