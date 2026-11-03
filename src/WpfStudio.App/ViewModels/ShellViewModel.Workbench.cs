using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core;

namespace WpfStudio.App.ViewModels;

/// <summary>Closing the solution and groups of tabs, filtering Problems and Solution Explorer, and the Help pages.</summary>
public sealed partial class ShellViewModel
{
    private void InitializeWorkbench()
    {
        Documents.CollectionChanged += (_, _) => NotifyOpenDocumentsChanged();
    }

    // ------------------------------------------------------------------ Close solution
    [RelayCommand] private Task CloseSolutionAsync() => GuardAsync(async () => { await CloseWorkspaceAsync(); });
    /// <summary>Closes every tab and unloads the solution, returning to the start page. False when the user cancels a save prompt.</summary>
    public async Task<bool> CloseWorkspaceAsync()
    {
        var onlyLooseFiles = Workspace == null && Explorer.Nodes.Count == 0;
        if (onlyLooseFiles && Documents.Count == 0) { Status = "No solution is open"; return true; }
        if (IsBusy || _loading) { Status = "Wait for the current workspace operation to finish"; return false; }
        if (IsPreviewOpen) { Status = "Finish or cancel the change preview before closing the solution"; return false; }
        if (!await CloseAllDocumentsForWorkspaceAsync()) return false;
        if (Debugger.IsActive) await Debugger.StopCommand.ExecuteAsync(null);
        if (_gitToolOpened && Features != null) await Features.Git.SetWorkspaceAsync(null);
        var name = Workspace == null ? "" : Path.GetFileName(Workspace.Path);
        _history.Clear();
        _allFiles = [];
        Explorer.Clear(); Projects.Clear(); SearchResults.Clear(); WpfExplorer.Items.Clear();
        _updatingChoices = true;
        TargetFrameworks.Clear(); LaunchProfiles.Clear(); TargetFramework = null; LaunchProfile = null;
        _updatingChoices = false;
        StartupProject = null;
        Workspace = null;
        RefreshEditorDiagnostics();
        await TaskList.LoadUserTasksAsync(); await FileGroups.LoadAsync(); await TaskList.RefreshCommand.ExecuteAsync(null);
        try { await _workspace.UnloadAsync(_lifetime.Token); } catch (Exception ex) { AppendOutput("Language worker shutdown: " + ex.Message); }
        await SaveSettingsAsync();
        Status = onlyLooseFiles ? "Closed all tabs" : name.Length == 0 ? "Solution closed" : $"Closed {name}";
        return true;
    }

    // ------------------------------------------------------------------ Window > close tabs
    [RelayCommand] private Task CloseAllDocumentsAsync() => GuardAsync(() => CloseDocumentsAsync(Documents.ToArray()));
    [RelayCommand] private Task CloseOtherDocumentsAsync() => GuardAsync(() => CloseDocumentsAsync(Documents.Where(d => !ReferenceEquals(d, ActiveDocument)).ToArray()));
    /// <summary>Closes the tabs without unsaved edits, so the ones still open are the ones that need attention.</summary>
    [RelayCommand] private Task CloseSavedDocumentsAsync() => GuardAsync(() => CloseDocumentsAsync(Documents.Where(d => !d.State.IsDirty).ToArray()));
    [RelayCommand] private Task CloseDocumentsToRightAsync() => GuardAsync(() =>
    {
        var index = ActiveDocument == null ? -1 : Documents.IndexOf(ActiveDocument);
        return index < 0 ? Task.CompletedTask : CloseDocumentsAsync(Documents.Skip(index + 1).ToArray());
    });
    /// <summary>Closes tabs one at a time; Cancel on a save prompt keeps that tab and the ones after it open.</summary>
    private async Task CloseDocumentsAsync(IReadOnlyList<EditorViewModel> documents)
    {
        var closed = 0;
        foreach (var document in documents)
        {
            if (!Documents.Contains(document)) continue;
            if (!await CloseDocumentAsync(document)) break;
            closed++;
        }
        Status = closed == 1 ? "Closed 1 tab" : $"Closed {closed} tabs";
    }

    // ------------------------------------------------------------------ Help > About / What's new
    [ObservableProperty] public partial bool IsAboutOpen { get; set; }
    [ObservableProperty] public partial bool IsWhatsNewPage { get; set; }
    public string ProductVersion { get; } = typeof(ShellViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ShellViewModel).Assembly.GetName().Version?.ToString() ?? "development build";
    public string BuildCommit { get; } = typeof(ShellViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+') is [_, var commit, ..] ? commit[..Math.Min(commit.Length, 10)] : "";
    public string RuntimeDescription { get; } = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}";
    public string OperatingSystemDescription { get; } = RuntimeInformation.OSDescription;
    public string DataFolder => AppPaths.DataDirectory;
    public IReadOnlyList<ReleaseNote> ReleaseNotes => ReleaseNoteCatalog.All;
    /// <summary>Version, runtime and OS in one block for bug reports.</summary>
    public string AboutText => $"WpfStudio {ProductVersion}{(BuildCommit.Length > 0 ? " (" + BuildCommit + ")" : "")}\n{RuntimeDescription}\n{OperatingSystemDescription}\nSDK: {Workspace?.SdkVersion ?? "no workspace open"}";
    [RelayCommand] private void ShowAbout() { IsWhatsNewPage = false; OnPropertyChanged(nameof(AboutText)); IsAboutOpen = true; }
    [RelayCommand] private void ShowWhatsNew() { IsWhatsNewPage = true; IsAboutOpen = true; }
    [RelayCommand] private void CloseAbout() => IsAboutOpen = false;
    [RelayCommand] private void CopyAboutText() { WpfStudio.App.Services.DesktopNavigation.Copy(AboutText); Status = "Version information copied"; }

    private IEnumerable<PaletteEntry> WorkbenchCommands()
    {
        yield return Command("File", "Close solution", CloseSolutionCommand, icon: "Close", detail: "Close every tab and unload the solution");
        yield return Command("Window", "Close all tabs", CloseAllDocumentsCommand, icon: "Close");
        yield return Command("Window", "Close other tabs", CloseOtherDocumentsCommand, icon: "Close", detail: "Keep only the active tab");
        yield return Command("Window", "Close saved tabs", CloseSavedDocumentsCommand, icon: "Close", detail: "Keep tabs with unsaved edits");
        yield return Command("Window", "Close tabs to the right", CloseDocumentsToRightCommand, icon: "Close");
        yield return Command("View", Explorer.NestRelatedFiles ? "Stop nesting related files in Solution Explorer" : "Nest related files in Solution Explorer", Explorer.ToggleNestingCommand, icon: "Tree", detail: "Code-behind under XAML, partial files under their main file");
        yield return Command("View", "Task List", ShowToolCommand, "TaskList", icon: "Check", detail: "TODO, HACK and FIXME comments, and your own to-do items");
        yield return Command("View", "Refresh Task List", TaskList.RefreshCommand, icon: "Refresh");
        foreach (var entry in FileGroupCommands()) yield return entry;
        yield return Command("Help", "About WpfStudio", ShowAboutCommand, icon: "Info", detail: "Version, runtime and data folder");
        yield return Command("Help", "What's new", ShowWhatsNewCommand, icon: "Bulb", detail: "Recent features and fixes");
    }
}
