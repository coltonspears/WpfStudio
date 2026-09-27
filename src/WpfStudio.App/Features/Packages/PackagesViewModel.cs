using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Packages;

public partial class PackagesViewModel : ObservableObject, IDisposable
{
    private readonly INuGetPackageService _service;
    private readonly IUserDialogService _dialogs;
    private readonly Func<Task<bool>> _saveBeforeChange;
    private readonly Func<Task> _workspaceChanged;
    private CancellationTokenSource? _operation;
    private bool _active;
    private bool _settingWorkspace;
    private bool _applyingInventory;
    private bool _refreshAfterOperation;
    private bool _disposed;
    private int _outputEpoch;
    private string? _workspacePath;

    public PackagesViewModel(INuGetPackageService service, IUserDialogService dialogs,
        Func<Task<bool>> saveBeforeChange, Func<Task> workspaceChanged)
    { _service = service; _dialogs = dialogs; _saveBeforeChange = saveBeforeChange; _workspaceChanged = workspaceChanged; }

    public ObservableCollection<WorkspaceProject> Projects { get; } = [];
    public ObservableCollection<PackageSource> Sources { get; } = [];
    public ObservableCollection<InstalledPackage> Installed { get; } = [];
    public ObservableCollection<PackageSearchItem> SearchResults { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(SearchCommand), nameof(InstallCommand), nameof(RemoveCommand), nameof(LoadVersionsCommand), nameof(InspectInstalledCommand), nameof(RemovePackageCommand))]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    public partial WorkspaceProject? SelectedProject { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(LoadVersionsCommand))]
    public partial PackageSource? SelectedSource { get; set; }
    [ObservableProperty] public partial InstalledPackage? SelectedInstalled { get; set; }
    [ObservableProperty] public partial PackageSearchItem? SelectedSearchResult { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial bool IncludePrerelease { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RemoveCommand), nameof(LoadVersionsCommand))]
    [NotifyPropertyChangedFor(nameof(InstallLabel), nameof(InstalledVersionLabel))]
    public partial string PackageId { get; set; } = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial string SelectedVersion { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "Choose an installed package or search a feed. You can also enter an exact package ID and version.";
    [ObservableProperty] public partial string Authors { get; set; } = "";
    [ObservableProperty] public partial string DownloadLabel { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "Open a solution or project to manage packages.";
    [ObservableProperty] public partial string Warning { get; set; } = "";
    [ObservableProperty] public partial string ConfigurationSummary { get; set; } = "";
    [ObservableProperty] public partial string CentralVersionNotice { get; set; } = "";
    [ObservableProperty] public partial string Output { get; set; } = "Package changes and restore output appear here.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(SearchCommand), nameof(InstallCommand), nameof(RemoveCommand), nameof(LoadVersionsCommand), nameof(CancelCommand), nameof(InspectInstalledCommand), nameof(RemovePackageCommand))]
    public partial bool IsBusy { get; set; }
    public bool IsNotBusy => !IsBusy;
    public bool HasProject => SelectedProject is not null;
    public string InstallLabel => Installed.Any(x => x.Id.Equals(PackageId.Trim(), StringComparison.OrdinalIgnoreCase)) ? "Update version" : "Install package";
    public string InstalledVersionLabel => string.Join("; ", Installed.Where(x => x.Id.Equals(PackageId.Trim(), StringComparison.OrdinalIgnoreCase)).Select(x => $"Installed: {x.ResolvedVersion} ({x.Frameworks})"));

    public void SetWorkspace(WorkspaceSnapshot? workspace)
    {
        var changed = !string.Equals(_workspacePath, workspace?.Path, StringComparison.OrdinalIgnoreCase);
        var selected = SelectedProject?.ProjectPath;
        if (changed) _operation?.Cancel();
        _workspacePath = workspace?.Path;
        _settingWorkspace = true;
        try
        {
            Projects.Clear();
            foreach (var project in workspace?.Projects ?? [])
                if (Path.GetExtension(project.ProjectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) Projects.Add(project);
            SelectedProject = Projects.FirstOrDefault(x => x.ProjectPath.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? Projects.FirstOrDefault();
            if (changed) { Installed.Clear(); SearchResults.Clear(); Versions.Clear(); Sources.Clear(); SelectedSource = null; PackageId = ""; SelectedVersion = ""; }
        }
        finally { _settingWorkspace = false; }
        var targetChanged = !string.Equals(selected, SelectedProject?.ProjectPath, StringComparison.OrdinalIgnoreCase);
        if (IsBusy && (changed || targetChanged))
        {
            _operation?.Cancel();
            _refreshAfterOperation = _active;
        }
        if (SelectedProject is null) Status = "Open a solution or C# project to manage packages.";
        else if (_active && !IsBusy) RefreshCommand.Execute(null);
        else if (!_active) Status = "Select a project, then refresh to load its package references.";
    }

    public async Task ActivateAsync()
    {
        if (_active) return;
        _active = true;
        if (CanRefresh()) await RefreshCommand.ExecuteAsync(null);
    }

    partial void OnSelectedProjectChanged(WorkspaceProject? value)
    {
        if (_settingWorkspace) return;
        _operation?.Cancel();
        if (IsBusy) _refreshAfterOperation = _active;
        Installed.Clear(); SearchResults.Clear(); Versions.Clear(); PackageId = ""; SelectedVersion = "";
        SelectedInstalled = null; SelectedSearchResult = null; Warning = "";
        if (value is not null && _active && !IsBusy) RefreshCommand.Execute(null);
    }
    partial void OnSelectedSourceChanged(PackageSource? value)
    {
        if (!_applyingInventory) _operation?.Cancel();
        SearchResults.Clear(); Versions.Clear();
    }
    partial void OnSelectedInstalledChanged(InstalledPackage? value) => ShowInstalled(value);
    private void ShowInstalled(InstalledPackage? value)
    {
        if (value is null) return;
        PackageId = value.Id; SelectedVersion = value.ResolvedVersion == "Not evaluated" ? value.RequestedVersion : value.ResolvedVersion;
        Versions.Clear(); Description = $"Direct package reference for {value.Frameworks}. Requested version: {value.RequestedVersion}. Load versions to choose an update.";
        Authors = ""; DownloadLabel = "";
    }
    partial void OnSelectedSearchResultChanged(PackageSearchItem? value)
    {
        if (value is null) return;
        PackageId = value.Id; Description = value.Description; Authors = value.Authors; DownloadLabel = value.DownloadLabel;
        Versions.Clear(); foreach (var version in value.Versions) Versions.Add(version);
        SelectedVersion = value.Version;
    }

    private bool CanRefresh() => !_disposed && !IsBusy && SelectedProject is not null;
    private bool CanSearch() => CanRefresh() && SelectedSource is not null;
    private bool CanInstall() => CanRefresh() && !string.IsNullOrWhiteSpace(PackageId) && !string.IsNullOrWhiteSpace(SelectedVersion);
    private bool CanRemove() => CanRefresh() && Installed.Any(x => x.Id.Equals(PackageId.Trim(), StringComparison.OrdinalIgnoreCase));
    private bool CanLoadVersions() => CanSearch() && !string.IsNullOrWhiteSpace(PackageId);
    private bool CanCancel() => IsBusy;
    private bool CanInspectInstalled(InstalledPackage? package) => CanRefresh() && package is not null && Installed.Contains(package);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync() => await RunAsync("Reading package references…", async token =>
    {
        await ReadInventoryAsync(SelectedProject!.ProjectPath, token);
        Status = Installed.Count == 0 ? "No direct package references. Search a feed to add the first package." : $"{Installed.Count} direct package reference{(Installed.Count == 1 ? "" : "s")}.";
    });

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync() => await RunAsync("Searching packages…", async token =>
    {
        var items = await _service.SearchAsync(SelectedSource!, Query, IncludePrerelease, token);
        token.ThrowIfCancellationRequested();
        SearchResults.Clear(); foreach (var item in items) SearchResults.Add(item);
        Status = items.Count == 0 ? "No packages found. Try another search or source." : $"{items.Count} packages shown{(items.Count == 40 ? "; narrow the search for more specific results" : "")}.";
    });

    [RelayCommand(CanExecute = nameof(CanLoadVersions))]
    private async Task LoadVersionsAsync() => await RunAsync("Loading available versions…", async token =>
    {
        var versions = await _service.GetVersionsAsync(SelectedSource!, PackageId.Trim(), IncludePrerelease, token);
        token.ThrowIfCancellationRequested();
        Versions.Clear(); foreach (var version in versions) Versions.Add(version);
        if (versions.Count > 0) SelectedVersion = versions[0];
        Status = versions.Count == 0 ? "No matching versions. Enable prerelease or select another source." : $"{versions.Count} versions available. Review the chosen version before updating.";
    });

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => ChangeAsync(false);
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() => ChangeAsync(true);
    [RelayCommand(CanExecute = nameof(CanInspectInstalled))]
    private void InspectInstalled(InstalledPackage? package) { SelectedInstalled = package; ShowInstalled(package); }
    [RelayCommand(CanExecute = nameof(CanInspectInstalled))]
    private async Task RemovePackageAsync(InstalledPackage? package)
    {
        if (package is null) return;
        SelectedInstalled = package; ShowInstalled(package);
        await ChangeAsync(true);
    }
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _operation?.Cancel();

    private async Task ChangeAsync(bool remove)
    {
        var project = SelectedProject!;
        var id = PackageId.Trim(); var version = SelectedVersion.Trim();
        await RunAsync(remove ? $"Removing {id}…" : $"Installing {id} {version}…", async token =>
        {
            // Validate before saving files or displaying confirmation.
            NuGetPackageService.CreateChangeArguments(project.ProjectPath, id, version, remove);
            if (remove && !await _dialogs.ConfirmAsync("Remove package", $"Remove the {id} reference from {project.Name}? The project will be restored afterward. Shared central versions are left in place."))
            { Status = "Package removal cancelled."; return; }
            token.ThrowIfCancellationRequested();
            if (!await _saveBeforeChange()) { Status = "Package change cancelled because modified documents could not be saved."; return; }
            token.ThrowIfCancellationRequested();
            Output = "";
            var outputEpoch = ++_outputEpoch;
            var output = new Progress<string>(line => { if (!_disposed && _outputEpoch == outputEpoch) AppendOutput(line); });
            try
            {
                var result = await _service.ChangeAsync(project.ProjectPath, id, version, remove, output, token);
                if (string.IsNullOrWhiteSpace(Output)) Output = result.Output;
                Status = result.ExitCode == 0 ? $"{id} {(remove ? "removed" : "installed at " + version)}. Restore completed." : $"Package command exited with code {result.ExitCode}. Review output; project files may have changed before restore failed.";
                await ReadInventoryAsync(project.ProjectPath, token);
                if (result.ExitCode == 0 && remove && Installed.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    Status = $"{id} is still referenced after the command. It may be declared in an imported props/targets file; review the package output and declaration.";
            }
            finally
            {
                // The CLI can modify project/central files before a restore failure or cancellation.
                // Always invalidate the language workspace after an attempted mutation.
                if (!_disposed)
                {
                    try { await _workspaceChanged(); }
                    catch (Exception ex) { AppendOutput("Workspace refresh failed: " + ex.Message); Warning = "Package files may have changed. Reopen the workspace if language services are stale."; }
                }
            }
        });
    }

    private async Task ReadInventoryAsync(string projectPath, CancellationToken token)
    {
        var inventory = await _service.ReadProjectAsync(projectPath, token);
        token.ThrowIfCancellationRequested();
        var source = SelectedSource?.Address;
        Installed.Clear(); foreach (var item in inventory.Packages) Installed.Add(item);
        _applyingInventory = true;
        try
        {
            Sources.Clear(); foreach (var item in inventory.Sources) Sources.Add(item);
            SelectedSource = Sources.FirstOrDefault(x => x.Address.Equals(source, StringComparison.OrdinalIgnoreCase)) ?? Sources.FirstOrDefault();
        }
        finally { _applyingInventory = false; }
        Warning = inventory.Warning ?? (Sources.Count == 0 ? "No enabled package sources. Configure NuGet.Config, then refresh. CLI changes use the project's configured sources." : "");
        ConfigurationSummary = inventory.ConfigurationFiles.Count == 0 ? "NuGet sources resolved by the selected SDK." : "Configuration: " + string.Join(" • ", inventory.ConfigurationFiles);
        CentralVersionNotice = inventory.CentralVersionsFile is null ? "" : "Central versions: " + inventory.CentralVersionsFile + ". Updating a shared version can affect other projects.";
        OnPropertyChanged(nameof(InstallLabel)); OnPropertyChanged(nameof(InstalledVersionLabel)); RemoveCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) return;
        using var operation = new CancellationTokenSource();
        _operation = operation; IsBusy = true; Status = status;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "Package operation cancelled. Any project changes already made by the CLI are kept; review output before retrying."; }
        catch (Exception ex) { Status = ex.Message; AppendOutput(ex.Message); }
        finally
        {
            _operation = null; IsBusy = false;
            if (_refreshAfterOperation && CanRefresh())
            {
                _refreshAfterOperation = false;
                await RefreshAsync();
            }
        }
    }

    private void AppendOutput(string line)
    {
        const int limit = 128 * 1024;
        Output += line + Environment.NewLine;
        if (Output.Length > limit) Output = "[Earlier output trimmed]\n" + Output[^limit..];
    }
    public void Dispose() { _disposed = true; _operation?.Cancel(); }
}
