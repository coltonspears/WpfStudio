using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Git;
using WpfStudio.Core.DevOps;

namespace WpfStudio.App.Features.DevOps;

/// <summary>
/// The Access and settings tab: what the signed-in token can do, feature by feature, checked without changing anything on the
/// service, and the workbench's preferences.
/// </summary>
public sealed partial class DevOpsAccessViewModel(IDevOpsHost host) : ObservableObject, IDisposable
{
    private readonly LatestLoad _check = new();
    private int _loadedVersion = -1;

    public ObservableCollection<DevOpsAccessEntry> Entries { get; } = [];

    [ObservableProperty] public partial bool IsChecking { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "";
    [ObservableProperty] public partial string BranchTemplate { get; set; } = "";
    [ObservableProperty] public partial string HiddenStates { get; set; } = "";
    [ObservableProperty] public partial string RefreshSeconds { get; set; } = "";
    [ObservableProperty] public partial bool KeepLogTimestamps { get; set; }
    [ObservableProperty] public partial bool DeleteSourceBranch { get; set; }
    [ObservableProperty] public partial bool CompleteWorkItems { get; set; }

    public bool IsAzureDevOps => host.Provider?.Target.Kind == DevOpsProviderKind.AzureDevOps;
    public string BranchPreview => DevOpsText.BranchName(BranchTemplate, 123, "Fix the login page", "Bug");

    partial void OnBranchTemplateChanged(string value) => OnPropertyChanged(nameof(BranchPreview));

    internal void EnsureLoaded()
    {
        if (_loadedVersion == host.Version) return;
        _loadedVersion = host.Version;
        OnPropertyChanged(nameof(IsAzureDevOps));
        LoadSettings();
        Check();
    }

    internal void Reload()
    {
        if (_loadedVersion != host.Version) EnsureLoaded();
        else Check();
    }

    internal void Reset()
    {
        _check.Cancel();
        _loadedVersion = -1;
        Entries.Clear();
        Summary = "";
    }

    internal IEnumerable<Task> Loads => [_check.Task];

    private void LoadSettings()
    {
        var settings = host.Settings;
        BranchTemplate = settings.BranchTemplate;
        HiddenStates = settings.HiddenStates;
        RefreshSeconds = settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        KeepLogTimestamps = settings.KeepLogTimestamps;
        DeleteSourceBranch = settings.DeleteSourceBranch;
        CompleteWorkItems = settings.CompleteWorkItems;
    }

    [RelayCommand]
    private void Check()
    {
        if (host.Provider is not { } provider) return;
        _check.Start(async token =>
        {
            var entries = await provider.CheckAccessAsync(token);
            token.ThrowIfCancellationRequested();
            GitViewModel.Replace(Entries, entries);
            var denied = entries.Count(entry => entry.State == AccessState.Denied);
            var unknown = entries.Count(entry => entry.State == AccessState.Unknown);
            Summary = denied == 0 && unknown == 0 ? "Everything the workbench offers is available with this sign-in."
                : (denied > 0 ? $"{denied} feature{(denied == 1 ? " needs" : "s need")} more access. " : "")
                  + (unknown > 0 ? $"{unknown} could not be checked here." : "");
        }, host.ReportError, loading => IsChecking = loading);
    }

    [RelayCommand]
    private void OpenTokenPage()
    {
        if (host.Provider is { } provider) host.OpenUrl(provider.TokenPageUrl);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(RefreshSeconds.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 3600)
        {
            host.ReportError(new InvalidOperationException("Refresh running pipelines every: enter a number of seconds from 0 (off) to 3600."));
            return;
        }
        if (string.IsNullOrWhiteSpace(DevOpsText.BranchName(BranchTemplate, 1, "x", "x")) || !BranchTemplate.Contains("{id}", StringComparison.OrdinalIgnoreCase))
        {
            host.ReportError(new InvalidOperationException("The branch name template must contain {id}, such as feature/{id}-{title}."));
            return;
        }
        try
        {
            await host.SaveSettingsAsync(host.Settings with
            {
                BranchTemplate = BranchTemplate.Trim(), HiddenStates = HiddenStates.Trim(), RefreshSeconds = seconds, KeepLogTimestamps = KeepLogTimestamps,
                DeleteSourceBranch = DeleteSourceBranch, CompleteWorkItems = CompleteWorkItems
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { host.ReportError(ex); }
    }

    [RelayCommand]
    private void RevertSettings() => LoadSettings();

    internal void NotifyCommands() { }

    public void Dispose() => _check.Cancel();
}
