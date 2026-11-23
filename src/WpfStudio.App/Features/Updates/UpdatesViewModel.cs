using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Updates;

namespace WpfStudio.App.Features.Updates;

public enum UpdateState { NotChecked, Checking, UpToDate, NoReleases, Available, Downloading, Ready, Failed }

/// <summary>What Help > Check for updates needs from the shell.</summary>
public interface IUpdateHost
{
    /// <summary>
    /// Closes WpfStudio the way its close button does, asking about unsaved work, then starts the staged package to replace
    /// this one and start it again. False when the person keeps WpfStudio open. <paramref name="automatic"/> is true right
    /// after a download, which the person may no longer be watching; the host may then leave the restart to them.
    /// </summary>
    Task<bool> RestartToUpdateAsync(StagedUpdate staged, bool automatic);
    void OpenUrl(string url);
}

/// <summary>
/// Help > Check for updates: asks GitHub for the newest release, shows its notes, and with <b>Update and restart</b>
/// downloads and verifies it, then closes WpfStudio, which the staged package replaces and starts again.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private readonly UpdateService _service;
    private readonly IUpdateHost _host;
    private readonly IUiDispatcher _dispatcher;
    private Task _cleanup = Task.CompletedTask;
    private CancellationTokenSource? _work;

    public UpdatesViewModel(UpdateService service, IUpdateHost host, IUiDispatcher dispatcher) { _service = service; _host = host; _dispatcher = dispatcher; }

    public string CurrentVersion => _service.CurrentVersion?.ToString() ?? "a development build";
    public string ApplicationDirectory => _service.ApplicationDirectory;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsReady), nameof(CanInstall), nameof(ShowInstall), nameof(InstallLabel), nameof(ShowActivity), nameof(ShowProgress))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand), nameof(CancelCommand))]
    public partial UpdateState State { get; private set; }
    /// <summary>The page's headline: what was found, or what is happening.</summary>
    [ObservableProperty] public partial string Message { get; private set; } = "";
    /// <summary>The sentence under it: why an update cannot be installed here, what went wrong, or the download's progress.</summary>
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial double Progress { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowActivity), nameof(ShowProgress))] public partial bool IsProgressIndeterminate { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRelease), nameof(ReleaseHeading), nameof(ReleaseNotes), nameof(CanInstall), nameof(ShowInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial UpdateCheck? LastCheck { get; private set; }
    public StagedUpdate? Staged { get; private set; }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;
    /// <summary>The sweeping bar, while how long is unknown; <see cref="ShowProgress"/> once the download's size is.</summary>
    public bool ShowActivity => IsBusy && IsProgressIndeterminate;
    public bool ShowProgress => IsBusy && !IsProgressIndeterminate;
    public bool IsReady => State == UpdateState.Ready && Staged is not null;
    public bool CanInstall => !IsBusy && !IsReady && LastCheck?.CanInstall == true;
    public bool ShowInstall => CanInstall || IsReady;
    public string InstallLabel => IsReady ? "Restart to update" : "Update and restart";
    public bool HasRelease => LastCheck?.Release is not null;
    public string ReleaseHeading => LastCheck?.Release is { } release
        ? release.Title + (release.PublishedAt is { } published ? " · " + published.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "")
        : "";
    public string ReleaseNotes => LastCheck?.Release is { } release ? ReleaseNotesText.ToPlainText(release.Notes) is { Length: > 0 } notes ? notes : "This release has no notes." : "";

    /// <summary>
    /// At startup: removes what a finished update left behind, and returns the line to show when this start was the end of
    /// an update.
    /// </summary>
    public string? Start(IReadOnlyList<string> arguments)
    {
        _cleanup = Task.Run(() =>
        {
            try { _service.RemoveLeftovers(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The next start tries again. */ }
        });
        return UpdateCommandLine.UpdatedFrom(arguments) is { } previous ? $"Updated WpfStudio from {previous} to {CurrentVersion}" : null;
    }

    private bool CanCheck() => !IsBusy && !IsReady;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        var work = Begin();
        State = UpdateState.Checking; Message = "Checking for updates…"; Detail = ""; IsProgressIndeterminate = true;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
        limit.CancelAfter(CheckTimeout);
        try
        {
            var check = await _service.CheckAsync(limit.Token);
            LastCheck = check;
            if (check.Release is not { } release)
            {
                State = UpdateState.NoReleases;
                Message = "No release has been published yet.";
                Detail = $"You have WpfStudio {CurrentVersion}.";
            }
            else if (!check.IsNewer)
            {
                State = UpdateState.UpToDate;
                Message = $"WpfStudio {CurrentVersion} is up to date.";
                Detail = release.Version == _service.CurrentVersion ? "It is the newest release." : $"The newest release is {release.Version}.";
            }
            else
            {
                State = UpdateState.Available;
                Message = $"WpfStudio {release.Version} is available.";
                Detail = check.CannotInstall ?? $"You have {CurrentVersion}. Updating downloads {UpdateDownloader.FormatSize(check.Asset!.Size)}, then closes WpfStudio, replaces it and starts it again with the same workspace and files.";
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            Fail("The check was cancelled.", "", keepCheck: false);
        }
        catch (OperationCanceledException)
        {
            Fail("Could not check for updates.", $"GitHub did not answer within {CheckTimeout.TotalSeconds:0} seconds.", keepCheck: false);
        }
        catch (UpdateException exception)
        {
            Fail("Could not check for updates.", exception.Message, keepCheck: false);
        }
        finally { End(work); }
    }

    [RelayCommand(CanExecute = nameof(ShowInstall))]
    private async Task InstallAsync()
    {
        if (IsReady) { await _host.RestartToUpdateAsync(Staged!, automatic: false); return; }
        if (LastCheck is not { CanInstall: true, Release: { } release } check) return;
        var work = Begin();
        State = UpdateState.Downloading; Message = $"Downloading WpfStudio {release.Version}…"; Detail = ""; Progress = 0; IsProgressIndeterminate = true;
        var progress = new UiProgress(this);
        StagedUpdate staged;
        try
        {
            await _cleanup; // Startup's cleanup must not remove what this downloads.
            staged = await Task.Run(() => _service.DownloadAsync(check, progress, work.Token), work.Token);
            Staged = staged;
            State = UpdateState.Ready;
            Message = $"WpfStudio {staged.Version} is ready to install.";
            Detail = "WpfStudio closes, asking about unsaved files first, then updates and starts again with the same workspace and files.";
            Progress = 1; IsProgressIndeterminate = false;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            State = UpdateState.Available; Message = $"WpfStudio {release.Version} is available."; Detail = "The download was cancelled.";
            return;
        }
        catch (Exception exception) when (exception is UpdateException or IOException or UnauthorizedAccessException)
        {
            Fail($"WpfStudio {release.Version} could not be downloaded.", exception.Message, keepCheck: true);
            return;
        }
        finally { End(work); }
        await _host.RestartToUpdateAsync(staged, automatic: true);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _work?.Cancel();

    [RelayCommand]
    private void OpenReleasePage() => _host.OpenUrl((LastCheck?.Release?.Page ?? _service.Feed.ReleasesPage).AbsoluteUri);

    private CancellationTokenSource Begin()
    {
        var work = new CancellationTokenSource();
        Interlocked.Exchange(ref _work, work)?.Dispose();
        return work;
    }

    private void End(CancellationTokenSource work)
    {
        if (Interlocked.CompareExchange(ref _work, null, work) == work) work.Dispose();
        IsProgressIndeterminate = false;
    }

    private void Fail(string message, string detail, bool keepCheck)
    {
        if (!keepCheck) LastCheck = null;
        State = UpdateState.Failed; Message = message; Detail = detail;
    }

    /// <summary>Shows the download's progress on the UI thread. Reports that arrive after it ended are ignored.</summary>
    private sealed class UiProgress(UpdatesViewModel owner) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => owner._dispatcher.Post(() =>
        {
            if (owner.State != UpdateState.Downloading) return;
            owner.Detail = value.Message;
            owner.IsProgressIndeterminate = value.Fraction is null;
            if (value.Fraction is { } fraction) owner.Progress = fraction;
        });
    }
}
