using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

public sealed class UiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => _ = dispatcher.BeginInvoke(action);
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "This class owns shell navigation (C8).")]
public sealed class DesktopService : IDesktopService
{
    public void Reveal(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (File.Exists(path)) { start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); }
        else start.ArgumentList.Add(path);
        Process.Start(start);
    }
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new InvalidOperationException("Only web addresses can be opened.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "Starts the official .NET installers through the shell, so they can ask Windows for elevation (C8).")]
public sealed class ShellInstallerLauncher : WpfStudio.Workspace.Runtimes.IInstallerLauncher
{
    public async Task<int> RunAsync(string path, CancellationToken cancellationToken)
    {
        // Passive shows the installer's progress without asking anything; the elevation prompt is the installer's own.
        using var process = Process.Start(new ProcessStartInfo(path, "/install /passive /norestart") { UseShellExecute = true })
            ?? throw new InvalidOperationException("The installer did not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "This class owns the clipboard and its contention handling (C8).")]
public sealed class ClipboardService : IClipboardService
{
    /// <summary>For views, which XAML creates out of the container's reach. It holds no state; view models take <see cref="IClipboardService"/>.</summary>
    public static ClipboardService Shared { get; } = new();
    public bool TrySetText(string text)
    {
        // Another application can hold the clipboard open at any moment.
        try { Clipboard.SetText(text); return true; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException) { return false; }
    }
}

/// <summary>
/// Synchronous by design: every dialog is modal and returns only once the user has answered, on the UI thread, which is
/// where callers must be. The tasks exist for the shared contract. Callers such as the close-and-save flow check state
/// right after the answer, so deferring through the dispatcher would let other queued work run between the two.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "This class owns the dialogs and the default owner window (A8).")]
public sealed class DesktopDialogs(Func<Window?>? owner = null, IThemeService? theme = null) : IFileDialogService, IUserDialogService
{
    private readonly Func<Window?> _owner = owner ?? ShownMainWindow;
    // Before the window is shown, or after it closed, WPF rejects it as an owner.
    private static Window? ShownMainWindow() => Application.Current?.MainWindow is { } main && new WindowInteropHelper(main).Handle != 0 ? main : null;
    private MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        _owner() is { } window ? MessageBox.Show(window, message, title, buttons, image) : MessageBox.Show(message, title, buttons, image);
    public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = initialDirectory ?? "", CheckFileExists = true };
        return Task.FromResult(dialog.ShowDialog(_owner()) == true ? dialog.FileName : null);
    }
    public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = suggestedFileName ?? "", OverwritePrompt = true };
        return Task.FromResult(dialog.ShowDialog(_owner()) == true ? dialog.FileName : null);
    }
    public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory ?? "" };
        return Task.FromResult(dialog.ShowDialog(_owner()) == true ? dialog.FolderName : null);
    }
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(Show($"Save changes to {documentName}?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch { MessageBoxResult.Yes => SaveDecision.Save, MessageBoxResult.No => SaveDecision.Discard, _ => SaveDecision.Cancel });
    public Task ShowErrorAsync(string title, string message) { Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error); return Task.CompletedTask; }
    public Task<string?> PromptAsync(string title, string message, string defaultValue = "")
    {
        // This small presentation-only dialog lives behind the application dialog boundary.
        var input = new TextBox { Text = defaultValue, Margin = new Thickness(0, 12, 0, 16), MinWidth = 380 };
        var ok = new Button { Content = "Continue", IsDefault = true, MinWidth = 88, Style = Application.Current?.TryFindResource("AccentButton") as Style };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 540 });
        stack.Children.Add(input); stack.Children.Add(buttons);
        var window = new Window { Title = title, Content = stack, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = _owner(), Background = Application.Current?.TryFindResource("SurfaceBrush") as Brush ?? SystemColors.WindowBrush, Foreground = Application.Current?.TryFindResource("TextBrush") as Brush ?? SystemColors.WindowTextBrush };
        ok.Click += (_, _) => window.DialogResult = true;
        theme?.Track(window);
        window.ContentRendered += (_, _) => { input.Focus(); input.SelectAll(); };
        return Task.FromResult(window.ShowDialog() == true ? input.Text : null);
    }
}
