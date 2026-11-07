using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Runtimes;

namespace WpfStudio.App.Features.Runtimes;

/// <summary>What the runtime check needs from the shell beyond the workbench contract.</summary>
public interface IRuntimeCheckHost : IWorkbenchHost
{
    void OpenUrl(string url);
}

/// <summary>The services behind the runtime check, which the container builds and the shell hands on.</summary>
public sealed record RuntimeCheckServices(IDotNetInventorySource Inventory, DotNetReleases Releases, RuntimeInstaller Installer);

/// <summary>One thing the workspace needs that the machine lacks: a runtime, the SDK, or a .NET Framework developer pack.</summary>
public sealed partial class MissingRuntimeItem : ObservableObject
{
    private MissingRuntimeItem(string title, string detail, Uri downloadPage) { Title = title; Detail = detail; DownloadPage = downloadPage; }
    public string Title { get; }
    public string Detail { get; }
    public Uri DownloadPage { get; }
    public RuntimeRequirement? Runtime { get; private init; }
    public SdkRequirement? Sdk { get; private init; }
    public TargetingPackRequirement? TargetingPack { get; private init; }
    /// <summary>Runtimes and SDKs come with their installer in Microsoft's release metadata; a developer pack only has a download page.</summary>
    public bool CanInstall => Runtime is not null || Sdk is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsInstalling { get; set; }
    public bool HasStatus => Status.Length > 0;
    public string Key => Runtime is { } runtime ? runtime.Framework + runtime.Channel : Sdk is { } sdk ? "sdk" + sdk.Version : "pack" + TargetingPack?.Version;

    public static MissingRuntimeItem For(RuntimeRequirement requirement) => new(
        $"{Projects(requirement.Projects)} the {requirement.Title}",
        requirement.Projects.Count == 1 ? "It isn't installed, so the project can't run or be debugged here." : "It isn't installed, so these projects can't run or be debugged here.",
        new Uri($"https://dotnet.microsoft.com/download/dotnet/{requirement.Channel.ToString(2)}")) { Runtime = requirement };

    public static MissingRuntimeItem For(SdkRequirement requirement) => new(
        requirement.Version is null ? "The workspace needs the .NET SDK" : $"global.json asks for the {requirement.Title}",
        requirement.Version is null
            ? "No .NET SDK was found, so the workspace can't load or build. " + requirement.Reason
            : "No installed SDK matches it, so the workspace can't load or build." + (requirement.GlobalJsonPath is { } globalJson ? $" It is set in {globalJson}." : ""),
        new Uri(requirement.Version is { } version && RuntimeRequirements.ParseVersion(version) is { } parsed ? $"https://dotnet.microsoft.com/download/dotnet/{parsed.Major}.{parsed.Minor}" : "https://dotnet.microsoft.com/download")) { Sdk = requirement };

    public static MissingRuntimeItem For(TargetingPackRequirement requirement) => new(
        $"{Projects(requirement.Projects)} the {requirement.Title}",
        "Visual Studio's MSBuild needs its reference assemblies to build the project. Microsoft's download page has the installer.",
        requirement.DownloadPage) { TargetingPack = requirement };

    private static string Projects(IReadOnlyList<string> projects) => projects switch
    {
        [var one] => one + " needs",
        [var first, var second] => $"{first} and {second} need",
        [var first, ..] => $"{first} and {projects.Count - 1} other projects need",
        _ => "The workspace needs"
    };
}

