using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.Core;

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
        Diagnostics.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ErrorCount)); OnPropertyChanged(nameof(WarningCount)); };
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));
        if (Features?.Assistant.Settings is INotifyPropertyChanged settings)
            settings.PropertyChanged += (_, e) => { if (e.PropertyName == "HasApiKey") OnPropertyChanged(nameof(IsColtonGptReady)); };
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
    public int ErrorCount => Diagnostics.Count(d => d.Severity.Equals("Error", IgnoreCase));
    public int WarningCount => Diagnostics.Count(d => d.Severity.Equals("Warning", IgnoreCase));
    public bool IsColtonGptReady => Features?.Assistant.Settings.HasApiKey == true;
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
    [RelayCommand] private Task OpenDataFolderAsync() => GuardAsync(() => { Directory.CreateDirectory(AppPaths.DataDirectory); DesktopNavigation.Reveal(AppPaths.DataDirectory); return Task.CompletedTask; });
    [RelayCommand] private void ShowShortcuts() { IsCommandPalette = true; PaletteQuery = ">"; IsPaletteOpen = true; RefreshPalette(); }

    // ------------------------------------------------------------------ Related files
    /// <summary>Conventional pairs, in the order <see cref="SwitchRelatedCommand"/> cycles through them.</summary>
    private List<string> ConventionalRelated(string path)
    {
        var files = _allFiles.Concat(Documents.Select(d => d.State.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new List<string>();
        void AddName(string name)
        {
            candidates.Add(Path.Combine(Path.GetDirectoryName(path)!, name));
            candidates.AddRange(files.Where(f => Path.GetFileName(f).Equals(name, IgnoreCase)));
        }
        if (path.EndsWith(".xaml.cs", IgnoreCase))
        {
            var view = Path.GetFileName(path)[..^8];
            AddName(view + "Model.cs"); AddName(view + "ViewModel.cs"); AddName(view + ".xaml");
        }
        else if (path.EndsWith(".xaml", IgnoreCase))
        {
            var view = Path.GetFileNameWithoutExtension(path);
            AddName(view + ".xaml.cs"); AddName(view + "Model.cs"); AddName(view + "ViewModel.cs");
        }
        else if (path.EndsWith("ViewModel.cs", IgnoreCase))
        {
            var stem = Path.GetFileName(path)[..^12];
            AddName(stem + ".xaml"); AddName(stem + "View.xaml");
        }
        return candidates.Where(p => !p.Equals(path, IgnoreCase) && (File.Exists(p) || _store.Find(p) != null)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Views and view models linked through a declared DataContext, found in the WPF index.</summary>
    private List<string> DataContextRelated(string path)
    {
        var results = new List<string>();
        if (_wpfIndex == null) return results;
        if (path.EndsWith(".xaml", IgnoreCase))
        {
            var text = _store.Find(path)?.Content ?? (_wpfIndex.Texts.TryGetValue(path, out var indexed) ? indexed : null);
            if (text != null && DataContextType(text) is { } type)
                results.AddRange(_wpfIndex.Items.Where(i => i.Path.EndsWith(".cs", IgnoreCase) && (i.Name == type || i.Name.EndsWith("." + type, StringComparison.Ordinal))).Select(i => i.Path));
        }
        else if (path.EndsWith(".cs", IgnoreCase) && _wpfIndex.Texts.TryGetValue(path, out var source))
        {
            var types = Regex.Matches(source, @"\b(?:class|record)\s+(\w+)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var (xamlPath, xaml) in _wpfIndex.Texts.Where(t => t.Key.EndsWith(".xaml", IgnoreCase)))
                if (DataContextType(xaml) is { } type && types.Contains(type)) results.Add(xamlPath);
        }
        return results.Where(p => !p.Equals(path, IgnoreCase) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
    }

    private static string? DataContextType(string xaml)
    {
        var match = Regex.Match(xaml, @"d:DataContext\s*=\s*[""']\{d:DesignInstance\s+(?:Type\s*=\s*)?(?:\w+:)?(?<type>\w+)");
        if (!match.Success) match = Regex.Match(xaml, @"<\w+\.DataContext\s*>\s*<(?:\w+:)?(?<type>\w+)\b");
        return match.Success ? match.Groups["type"].Value : null;
    }

    private static string RoleFor(string source, string related)
    {
        if (related.EndsWith(".xaml.cs", IgnoreCase)) return "Code-behind";
        if (related.EndsWith(".xaml", IgnoreCase)) return "View";
        if (related.EndsWith(".cs", IgnoreCase)) return source.EndsWith(".xaml.cs", IgnoreCase) || source.EndsWith(".xaml", IgnoreCase) ? "View model" : "Related";
        return "Related";
    }

    private void RefreshRelated(EditorViewModel document)
    {
        var path = document.State.Path;
        document.Location = LocationFor(path);
        var related = ConventionalRelated(path).Concat(DataContextRelated(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        if (document.RelatedFiles.Select(r => r.Path).SequenceEqual(related, StringComparer.OrdinalIgnoreCase)) return;
        document.RelatedFiles.Clear();
        foreach (var file in related) document.RelatedFiles.Add(new RelatedFile(RoleFor(path, file), file));
    }
    private void RefreshAllRelated() { foreach (var document in Documents) RefreshRelated(document); }

    private string WorkspaceRoot => Workspace == null ? "" : Path.GetDirectoryName(Path.GetFullPath(Workspace.Path)) ?? "";
    private string RelativeDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var root = WorkspaceRoot;
        if (root.Length > 0 && (directory.Equals(root, IgnoreCase) || directory.StartsWith(root + Path.DirectorySeparatorChar, IgnoreCase)))
        {
            var relative = Path.GetRelativePath(root, directory);
            return relative == "." ? Path.GetFileName(root) : Path.Combine(Path.GetFileName(root), relative);
        }
        return directory;
    }
    private string LocationFor(string path)
    {
        var parts = RelativeDirectory(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 4) parts = ["…", .. parts[^3..]];
        return string.Join("  ›  ", parts);
    }

    // ------------------------------------------------------------------ Command palette
    public string PalettePlaceholder => IsCommandPalette ? "Type a command name" : "Go to file by name — type > to run a command";
    partial void OnIsCommandPaletteChanged(bool value) => OnPropertyChanged(nameof(PalettePlaceholder));
    [RelayCommand] private void PaletteNext() => MovePalette(1);
    [RelayCommand] private void PalettePrevious() => MovePalette(-1);
    private void MovePalette(int delta)
    {
        if (PaletteResults.Count == 0) return;
        var index = SelectedPaletteEntry == null ? -1 : PaletteResults.IndexOf(SelectedPaletteEntry);
        SelectedPaletteEntry = PaletteResults[Math.Clamp(index + delta, 0, PaletteResults.Count - 1)];
    }

    private void RefreshPalette()
    {
        PaletteResults.Clear();
        var query = PaletteQuery.TrimStart();
        var commandMode = query.StartsWith('>');
        if (IsCommandPalette != commandMode) IsCommandPalette = commandMode;
        if (commandMode) query = query[1..].Trim();
        IEnumerable<PaletteEntry> results;
        if (commandMode)
        {
            var commands = CommandEntries().ToList();
            results = query.Length == 0 ? commands : commands
                .Select(entry => (entry, score: Best(FuzzyScore(entry.Label, query), FuzzyScore(entry.Category + " " + entry.Label, query) - 6)))
                .Where(x => x.score != null).OrderByDescending(x => x.score).Select(x => x.entry);
        }
        else
        {
            var files = Documents.Select(d => d.State.Path).Reverse().Concat(_allFiles).Distinct(StringComparer.OrdinalIgnoreCase);
            results = query.Length == 0 ? files.Select(FileEntry) : files
                .Select(path => (path, score: Best(FuzzyScore(Path.GetFileName(path), query), FuzzyScore(Path.GetRelativePath(WorkspaceRoot.Length > 0 ? WorkspaceRoot : Path.GetPathRoot(path) ?? "", path), query) - 20)))
                .Where(x => x.score != null).OrderByDescending(x => x.score).ThenBy(x => x.path.Length).Select(x => FileEntry(x.path));
        }
        foreach (var entry in results.Take(80)) PaletteResults.Add(entry);
        SelectedPaletteEntry = PaletteResults.FirstOrDefault();
    }

    private static int? Best(int? first, int? second) => first == null ? second : second == null ? first : Math.Max(first.Value, second.Value);
    private PaletteEntry FileEntry(string path) => new(Path.GetFileName(path), RelativeDirectory(path), () => OpenDocumentAsync(path), IconKind: FileIconKindConverter.KindFor(path));

    /// <summary>Case-insensitive subsequence match favouring prefixes, word starts and runs; null when absent.</summary>
    internal static int? FuzzyScore(string candidate, string query)
    {
        if (query.Length == 0) return 0;
        int score = 0, matched = 0, previous = -2;
        for (var i = 0; i < candidate.Length && matched < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[matched])) continue;
            var wordStart = i == 0 || !char.IsLetterOrDigit(candidate[i - 1]) || (char.IsUpper(candidate[i]) && char.IsLower(candidate[i - 1]));
            score += 1 + (wordStart ? 8 : 0) + (i == previous + 1 ? 5 : 0);
            previous = i; matched++;
        }
        if (matched < query.Length) return null;
        if (candidate.StartsWith(query, IgnoreCase)) score += 30;
        else if (candidate.Contains(query, IgnoreCase)) score += 14;
        return score - candidate.Length / 10;
    }

    private PaletteEntry Command(string category, string label, ICommand command, object? parameter = null, string shortcut = "", string icon = "", string detail = "")
        => new(label, detail, async () =>
        {
            if (!command.CanExecute(parameter)) { Status = label + " isn't available right now"; return; }
            if (command is IAsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync(parameter);
            else command.Execute(parameter);
        }, category, shortcut, icon);

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
        yield return Command("Code", "Find references", FindReferencesCommand, shortcut: "Shift+F12", icon: "Search");
        yield return Command("Code", "Rename symbol…", RenameSymbolCommand, shortcut: "F2");
        yield return Command("Code", "Format document", FormatCommand, icon: "Braces");
        yield return Command("Code", "Organize usings", RefactorCommand, "OrganizeUsings", detail: "Preview import changes");
        yield return Command("Code", "Use var", RefactorCommand, "UseVar");
        yield return Command("Code", "Use explicit type", RefactorCommand, "UseExplicitType");
        yield return Command("WPF", "New WPF item…", NewWpfItemCommand, shortcut: "Ctrl+Shift+N", icon: "NewFile", detail: "View + view model, control, dictionary or converter");
        yield return Command("WPF", "Switch view ⇄ view model", SwitchRelatedCommand, shortcut: "F7", icon: "Swap");
        yield return Command("WPF", "Insert observable property…", InsertPropertyCommand, icon: "Plus");
        yield return Command("WPF", "Insert relay command…", InsertCommandCommand, icon: "Plus");
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
        yield return Command("Debug", "Toggle breakpoint", ToggleBreakpointCommand, shortcut: "F9");
        yield return Command("Debug", "Show breakpoints", ShowBreakpointsCommand, icon: "Bug", detail: "Conditions, enabled state and binding status");
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
        yield return Command("View", "Dark theme", SetThemeCommand, "Dark", icon: "Moon");
        yield return Command("View", "Light theme", SetThemeCommand, "Light", icon: "Sun");
        yield return Command("View", "Save window layout", SaveLayoutCommand, icon: "Sidebar");
        yield return Command("View", "Reset window layout", ResetLayoutCommand, icon: "Sidebar");
        yield return Command("Tools", "NuGet packages", OpenPackagesCommand, icon: "Package", detail: "Search, install, update, remove");
        yield return Command("Tools", "Git changes", OpenGitCommand, icon: "Git", detail: "Stage, commit, branches and history");
        yield return Command("Tools", "Settings", OpenSettingsCommand, shortcut: "Ctrl+,", icon: "Settings", detail: "Appearance and ColtonGPT");
        yield return Command("Tools", "Reload workspace", ReloadWorkspaceCommand, icon: "Refresh");
        yield return Command("Tools", "Restart language service", RestartWorkspaceCommand, icon: "Restart");
        yield return Command("Help", "Open WpfStudio data folder", OpenDataFolderCommand, icon: "FolderOpen", detail: "Settings, layouts, logs and recovery files");
    }
}
