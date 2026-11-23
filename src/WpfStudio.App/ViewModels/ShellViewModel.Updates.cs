using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WpfStudio.App.Features.Updates;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Updates;

namespace WpfStudio.App.ViewModels;

/// <summary>The shell as the host of Help > Check for updates, which is a page of the About overlay.</summary>
public sealed partial class ShellViewModel : IUpdateHost
{
    private readonly IUpdateLauncher _updateLauncher;

    public UpdatesViewModel Updates { get; }

    /// <summary>
    /// Raised once the shell has closed for an update: unsaved work was dealt with, as <see cref="TryCloseAsync"/> does for
    /// the close button, so the window closes without asking again.
    /// </summary>
    public event Action? ShutdownRequested;

    /// <summary>Help > Check for updates: the Updates page, checking again unless a check or download is under way or an update is ready.</summary>
    [RelayCommand]
    private void CheckForUpdates()
    {
        ShowUpdatesPage();
        if (Updates.CheckCommand.CanExecute(null)) Updates.CheckCommand.ExecuteAsync(null).Forget(ReportBackgroundFault);
    }

    /// <summary>The overlay's Updates tab: checks the first time only.</summary>
    [RelayCommand]
    private void ShowUpdates()
    {
        ShowUpdatesPage();
        if (Updates.State == UpdateState.NotChecked && Updates.CheckCommand.CanExecute(null)) Updates.CheckCommand.ExecuteAsync(null).Forget(ReportBackgroundFault);
    }

    private void ShowUpdatesPage() { IsWhatsNewPage = false; IsUpdatesPage = true; IsAboutOpen = true; }

    void IUpdateHost.OpenUrl(string url) => _desktop.OpenUrl(url);

    async Task<bool> IUpdateHost.RestartToUpdateAsync(StagedUpdate staged, bool automatic)
    {
        // A download finished while the person was doing something else: closing WpfStudio under them would be a surprise.
        if (automatic && !(IsAboutOpen && IsUpdatesPage))
        {
            Status = $"WpfStudio {staged.Version} is downloaded. Help > Check for updates installs it.";
            return false;
        }
        if (UpdateApplier.CountOtherInstances(Updates.ApplicationDirectory) is > 0 and var others)
        {
            Status = $"WpfStudio {staged.Version} is ready. Close the other WpfStudio windows, then choose Restart to update.";
            await _dialogs.ShowErrorAsync("Close the other WpfStudio windows first",
                (others == 1 ? "Another WpfStudio window runs" : $"{others} other WpfStudio windows run") + $" from {Updates.ApplicationDirectory}. Its files cannot be replaced while they are open. Close them, then choose Restart to update on Help > Check for updates.");
            return false;
        }
        if (!await TryCloseAsync())
        {
            Status = $"WpfStudio {staged.Version} is ready. Choose Restart to update on Help > Check for updates when you are done.";
            return false;
        }
        // From here WpfStudio closes whatever happens: TryCloseAsync has ended the session.
        try { _updateLauncher.Start(UpdateCommandLine.ApplyStart(staged, Updates.ApplicationDirectory, Environment.ProcessId, ProductVersion)); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogError(exception, "The update could not start");
            await _dialogs.ShowErrorAsync("WpfStudio was not updated", $"The update could not start: {exception.Message}\n\nWpfStudio closes now. Start it again to keep working.");
        }
        ShutdownRequested?.Invoke();
        return true;
    }
}
