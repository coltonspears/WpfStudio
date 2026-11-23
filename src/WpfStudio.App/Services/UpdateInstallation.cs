using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfStudio.Core.Updates;

namespace WpfStudio.App.Services;

/// <summary>Starts the staged package that installs an update. A service, so the shell's restart can be tested without starting anything.</summary>
public interface IUpdateLauncher
{
    void Start(ProcessStartInfo start);
}

public sealed class ProcessUpdateLauncher : IUpdateLauncher
{
    public void Start(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The update did not start.");
    }
}

/// <summary>
/// The staged package's side of an update, when WpfStudio.exe starts with <c>--apply-update</c> instead of as the IDE:
/// it waits for the closing WpfStudio to exit, replaces its files (<see cref="UpdateApplier"/>) and starts it again. If
/// anything fails, the previous version is kept and started instead, after saying why. A small window shows the
/// progress, since copying a few hundred megabytes can take a while.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "Runs instead of the composition root, so like a startup failure it reports with a message box.")]
internal static class UpdateInstallation
{
    public static async Task<int> RunAsync(ApplyRequest request)
    {
        var (window, report) = CreateWindow();
        window.Show();
        string? failure = null;
        report(new UpdateProgress("Waiting for WpfStudio to close…"));
        if (!await UpdateApplier.WaitForExitAsync(request.WaitForProcessId, TimeSpan.FromMinutes(2), CancellationToken.None))
        {
            // Still running, so starting it again would only open a second window.
            window.Close();
            MessageBox.Show("WpfStudio did not close within two minutes, so it was not updated. Close it, then use Help > Check for updates again.",
                "WpfStudio was not updated", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        try
        {
            var progress = new Progress<UpdateProgress>(report); // Created here, so reports arrive on this thread.
            await Task.Run(() => new UpdateApplier().ApplyAsync(AppContext.BaseDirectory, request.Target, progress, CancellationToken.None));
        }
        // Whatever went wrong, the previous version must start again, so nothing is let through.
        catch (Exception exception) { failure = exception is UpdateException ? exception.Message : "WpfStudio could not be updated: " + exception.Message; }

        window.Close();
        if (failure is not null) MessageBox.Show(failure, "WpfStudio was not updated", MessageBoxButton.OK, MessageBoxImage.Warning);
        try { new ProcessUpdateLauncher().Start(UpdateCommandLine.RestartStart(request.Target, failure is null ? request.PreviousVersion : null)); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            MessageBox.Show($"WpfStudio could not be started again: {exception.Message}\n\nStart {Path.Combine(request.Target, InstalledPackage.ExecutableName)} yourself.",
                failure is null ? "WpfStudio was updated" : "WpfStudio was not updated", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        return failure is null ? 0 : 1;
    }

    /// <summary>The theme's colours when the application's resources have them, the system's otherwise.</summary>
    private static (Window Window, Action<UpdateProgress> Report) CreateWindow()
    {
        var resources = Application.Current;
        var heading = new TextBlock { Text = "Updating WpfStudio", FontSize = 16, FontWeight = FontWeights.SemiBold };
        var message = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Width = 380 };
        var bar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 14, 0, 0) };
        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        content.Children.Add(heading); content.Children.Add(message); content.Children.Add(bar);
        var window = new Window
        {
            Title = "Updating WpfStudio", Content = content, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = resources?.TryFindResource("SurfaceBrush") as Brush ?? SystemColors.WindowBrush,
            Foreground = resources?.TryFindResource("TextBrush") as Brush ?? SystemColors.WindowTextBrush,
        };
        return (window, progress =>
        {
            message.Text = progress.Message;
            bar.IsIndeterminate = progress.Fraction is null;
            if (progress.Fraction is { } fraction) bar.Value = fraction;
        });
    }
}
