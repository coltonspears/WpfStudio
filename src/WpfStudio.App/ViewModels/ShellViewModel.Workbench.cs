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
    private (HashSet<string> Expanded, string? Selected)? _explorerStateBeforeFilter;

    private void InitializeWorkbench()
    {
        Explorer.CollectionChanged += (_, _) => NotifyExplorerPlaceholders();
        Documents.CollectionChanged += (_, _) => { if (ProblemScope == OpenFilesScope) RefreshVisibleProblems(); };
    }

    // ------------------------------------------------------------------ Close solution
    [RelayCommand] private Task CloseSolutionAsync() => GuardAsync(async () => { await CloseWorkspaceAsync(); });
    /// <summary>Closes every tab and unloads the solution, returning to the start page. False when the user cancels a save prompt.</summary>
    public async Task<bool> CloseWorkspaceAsync()
    {
        if (Workspace == null && Explorer.Count == 0) { Status = "No solution is open"; return true; }
        if (IsBusy || _loading) { Status = "Wait for the current workspace operation to finish"; return false; }
        if (IsPreviewOpen) { Status = "Finish or cancel the change preview before closing the solution"; return false; }
        if (!await CloseAllDocumentsForWorkspaceAsync()) return false;
        if (Debugger.IsActive) await Debugger.StopCommand.ExecuteAsync(null);
        if (_gitToolOpened && Features != null) await Features.Git.SetWorkspaceAsync(null);
        var name = Workspace == null ? "" : Path.GetFileName(Workspace.Path);
        _history.Clear();
        _allFiles = [];
        _explorerStateBeforeFilter = null; ExplorerFilter = "";
        Explorer.Clear(); Projects.Clear(); SearchResults.Clear(); WpfItems.Clear();
        _updatingChoices = true;
        TargetFrameworks.Clear(); LaunchProfiles.Clear(); TargetFramework = null; LaunchProfile = null;
        _updatingChoices = false;
        StartupProject = null;
        Workspace = null;
        RefreshEditorDiagnostics();
        await LoadUserTasksAsync(); await LoadFileGroupsAsync(); await ScanTaskCommentsAsync();
        try { await _workspace.UnloadAsync(_lifetime.Token); } catch (Exception ex) { AppendOutput("Language worker shutdown: " + ex.Message); }
        await SaveSettingsAsync();
        Status = name.Length == 0 ? "Solution closed" : $"Closed {name}";
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

    // ------------------------------------------------------------------ Problems filter
    /// <summary>The rows Problems shows: <see cref="Diagnostics"/> narrowed by severity, scope and search text.</summary>
    public ObservableCollection<WorkspaceDiagnostic> VisibleDiagnostics { get; } = [];
    public IReadOnlyList<string> ProblemScopes { get; } = [AllFilesScope, OpenFilesScope, CurrentFileScope];
    private const string AllFilesScope = "All files", OpenFilesScope = "Open files", CurrentFileScope = "Current file";
    [ObservableProperty] public partial string ProblemFilter { get; set; } = "";
    [ObservableProperty] public partial string ProblemScope { get; set; } = AllFilesScope;
    [ObservableProperty] public partial bool ShowErrorProblems { get; set; } = true;
    [ObservableProperty] public partial bool ShowWarningProblems { get; set; } = true;
    [ObservableProperty] public partial bool ShowMessageProblems { get; set; } = true;
    partial void OnProblemFilterChanged(string value) => RefreshVisibleProblems();
    partial void OnProblemScopeChanged(string value) => RefreshVisibleProblems();
    partial void OnShowErrorProblemsChanged(bool value) => RefreshVisibleProblems();
    partial void OnShowWarningProblemsChanged(bool value) => RefreshVisibleProblems();
    partial void OnShowMessageProblemsChanged(bool value) => RefreshVisibleProblems();
    public int MessageCount => Diagnostics.Count - ErrorCount - WarningCount;
    public bool IsProblemFilterActive => ProblemFilter.Trim().Length > 0 || ProblemScope != AllFilesScope || !ShowErrorProblems || !ShowWarningProblems || !ShowMessageProblems;
    /// <summary>"12 of 40 shown" while a filter hides rows; empty otherwise.</summary>
    public string ProblemFilterSummary => IsProblemFilterActive && Diagnostics.Count > 0 ? $"{VisibleDiagnostics.Count} of {Diagnostics.Count} shown" : "";
    [RelayCommand] private void ClearProblemFilter() { ProblemFilter = ""; ProblemScope = AllFilesScope; ShowErrorProblems = ShowWarningProblems = ShowMessageProblems = true; }

    private void RefreshVisibleProblems()
    {
        var selected = SelectedDiagnostic;
        var terms = ProblemFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var open = ProblemScope == OpenFilesScope ? Documents.Select(d => d.State.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var current = ProblemScope == CurrentFileScope ? ActiveDocument?.State.Path : null;
        VisibleDiagnostics.Clear();
        foreach (var diagnostic in Diagnostics)
        {
            if (!SeverityShown(diagnostic.Severity)) continue;
            if (open != null && (diagnostic.Path == null || !open.Contains(diagnostic.Path))) continue;
            if (ProblemScope == CurrentFileScope && (current == null || !string.Equals(diagnostic.Path, current, IgnoreCase))) continue;
            if (!terms.All(term => ProblemMatches(diagnostic, term))) continue;
            VisibleDiagnostics.Add(diagnostic);
        }
        if (selected != null && !VisibleDiagnostics.Contains(selected)) SelectedDiagnostic = null;
        OnPropertyChanged(nameof(MessageCount)); OnPropertyChanged(nameof(IsProblemFilterActive)); OnPropertyChanged(nameof(ProblemFilterSummary));
    }
    private bool SeverityShown(string severity) =>
        severity.Equals("Error", IgnoreCase) ? ShowErrorProblems : severity.Equals("Warning", IgnoreCase) ? ShowWarningProblems : ShowMessageProblems;
    /// <summary>A term matches the code, message, file name or project; <c>-term</c> excludes rows that match it.</summary>
    internal static bool ProblemMatches(WorkspaceDiagnostic diagnostic, string term)
    {
        if (term.Length > 1 && term[0] == '-') return !ProblemMatches(diagnostic, term[1..]);
        return diagnostic.Id.Contains(term, IgnoreCase) || diagnostic.Message.Contains(term, IgnoreCase)
            || (diagnostic.Path != null && Path.GetFileName(diagnostic.Path).Contains(term, IgnoreCase))
            || (diagnostic.ProjectName?.Contains(term, IgnoreCase) ?? false);
    }

    // ------------------------------------------------------------------ Solution Explorer filter and file nesting
    /// <summary>Shows only files whose name contains this text, with their folders expanded.</summary>
    [ObservableProperty] public partial string ExplorerFilter { get; set; } = "";
    /// <summary>Nests code-behind under its XAML (MainWindow.xaml.cs under MainWindow.xaml) and partial files under their main file.</summary>
    [ObservableProperty] public partial bool NestRelatedFiles { get; set; } = true;
    public bool IsExplorerFiltered => ExplorerFilter.Trim().Length > 0;
    /// <summary>The "No workspace open" prompt; a filter with no matches shows its own message instead.</summary>
    public bool ShowExplorerPlaceholder => Explorer.Count == 0 && !IsExplorerFiltered;
    public bool ShowExplorerNoMatches => Explorer.Count == 0 && IsExplorerFiltered;
    private void NotifyExplorerPlaceholders() { OnPropertyChanged(nameof(ShowExplorerPlaceholder)); OnPropertyChanged(nameof(ShowExplorerNoMatches)); }
    partial void OnExplorerFilterChanged(string value)
    {
        var filtering = value.Trim().Length > 0;
        if (filtering && _explorerStateBeforeFilter == null) _explorerStateBeforeFilter = ExplorerNode.CaptureState(Explorer);
        OnPropertyChanged(nameof(IsExplorerFiltered)); NotifyExplorerPlaceholders();
        if (Workspace == null) return;
        if (filtering) { RebuildExplorer(keepState: false); return; }
        var state = _explorerStateBeforeFilter; _explorerStateBeforeFilter = null;
        RebuildExplorer(keepState: false);
        if (state is { } previous) ExplorerNode.RestoreState(Explorer, previous);
    }
    partial void OnNestRelatedFilesChanged(bool value) { if (Workspace != null) RebuildExplorer(keepState: true); }
    [RelayCommand] private void ClearExplorerFilter() => ExplorerFilter = "";
    [RelayCommand] private void ToggleNestRelatedFiles() { NestRelatedFiles = !NestRelatedFiles; Status = NestRelatedFiles ? "Related files nested in Solution Explorer" : "Related files listed separately in Solution Explorer"; }

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
        yield return Command("View", NestRelatedFiles ? "Stop nesting related files in Solution Explorer" : "Nest related files in Solution Explorer", ToggleNestRelatedFilesCommand, icon: "Tree", detail: "Code-behind under XAML, partial files under their main file");
        yield return Command("View", "Task List", ShowToolCommand, "TaskList", icon: "Check", detail: "TODO, HACK and FIXME comments, and your own to-do items");
        yield return Command("View", "Refresh Task List", RefreshTaskListCommand, icon: "Refresh");
        foreach (var entry in FileGroupCommands()) yield return entry;
        yield return Command("Help", "About WpfStudio", ShowAboutCommand, icon: "Info", detail: "Version, runtime and data folder");
        yield return Command("Help", "What's new", ShowWhatsNewCommand, icon: "Bulb", detail: "Recent features and fixes");
    }
}