/// <summary>
/// Checks, when a workspace loads, that the runtimes its programs run on and the SDK its global.json asks for are
/// installed, and installs a missing one from Microsoft's release metadata after the user agrees: the official
/// installer, refused unless its SHA-512 hash matches.
/// </summary>
public sealed partial class RuntimeCheckViewModel(IRuntimeCheckHost host, RuntimeCheckServices services) : ObservableObject
{
    private int _checks;
    private string? _workspacePath;
    private IReadOnlyList<WorkspaceProject> _projects = [];
    // What dotnet reports changes only when something is installed, so each answer is kept until then or until Check again.
    private Task<DotNetInventory>? _inventory;
    private readonly Dictionary<string, Task<SdkRequirement?>> _sdks = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<MissingRuntimeItem> Missing { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsVisible))] public partial bool IsDismissed { get; set; }
    public bool IsVisible => Missing.Count > 0 && !IsDismissed;
    public string Summary => Missing.Count == 1 ? "Something this workspace needs isn't installed" : $"{Missing.Count} things this workspace needs aren't installed";

    /// <summary>Checks the workspace; null clears the banner. A newer check supersedes one still running.</summary>
    public async Task CheckAsync(string? workspacePath, IReadOnlyList<WorkspaceProject> projects)
    {
        var check = ++_checks;
        var changedWorkspace = !string.Equals(_workspacePath, workspacePath, StringComparison.OrdinalIgnoreCase);
        _workspacePath = workspacePath; _projects = projects;
        if (workspacePath is null) { Replace([]); return; }
        var directory = Path.GetDirectoryName(Path.GetFullPath(workspacePath))!;
        if (!_sdks.TryGetValue(directory, out var sdkCheck) || sdkCheck.IsFaulted || sdkCheck.IsCanceled)
            _sdks[directory] = sdkCheck = services.Inventory.FindMissingSdkAsync(directory, host.Lifetime);
        if (_inventory is not { IsFaulted: false, IsCanceled: false } inventory) _inventory = inventory = services.Inventory.ReadAsync(directory, host.Lifetime);
        var sdk = await sdkCheck;
        var installed = await inventory;
        if (check != _checks) return;
        var items = new List<MissingRuntimeItem>();
        if (sdk is not null) items.Add(MissingRuntimeItem.For(sdk));
        // Without any dotnet there is nothing to list runtimes with, and the SDK installer brings the newest runtime.
        if (sdk?.Version is not null || installed.Runtimes.Count > 0 || installed.Sdks.Count > 0)
            items.AddRange(RuntimeRequirements.Missing(RuntimeRequirements.For(projects), installed).Select(MissingRuntimeItem.For));
        items.AddRange(RuntimeRequirements.MissingTargetingPacks(projects, services.Inventory.DirectoryExists).Select(MissingRuntimeItem.For));
        // A dismissed banner stays dismissed for the same findings, and comes back for a new workspace or a new finding.
        if (changedWorkspace || items.Any(item => Missing.All(existing => existing.Key != item.Key))) IsDismissed = false;
        Replace(items);
    }

    private void Replace(IReadOnlyList<MissingRuntimeItem> items)
    {
        Missing.Clear();
        foreach (var item in items) Missing.Add(item);
        OnPropertyChanged(nameof(IsVisible)); OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand] private void Dismiss() => IsDismissed = true;
    /// <summary>Asks dotnet again, as after installing something from its download page.</summary>
    [RelayCommand] private Task CheckAgainAsync() => host.GuardAsync(async () =>
    {
        Forget();
        await CheckAsync(_workspacePath, _projects);
        if (Missing.Count == 0) host.ReportStatus("Everything the workspace needs is installed");
    });
    private void Forget() { _inventory = null; _sdks.Clear(); }
    [RelayCommand] private void OpenDownloadPage(MissingRuntimeItem? item) { if (item is not null) host.OpenUrl(item.DownloadPage.AbsoluteUri); }

    [RelayCommand] private Task InstallAsync(MissingRuntimeItem? item) => item is not { CanInstall: true, IsInstalling: false } ? Task.CompletedTask : host.GuardAsync(async () =>
    {
        item.IsInstalling = true;
        item.Status = "Looking up the installer…";
        try
        {
            var rid = DotNetReleases.MachineRid;
            var file = item.Runtime is { } runtime
                ? await services.Releases.FindRuntimeAsync(runtime.Framework, runtime.Channel, rid, host.Lifetime)
                : await services.Releases.FindSdkAsync(item.Sdk!.Version, rid, host.Lifetime);
            var support = file.OutOfSupport ? " Microsoft no longer updates this version." : "";
            if (!await host.Dialogs.ConfirmAsync("Install " + file.Title,
                    $"WpfStudio downloads {file.FileName} from {file.Url.Host}, checks its SHA-512 hash against Microsoft's release list, and runs it. Windows asks for permission to change the computer.{support}"))
            { item.Status = ""; return; }
            var result = await services.Installer.InstallAsync(file, new Progress<string>(message => item.Status = message), host.Lifetime);
            item.Status = result.Outcome switch
            {
                InstallOutcome.Installed => "Installed.",
                InstallOutcome.RestartNeeded => "Installed. Restart Windows to finish.",
                InstallOutcome.Cancelled => "The installation was cancelled.",
                _ => $"The installer stopped with exit code {result.ExitCode}."
            };
            if (result.Outcome is InstallOutcome.Installed or InstallOutcome.RestartNeeded)
            {
                // A workspace that failed to load for want of its SDK loads only when it is opened again.
                host.ReportStatus($"Installed {file.Title}" + (result.Outcome == InstallOutcome.RestartNeeded ? ". Restart Windows to finish." : item.Sdk is not null ? ". Open the solution again to load it." : ""));
                Forget();
                await CheckAsync(_workspacePath, _projects);
            }
        }
        // A failed lookup, download or hash check is shown on its row, where the next try is made.
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidDataException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            item.Status = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "The installation was cancelled." : ex.Message;
            host.ReportStatus(item.Status);
        }
        finally { item.IsInstalling = false; }
    });
}
