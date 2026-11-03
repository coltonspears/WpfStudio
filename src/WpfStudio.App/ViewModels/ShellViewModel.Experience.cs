using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Controls;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Services;

namespace WpfStudio.App.ViewModels;

/// <summary>
/// Shell experience: theme selection, workspace summary, related-file context, the unified
/// command palette and start-page state. Presentation-neutral; views bind to these members.
/// </summary>
public sealed partial class ShellViewModel
{
    private static readonly StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    public event Action? SearchFocusRequested;

    private void InitializeExperience()
    {
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));
    }

    // ------------------------------------------------------------------ Theme
    public bool IsLightTheme => ThemeName.Equals("Light", IgnoreCase);
    public bool IsDarkTheme => !IsLightTheme;
    partial void OnThemeNameChanged(string value) { OnPropertyChanged(nameof(IsLightTheme)); OnPropertyChanged(nameof(IsDarkTheme)); }
    [RelayCommand]
    private void SetTheme(string? name)
    {
        if (name is not ("Dark" or "Light") || name.Equals(ThemeName, IgnoreCase)) return;
        ThemeName = name; ThemeChanged?.Invoke(ThemeName);
    }

    // ------------------------------------------------------------------ Workspace summary
    public string WorkspaceName => Workspace == null ? "" : Path.GetFileNameWithoutExtension(Workspace.Path);
    public string WorkspaceSummary => Workspace == null ? "" : $"{Workspace.Projects.Count} project{(Workspace.Projects.Count == 1 ? "" : "s")} · .NET SDK {Workspace.SdkVersion}";
    public string CommandCenterText => Workspace == null ? "Search files and commands" : WorkspaceName;
    public bool HasDocuments => Documents.Count > 0;
    public bool IsColtonGptReady => Features.Assistant.Settings.HasApiKey;
    private void NotifyWorkspaceSummary()
    {
        OnPropertyChanged(nameof(WorkspaceName)); OnPropertyChanged(nameof(WorkspaceSummary)); OnPropertyChanged(nameof(CommandCenterText));
        RefreshAllRelated();
    }

    // ------------------------------------------------------------------ Start page
    public string? SamplePath { get; } = FindSample();
    public bool HasSample => SamplePath != null;
    [RelayCommand] private Task OpenSampleAsync() => SamplePath == null ? Task.CompletedTask : GuardAsync(() => LoadWorkspaceAsync(SamplePath));
    private static string? FindSample()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "CounterApp", "CounterApp.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    [RelayCommand] private void FindInWorkspace() { ToolRequested?.Invoke("Search"); SearchFocusRequested?.Invoke(); }
    [RelayCommand] private Task OpenDataFolderAsync() => GuardAsync(() => { Directory.CreateDirectory(DataFolder); DesktopNavigation.Reveal(DataFolder); return Task.CompletedTask; });
    /// <summary>Opens the command palette on the snippets for the active editor's language.</summary>
    [RelayCommand] private void InsertSnippet()
    {
        if (ActiveDocument is not { IsReadOnly: false } document || !WpfStudio.Core.Text.SnippetCatalog.For(document.State.Extension).Any())
        { Status = "Snippets are available in C# and XAML editors"; return; }
        Palette.OpenCommands("> Snippet: ");
    }
    [RelayCommand] private void ShowShortcuts() => Palette.OpenCommands();

    // ------------------------------------------------------------------ Related files
    /// <summary>Conventional pairs, in the order <see cref="SwitchRelatedCommand"/> cycles through them.</summary>
    private List<string> ConventionalRelated(string path) =>
        RelatedFileLocator.Conventional(path, _allFiles.Concat(Documents.Select(d => d.State.Path)), candidate => File.Exists(candidate) || _store.Find(candidate) != null);
    /// <summary>Views and view models linked through a declared DataContext, found in the WPF index.</summary>
    private List<string> DataContextRelated(string path) => RelatedFileLocator.DataContext(path, WpfExplorer.Index, candidate => _store.Find(candidate)?.Content);
    private void RefreshRelated(EditorViewModel document)
    {
        var path = document.State.Path;
        document.Location = RelatedFileLocator.LocationFor(path, WorkspaceRoot);
        var related = ConventionalRelated(path).Concat(DataContextRelated(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        if (document.RelatedFiles.Select(r => r.Path).SequenceEqual(related, StringComparer.OrdinalIgnoreCase)) return;
        document.RelatedFiles.Clear();
        foreach (var file in related) document.RelatedFiles.Add(new RelatedFile(RelatedFileLocator.RoleFor(path, file), file));
    }
    private void RefreshAllRelated() { foreach (var document in Documents) RefreshRelated(document); }
    private string WorkspaceRoot => Workspace == null ? "" : Path.GetDirectoryName(Path.GetFullPath(Workspace.Path)) ?? "";
    private string RelativeDirectory(string path) => RelatedFileLocator.RelativeDirectory(path, WorkspaceRoot);

    // ------------------------------------------------------------------ Command palette entries
    private PaletteEntry FileEntry(string path) => new(Path.GetFileName(path), RelativeDirectory(path), () => { RecordNavigationOrigin(); return OpenDocumentAsync(path); }, IconKind: FileIconKindConverter.KindFor(path));

    private PaletteEntry Command(string category, string label, ICommand command, object? parameter = null, string shortcut = "", string icon = "", string detail = "")
        => new(label, detail, async () =>
        {
            if (!command.CanExecute(parameter)) { Status = label + " isn't available right now"; return; }
            if (command is IAsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync(parameter);
            else command.Execute(parameter);
        }, category, shortcut, icon);

    /// <summary>A memory-profiler command: shows the profiler first so the result is visible.</summary>
    private PaletteEntry MemoryCommand(string label, ICommand command, object? parameter = null, string icon = "", string detail = "")
        => new(label, detail, async () =>
        {
            ShowToolCommand.Execute("Profiler");
            if (!command.CanExecute(parameter)) { Status = label + " isn't available right now"; return; }
            if (command is IAsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync(parameter);
            else command.Execute(parameter);
        }, "Memory", "", icon);

    /// <summary>Every shell command, grouped the same way as the menus.</summary>
    private IEnumerable<PaletteEntry> CommandEntries()
    {
        yield return Command("File", "Open solution or project…", OpenWorkspaceCommand, shortcut: "Ctrl+Shift+O", icon: "FolderOpen");
        yield return Command("File", "Open file…", OpenFileCommand, shortcut: "Ctrl+O", icon: "File");
        if (HasSample) yield return Command("File", "Open the CounterApp sample", OpenSampleCommand, icon: "Sample");
        yield return Command("File", "Save", SaveCommand, shortcut: "Ctrl+S", icon: "Save");
        yield return Command("File", "Save all", SaveAllCommand, shortcut: "Ctrl+Shift+S");
        yield return Command("File", "Reload document from disk", ReloadDocumentCommand);
        yield return Command("File", "Close document", CloseActiveCommand, shortcut: "Ctrl+W");
        yield return Command("Edit", "Find in workspace…", FindInWorkspaceCommand, icon: "Search");
        yield return Command("Edit", "Undo last workspace edit", UndoWorkspaceEditCommand);
        yield return Command("Code", "Go to definition", GoToDefinitionCommand, shortcut: "F12", icon: "Target");
        yield return Command("Code", "Go to implementation", GoToImplementationCommand, shortcut: "Ctrl+F12", icon: "Target", detail: "Implementations, overrides or derived types");
        yield return Command("Code", "Find references", FindReferencesCommand, shortcut: "Shift+F12", icon: "Search");
        yield return Command("Code", "Go to symbol…", GoToSymbolCommand, shortcut: "Ctrl+T", icon: "Braces", detail: "Search types and members across the solution");
        yield return Command("Code", "Navigate back", NavigateBackCommand, shortcut: "Ctrl+-", icon: "ArrowLeft");
        yield return Command("Code", "Navigate forward", NavigateForwardCommand, shortcut: "Ctrl+Shift+-", icon: "ArrowRight");
        yield return Command("Code", "Rename symbol…", RenameSymbolCommand, shortcut: "F2");
        yield return Command("Code", "Format document", FormatCommand, icon: "Braces");
        yield return Command("Code", "Organize usings", RefactorCommand, "OrganizeUsings", detail: "Preview import changes");
        yield return Command("Code", "Use var", RefactorCommand, "UseVar");
        yield return Command("Code", "Use explicit type", RefactorCommand, "UseExplicitType");
        yield return Command("WPF", "New WPF item…", NewWpfItemCommand, shortcut: "Ctrl+Shift+N", icon: "NewFile", detail: "View + view model, control, dictionary or converter");
        yield return Command("WPF", "Switch view ⇄ view model", SwitchRelatedCommand, shortcut: "F7", icon: "Swap");
        yield return Command("WPF", "Insert observable property…", InsertPropertyCommand, icon: "Plus");
        yield return Command("WPF", "Insert relay command…", InsertCommandCommand, icon: "Plus");
        yield return Command("Edit", "Insert snippet…", InsertSnippetCommand, icon: "Braces", detail: "MVVM, WPF and C# templates; or type a shortcut and press Tab");
        if (ActiveDocument is { IsReadOnly: false } editor)
            foreach (var snippet in WpfStudio.Core.Text.SnippetCatalog.For(editor.State.Extension))
                yield return new PaletteEntry("Snippet: " + snippet.Title, snippet.Shortcut + " ⇥" + (snippet.WrapsSelection ? " · wraps the selection" : ""),
                    () => { if (Documents.Contains(editor)) { ActiveDocument = editor; editor.InsertSnippet(snippet); } return Task.CompletedTask; }, "Snippets", Icon: "Braces");
        yield return Command("WPF", "Rename resource key…", RenameResourceCommand, icon: "Tag");
        yield return Command("WPF", "Refresh WPF index", RefreshWpfCommand, icon: "Refresh");
        yield return Command("WPF", "Import asset…", ImportAssetCommand, icon: "Image");
        yield return Command("WPF", "Show pack URI", ShowPackUriCommand, icon: "Link");
        yield return Command("Build", "Build", BuildCommand, shortcut: "Ctrl+Shift+B", icon: "Build");
        yield return Command("Build", "Rebuild", RebuildCommand);
        yield return Command("Build", "Clean", CleanCommand, icon: "Trash");
        yield return Command("Build", "Restore packages", RestoreCommand, icon: "Package");
        yield return Command("Build", "Run tests", TestCommand, icon: "Flask");
        yield return Command("Build", "Cancel running operation", CancelOperationCommand, icon: "Stop");
        yield return Command("Debug", "Start debugging", DebugCommand, shortcut: "F5", icon: "Play");
        yield return Command("Debug", "Run without debugging", RunCommand, shortcut: "Ctrl+F5", icon: "PlayOutline");
        yield return Command("Debug", "Stop debugging", Debugger.StopCommand, shortcut: "Shift+F5", icon: "Stop");
        yield return Command("Debug", "Toggle breakpoint", Breakpoints.ToggleCommand, shortcut: "F9");
        yield return Command("Debug", "Breakpoint hit count…", Breakpoints.HitCountCommand, icon: "Bug", detail: "Break on a hit number, from a hit, or every Nth hit");
        yield return Command("Debug", "Tracepoint log message…", Breakpoints.LogMessageCommand, icon: "Bug", detail: "Log a message with {expressions} and continue");
        yield return Command("Debug", "Add watch", Breakpoints.AddWatchCommand, icon: "Eye", detail: "Watch the expression at the caret or the selection");
        yield return Command("Debug", "Show breakpoints", Breakpoints.ShowCommand, icon: "Bug", detail: "Conditions, enabled state and binding status");
        yield return Command("Debug", "Install or reinstall the debugger", Debugger.InstallAdapterCommand, icon: "Bug", detail: "Download netcoredbg into your WpfStudio profile");
        yield return Command("Debug", "Attach to process…", ShowToolCommand, "Debugger", icon: "Plug");
        yield return Command("View", "Solution Explorer", ShowToolCommand, "Explorer", icon: "Folder");
        yield return Command("View", "WPF Explorer", ShowToolCommand, "WpfTools", icon: "Layers");
        yield return Command("View", "XAML Designer", OpenDesignerCommand, icon: "Layers");
        yield return Command("View", "Live XAML", ShowToolCommand, "LiveInspection", icon: "Layers");
        yield return Command("Debug", "Run with XAML inspection", RunWithInspectionCommand, icon: "PlayOutline");
        yield return Command("Debug", "Debug with XAML inspection", DebugWithInspectionCommand, icon: "Play");
        yield return Command("View", "ColtonGPT", OpenAssistantCommand, icon: "Chat", detail: "Ask about code with optional editor context");
        yield return Command("View", "Output", ShowToolCommand, "Output", icon: "Output");
        yield return Command("View", "Problems", ShowToolCommand, "Problems", icon: "Warning");
        yield return Command("View", "Search results", ShowToolCommand, "Search", icon: "Search");
        yield return Command("View", "Terminal", ShowToolCommand, "Terminal", shortcut: "Ctrl+`", icon: "Terminal");
        yield return Command("View", "Debugger", ShowToolCommand, "Debugger", icon: "Bug");
        yield return Command("View", "SQL Server", ShowToolCommand, "Database", icon: "Database");
        yield return Command("Tools", "Memory profiler", ShowToolCommand, "Profiler", icon: "Layers", detail: "Dumps, live snapshots, GC roots and reference-removal estimates");
        yield return MemoryCommand("Capture memory snapshot", MemoryProfiler.CaptureCommand, icon: "Camera", detail: "Snapshot the selected process's managed heap");
        yield return MemoryCommand("Open memory dump…", MemoryProfiler.OpenDumpCommand, icon: "FolderOpen");
        yield return MemoryCommand("Compare memory snapshots", MemoryProfiler.ShowViewCommand, "4", icon: "Timeline", detail: "Live memory, snapshot history and type growth");
        yield return MemoryCommand("Set memory baseline", MemoryProfiler.SetBaselineCommand, icon: "Baseline");
        yield return MemoryCommand("Memory: go to type or address", MemoryProfiler.FocusGoToCommand, icon: "Search");
        yield return MemoryCommand("Export memory report…", MemoryProfiler.ExportReportCommand, icon: "Export", detail: "HTML report or JSON data");
        yield return Command("View", ShowInlayHints ? "Hide inline hints" : "Show inline hints", ToggleInlayHintsCommand, icon: "Text", detail: "Parameter names for literal arguments and types of var declarations");
        yield return Command("View", ShowCodeLens ? "Hide reference counts (CodeLens)" : "Show reference counts (CodeLens)", ToggleCodeLensCommand, icon: "Search", detail: "C# references and XAML bindings above each declaration");
        yield return Command("View", ShowInlineBlame ? "Hide Git history above members" : "Show Git history above members", ToggleInlineBlameCommand, icon: "Git", detail: "Who last changed each C# type and member, with the commits behind it");
        yield return Command("View", "Collapse all regions", CollapseAllCommand, icon: "Collapse");
        yield return Command("View", "Expand all regions", ExpandAllCommand, icon: "Expand");
        yield return Command("View", "Dark theme", SetThemeCommand, "Dark", icon: "Moon");
        yield return Command("View", "Light theme", SetThemeCommand, "Light", icon: "Sun");
        yield return Command("View", "Save window layout", SaveLayoutCommand, icon: "Sidebar");
        yield return Command("View", "Reset window layout", ResetLayoutCommand, icon: "Sidebar");
        yield return Command("Tools", "NuGet packages", OpenPackagesCommand, icon: "Package", detail: "Search, install, update, remove");
        if (Features.Git is { HasRemote: true } git)
        {
            yield return Command("Tools", git.RemoteText, git.OpenRemoteCommand, icon: "Link", detail: git.Remote!.WebUrl);
            yield return Command("Tools", git.PullRequestText, git.CreatePullRequestCommand, icon: "Link", detail: "Opens " + git.Remote.HostName + " for the current branch");
        }
        yield return Command("Tools", "Git changes", OpenGitCommand, icon: "Git", detail: "Stage, commit, branches and history");
        yield return Command("Tools", "Settings", OpenSettingsCommand, shortcut: "Ctrl+,", icon: "Settings", detail: "Appearance, text editor, code intelligence, files, ColtonGPT and shortcuts");
        yield return Command("Tools", "Text editor settings", OpenSettingsCommand, EditorPage, icon: "Text", detail: "Font, tabs and indentation, display and editing");
        yield return Command("Tools", "Keyboard shortcuts", OpenSettingsCommand, ShortcutsPage, icon: "Keyboard", detail: "Every command with a shortcut");
        yield return Command("View", "Zoom in", Editor.ZoomInCommand, shortcut: "Ctrl+Mouse wheel", icon: "ZoomIn", detail: "A larger editor font");
        yield return Command("View", "Zoom out", Editor.ZoomOutCommand, icon: "ZoomOut", detail: "A smaller editor font");
        yield return Command("View", "Reset zoom", Editor.ResetZoomCommand, icon: "ActualSize", detail: "The default editor font size");
        yield return Command("Tools", "Reload workspace", ReloadWorkspaceCommand, icon: "Refresh");
        yield return Command("Tools", "Restart language service", RestartWorkspaceCommand, icon: "Restart");
        yield return Command("Help", "Open WpfStudio data folder", OpenDataFolderCommand, icon: "FolderOpen", detail: "Settings, layouts, logs and recovery files");
        foreach (var entry in WorkbenchCommands()) yield return entry;
    }
}
