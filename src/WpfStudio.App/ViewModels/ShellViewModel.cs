using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WpfStudio.App.Features.Documents;
using WpfStudio.App.Services;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Database.ViewModels;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DocumentStore _store;
    private readonly SettingsStore _settings;
    private readonly WorkspaceClient _workspace;
    private readonly BuildService _build;
    private readonly WpfIndexService _indexer;
    private readonly ScaffoldingService _scaffolding;
    private readonly WorkspaceEditTransaction _edits;
    private readonly VerifiedEditPipeline _verifiedEdits;
    private readonly XamlCompletionService _xaml;
    private readonly WpfStudio.Workspace.Xaml.XamlFormattingService _xamlFormatter;
    private readonly XamlResourceContext _xamlResources = new();
    private readonly IFileDialogService _files;
    private readonly IUserDialogService _dialogs;
    private readonly IDesktopService _desktop;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly StudioPaths _paths;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OperationGate _operations;
    private readonly List<WorkspaceDiagnostic> _buildDiagnostics = [];
    private string[] _allFiles = [];
    private bool _initialized;
    private bool _disposed;
    private bool _loading;
    private bool _contextReloadPending;
    private bool _updatingChoices;
    private int _launchChoicesGeneration;
    private Task? _recoveryLoop;

    public ShellViewModel(DocumentStore store, SettingsStore settings, ShellLanguageServices language, ShellDesktopServices desktop, ShellPanes panes, StudioFeatures features, EditorPreferences editor, ShellToolServices tools, IUiDispatcher dispatcher, IMessenger messenger, ILogger<ShellViewModel> logger, IOptions<StudioPaths> paths)
    {
        _operations = new(busy => IsBusy = busy, _lifetime.Token);
        _store = store; _settings = settings; _workspace = language.Workspace; _build = language.Build; _indexer = language.Indexer; _scaffolding = language.Scaffolding; _edits = language.Edits; _xaml = language.Xaml; _xamlFormatter = language.XamlFormatter;
        _files = desktop.Files; _dialogs = desktop.Dialogs; _desktop = desktop.Desktop; _clipboard = desktop.Clipboard; Theme = desktop.Theme;
        _dispatcher = dispatcher; Messenger = messenger; _logger = logger; _paths = paths.Value;
        Debugger = panes.Debugger; Terminal = panes.Terminal; Database = panes.Database; MemoryProfiler = panes.MemoryProfiler; Designer = panes.Designer; Features = features; Editor = editor;
        // System colours and syntax highlighting are not set until a theme is applied.
        Theme.Apply(ThemeName);
        Designer.AttachSourceHost(this);
        TaskList = new(this, tools.Tasks, messenger); FileGroups = new(this, tools.FileGroups, messenger); Problems = new(this, Diagnostics); ExternalChanges = new(this, store, dispatcher, tools.WatchOpenDocuments);
        Palette = new(this); ChangeReview = new(this, dispatcher); _verifiedEdits = new(this, store, _edits);
        XamlAnalysis = new(this, store, _workspace, dispatcher, logger, _lifetime.Token, tools.WatchSources);
        Explorer = new(this, Projects, _desktop, _clipboard); WpfExplorer = new(); AutoSave = new(this, store, Editor, dispatcher, messenger); Breakpoints = new(this, Debugger, messenger);
        InspectionSources = new(this, store); DesignerContext = new(Designer, this);
        LiveInspection = new(dispatcher);
        LiveInspection.AttachHost(this);
        features.Attach(this);
        InitializeExperience();
        InitializeWorkbench();
        InitializeSettings();
        RegisterMessages();
        _workspace.WorkerExited += WorkerExited;
    }
    /// <summary>The application's message bus, for views that reach the rest of the application through the shell.</summary>
    public IMessenger Messenger { get; }
    public IThemeService Theme { get; }
    public DebuggerViewModel Debugger { get; }
    public TerminalViewModel Terminal { get; }
    public DatabasePaneViewModel Database { get; }
    public Features.Profiling.MemoryProfilerViewModel MemoryProfiler { get; }
    public ObservableCollection<EditorViewModel> Documents { get; } = [];
    public ObservableCollection<WorkspaceProject> Projects { get; } = [];
    public ObservableCollection<string> RecentWorkspaces { get; } = [];
    public ObservableCollection<NavigationResult> SearchResults { get; } = [];
    public ObservableCollection<WorkspaceDiagnostic> Diagnostics { get; } = [];
    public IReadOnlyList<ScaffoldKind> ScaffoldKinds { get; } = [ScaffoldKind.ViewAndViewModel, ScaffoldKind.UserControl, ScaffoldKind.ViewModel, ScaffoldKind.ResourceDictionary, ScaffoldKind.Converter];
    public ObservableCollection<string> Configurations { get; } = ["Debug", "Release"];
    public string[] BuildActions { get; } = ["Resource", "Content", "None", "Page"];
    public ObservableCollection<string> TargetFrameworks { get; } = [];
    public ObservableCollection<string> LaunchProfiles { get; } = [];
    public event Action<string>? ToolRequested;
    public event Action<string>? WorkbenchCloseRequested;
    public event Action? LayoutResetRequested;
    public event Action? LayoutSaveRequested;
    public event Action? LayoutRestoreRequested;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasWorkspace))] public partial WorkspaceSnapshot? Workspace { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial EditorViewModel? ActiveDocument { get; set; }
    [ObservableProperty] public partial string? ActiveWorkbench { get; set; }
    [ObservableProperty] public partial WorkspaceProject? StartupProject { get; set; }
    [ObservableProperty] public partial NavigationResult? SelectedSearchResult { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Ready";
    [ObservableProperty] public partial string Output { get; set; } = "";
    /// <summary>True while a build, run, launch, load or reload holds the gate. A reload holds it too, so this alone answers "may another one start".</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelOperationCommand), nameof(BuildCommand), nameof(RestoreCommand), nameof(RebuildCommand), nameof(CleanCommand), nameof(RunCommand), nameof(TestCommand),
        nameof(BuildSelectedCommand), nameof(RebuildSelectedCommand), nameof(CleanSelectedCommand), nameof(RestoreSelectedCommand), nameof(DebugCommand), nameof(RunWithInspectionCommand), nameof(DebugWithInspectionCommand), nameof(ReloadWorkspaceCommand))]
    public partial bool IsBusy { get; private set; }
    private bool CanRunWorkspaceOperation() => !IsBusy;
    [ObservableProperty] public partial string Configuration { get; set; } = "Debug";
    [ObservableProperty] public partial string? TargetFramework { get; set; }
    [ObservableProperty] public partial string? LaunchProfile { get; set; }
    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    [ObservableProperty] public partial bool IsScaffoldOpen { get; set; }
    partial void OnIsScaffoldOpenChanged(bool value) => UpdateDesignerOverlay();
    private void UpdateDesignerOverlay() => Designer.IsOverlayOpen = Palette.IsOpen || IsScaffoldOpen || IsPreviewOpen || IsSettingsOpen;
    [ObservableProperty] public partial ScaffoldKind SelectedScaffoldKind { get; set; } = ScaffoldKind.ViewAndViewModel;
    [ObservableProperty] public partial string ScaffoldName { get; set; } = "CustomerView";
    [ObservableProperty] public partial bool AddDataTemplate { get; set; }
    [ObservableProperty] public partial string SelectedBuildAction { get; set; } = "Resource";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLightTheme), nameof(IsDarkTheme))] public partial string ThemeName { get; set; } = "Dark";
    /// <summary>Inline parameter-name and type hints in C# editors.</summary>
    [ObservableProperty] public partial bool ShowInlayHints { get; set; } = true;
    partial void OnShowInlayHintsChanged(bool value) { foreach (var document in Documents) document.ShowInlayHints = value; }
    /// <summary>Who last changed the caret's line, shown after the line in editors of committed files.</summary>
    [ObservableProperty] public partial bool ShowInlineBlame { get; set; } = true;
    partial void OnShowInlineBlameChanged(bool value) { foreach (var document in Documents) document.ShowInlineBlame = value; }
    /// <summary>C# and XAML reference counts after declarations.</summary>
    [ObservableProperty] public partial bool ShowCodeLens { get; set; } = true;
    partial void OnShowCodeLensChanged(bool value) { foreach (var document in Documents) document.ShowCodeLens = value; }
    [RelayCommand] private void ToggleCodeLens() { ShowCodeLens = !ShowCodeLens; Status = ShowCodeLens ? "Reference counts shown" : "Reference counts hidden"; }
    [RelayCommand] private void ToggleInlineBlame() { ShowInlineBlame = !ShowInlineBlame; Status = ShowInlineBlame ? "Git history shown above C# members" : "Git history above members hidden"; }
    [RelayCommand] private void ToggleInlayHints() { ShowInlayHints = !ShowInlayHints; Status = ShowInlayHints ? "Inline hints shown" : "Inline hints hidden"; }
    [RelayCommand] private void CollapseAll() => ActiveDocument?.SetAllFolded(true);
    [RelayCommand] private void ExpandAll() => ActiveDocument?.SetAllFolded(false);
    public string WindowTitle => Workspace == null ? "WpfStudio" : $"{Path.GetFileNameWithoutExtension(Workspace.Path)} — WpfStudio";
    public bool HasWorkspace => Workspace != null;
    public bool IsWelcomeVisible => Documents.Count == 0;
    partial void OnWorkspaceChanged(WorkspaceSnapshot? value) { Messenger.Send(new WorkspaceChangedMessage(value)); NotifyWorkspaceSummary(); XamlAnalysis.Reset(value); }
    partial void OnStartupProjectChanged(WorkspaceProject? value) { if (value != null) _ = GuardAsync(RefreshLaunchChoicesAsync); }
    partial void OnConfigurationChanged(string value) { if (!string.IsNullOrWhiteSpace(value)) ScheduleContextReload(); }
    partial void OnTargetFrameworkChanged(string? value) { if (value != null) ScheduleContextReload(); }
    private void ScheduleContextReload()
    {
        if (!_initialized || Workspace == null || _updatingChoices) return;
        ReloadWhenFreeAsync().Forget(ReportBackgroundFault);
    }
    /// <summary>Reloads now, or when the operation holding the gate ends.</summary>
    private Task ReloadWhenFreeAsync()
    {
        if (Workspace == null || _disposed) return Task.CompletedTask;
        _contextReloadPending = true;
        return ExecuteWhenAvailableAsync(ReloadWorkspaceCommand);
    }
    /// <summary>For internal callers: ExecuteAsync skips CanExecute, and on a running cancellable command it cancels that execution.</summary>
    private static Task ExecuteWhenAvailableAsync(IAsyncRelayCommand command) => command.CanExecute(null) ? command.ExecuteAsync(null) : Task.CompletedTask;
    /// <summary>Frees the gate, then runs the reload that was requested while it was held.</summary>
    private void EndOperation(OperationGate.Lease lease)
    {
        lease.Dispose();
        if (_contextReloadPending) ReloadWhenFreeAsync().Forget(ReportBackgroundFault);
    }

    public async Task InitializeAsync(string[] arguments)
    {
        var assistant = Features.Assistant.InitializeAsync();
        var settings = await _settings.LoadAsync();
        await assistant;
        ThemeName = settings.Theme;
        ShowInlayHints = settings.ShowInlayHints;
        ShowInlineBlame = settings.ShowInlineBlame;
        ShowCodeLens = settings.ShowCodeLens;
        ExternalChanges.AutoReloadUnchangedFiles = settings.AutoReloadUnchangedFiles;
        ShowQuickInfo = settings.ShowQuickInfo;
        Explorer.NestRelatedFiles = settings.NestRelatedFiles;
        Editor.Load(settings.Editor);
        Configuration = settings.Configuration;
        foreach (var recent in settings.RecentWorkspaces.Where(File.Exists)) RecentWorkspaces.Add(recent);
        _initialized = true; AutoSave.IsEnabled = true;
        var requested = arguments.FirstOrDefault(a => File.Exists(a) && Path.GetExtension(a).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj");
        var restoring = requested == null && settings.WorkspacePath != null && File.Exists(settings.WorkspacePath);
        // Read the previous session's files while the language worker loads; tabs still open in their saved order afterwards.
        var prefetched = restoring ? settings.OpenDocuments.Where(File.Exists).Select(PrefetchDocumentAsync).ToArray() : [];
        if (requested != null) await LoadWorkspaceAsync(requested);
        else if (restoring) await LoadWorkspaceAsync(settings.WorkspacePath!);
        if (Workspace != null && requested == null)
        {
            StartupProject = Projects.FirstOrDefault(p => p.ProjectPath == settings.StartupProject) ?? StartupProject;
            foreach (var path in settings.OpenDocuments.Where(File.Exists)) await GuardAsync(() => OpenDocumentAsync(path));
        }
        foreach (var state in await Task.WhenAll(prefetched))
            if (state != null && !Documents.Any(d => ReferenceEquals(d.State, state))) _store.Close(state);
        var recovered = await _store.ReadRecoveryAsync();
        if (recovered.Count > 0 && await _dialogs.ConfirmAsync("Recover unsaved work", $"Recover {recovered.Count} unsaved document(s) from the previous session?"))
        {
            foreach (var item in recovered)
                await GuardAsync(async () => { var state = File.Exists(item.Path) ? await _store.OpenAsync(item.Path) : _store.Create(item.Path); state.Content = item.Content; AddDocument(state); });
        }
        else foreach (var item in recovered) await _store.DeleteRecoveryAsync(item.Path);
        LayoutRestoreRequested?.Invoke();
        _recoveryLoop = RecoveryLoopAsync(_lifetime.Token);
    }
    private async Task<DocumentState?> PrefetchDocumentAsync(string path)
    {
        // Failures are reported when the tab itself is opened.
        try { return await Task.Run(() => _store.OpenAsync(path, _lifetime.Token)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { return null; }
    }
    private async Task RecoveryLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try { while (await timer.WaitForNextTickAsync(token)) await _store.WriteRecoveryAsync(token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Recovery persistence failed"); _dispatcher.Post(() => AppendOutput("Recovery: " + ex.Message)); }
    }
    [RelayCommand] private Task OpenWorkspaceAsync() => GuardAsync(async () => { var path = await _files.OpenFileAsync("Open solution or project", "Solutions and projects|*.sln;*.slnx;*.csproj"); if (path != null) await LoadWorkspaceAsync(path); });
    [RelayCommand] private Task OpenRecentAsync(string? path) => path == null ? Task.CompletedTask : GuardAsync(() => LoadWorkspaceAsync(path));
    [RelayCommand] private Task OpenFileAsync() => GuardAsync(async () => { var path = await _files.OpenFileAsync("Open file", "Source files|*.cs;*.xaml;*.sql;*.json;*.xml;*.props;*.targets;*.csproj;*.md;*.txt|All files|*.*"); if (path != null) await OpenDocumentAsync(path); });
    public async Task LoadWorkspaceAsync(string path)
    {
        if (IsBusy) { Status = "Wait for the current workspace operation to finish"; return; }
        if (IsPreviewOpen) { Status = "Finish or cancel the change preview before switching workspaces"; return; }
        if (!await CloseAllDocumentsForWorkspaceAsync()) return;
        if (Debugger.IsActive) await Debugger.StopCommand.ExecuteAsync(null);
        if (_gitToolOpened) await Features.Git.SetWorkspaceAsync(null);
        // The awaits above let another operation start.
        if (_operations.TryEnter() is not { } lease) { Status = "Wait for the current workspace operation to finish"; return; }
        _loading = true;
        var token = lease.Token;
        try
        {
            Status = "Loading " + Path.GetFileName(path) + "…";
            _history.Clear();
            Explorer.Clear();
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Terminal.WorkingDirectory = directory;
            TargetFramework = null;
            // Start the worker first: listing files overlaps its process start and SDK resolution, and the tree appears before language services finish.
            // The project model saved by the last session answers at once; MSBuild refreshes it in the background below.
            var loading = _workspace.LoadAsync(new(path, Configuration, UseCache: true), token);
            try { _allFiles = await Task.Run(() => EnumerateSourceFiles(directory).ToArray(), token); }
            catch (IOException ex) { _allFiles = []; AppendOutput("File listing: " + ex.Message); }
            Explorer.ShowFiles(directory, _allFiles);
            Status = "Loading language services for " + Path.GetFileName(path) + "…";
            Workspace = await loading;
            RefreshConfigurations();
            Projects.Clear(); foreach (var project in Workspace.Projects) Projects.Add(project);
            StartupProject = Projects.FirstOrDefault(p => p.IsExecutable) ?? Projects.FirstOrDefault();
            _allFiles = _allFiles.Concat(Workspace.Projects.SelectMany(p => p.Files).Where(f => !f.IsGenerated).Select(f => f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Explorer.Rebuild(keepState: false);
            if (Explorer.Nodes.Count == 0 && !Explorer.IsFiltered) Explorer.ShowFiles(directory, _allFiles);
            foreach (var issue in Workspace.Issues) AppendOutput(issue.Severity + ": " + issue.Message);
            RecentWorkspaces.Remove(path); RecentWorkspaces.Insert(0, path); while (RecentWorkspaces.Count > 10) RecentWorkspaces.RemoveAt(10);
            await Debugger.SetWorkspaceAsync(path);
            if (_gitToolOpened) await Features.Git.SetWorkspaceAsync(path);
            await RefreshWpfAsync();
            await TaskList.LoadUserTasksAsync(); await FileGroups.LoadAsync();
            TaskList.RefreshCommand.ExecuteAsync(null).Forget(ReportBackgroundFault);
            ShowWorkspaceStatus(Workspace, refreshingModel: Workspace.Cached);
            await SaveSettingsAsync();
        }
        finally { _loading = false; lease.Dispose(); XamlAnalysis.Request(); if (_contextReloadPending) ReloadWhenFreeAsync().Forget(ReportBackgroundFault); }
        if (Workspace is { Cached: true } cached && !_contextReloadPending) RefreshSavedProjectModelAsync(cached.Path, Configuration).Forget(ReportBackgroundFault);
    }
    // Any other status written meanwhile is newer than the refresh's result and stays.
    private bool _statusShowsModelRefresh;
    partial void OnStatusChanged(string value) => _statusShowsModelRefresh = false;
    private void ShowWorkspaceStatus(WorkspaceSnapshot snapshot, bool refreshingModel)
    {
        Status = $"{Projects.Count} projects · SDK {snapshot.SdkVersion}" + (refreshingModel ? " · refreshing project model…" : "");
        _statusShowsModelRefresh = refreshingModel;
    }
    /// <summary>
    /// Closes every tab before the workspace changes, asking about each unsaved document first. Nothing is closed when any
    /// answer is Cancel or a save fails.
    /// </summary>
    private async Task<bool> CloseAllDocumentsForWorkspaceAsync()
    {
        if (Documents.Count == 0) return true;
        // Until every tab is closed, automatic saving must not write edits that are about to be discarded.
        using var holdAutoSave = await AutoSave.SuspendAsync();
        var closing = Documents.ToArray();
        var discard = new List<EditorViewModel>();
        foreach (var doc in closing.Where(d => d.State.IsDirty))
        {
            var decision = await _dialogs.AskSaveAsync(doc.State.Name);
            if (decision == SaveDecision.Cancel) return false;
            if (decision == SaveDecision.Save && !await SaveDocumentAsync(doc)) return false;
            if (decision == SaveDecision.Discard) discard.Add(doc);
        }
        // Do not discard anything until all close decisions have succeeded.
        foreach (var doc in closing)
        {
            if (discard.Contains(doc)) await _store.DeleteRecoveryAsync(doc.State.Path);
            if (Designer.SourcePath == doc.State.Path) await Designer.CloseAsync();
            doc.Dispose(); Documents.Remove(doc); _store.Close(doc.State); XamlAnalysis.UntrackDocument(doc.State);
        }
        ActiveDocument = null; OnPropertyChanged(nameof(IsWelcomeVisible));
        return true;
    }
    // A cached start is replaced by a full MSBuild load while the editor stays usable.
    private async Task RefreshSavedProjectModelAsync(string path, string configuration)
    {
        try
        {
            var snapshot = await _workspace.RefreshAsync(new(path, configuration), _lifetime.Token);
            // A workspace opened or reloaded meanwhile restarted the worker and supersedes this result.
            if (_loading || Workspace?.Path != snapshot.Path || Configuration != configuration) return;
            Workspace = snapshot;
            ApplyWorkspaceProjects(StartupProject?.ProjectPath);
            foreach (var issue in snapshot.Issues) AppendOutput(issue.Severity + ": " + issue.Message);
            foreach (var doc in Documents) await doc.SyncAsync(_lifetime.Token);
            await RefreshWpfAsync();
            if (_statusShowsModelRefresh) ShowWorkspaceStatus(snapshot, refreshingModel: false);
            XamlAnalysis.Request();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppendOutput("Project model refresh: " + exception.Message); }
    }
    private void ApplyWorkspaceProjects(string? startupPath)
    {
        if (Workspace is null) return;
        RefreshConfigurations();
        Projects.Clear(); foreach (var project in Workspace.Projects) Projects.Add(project);
        StartupProject = Projects.FirstOrDefault(p => p.ProjectPath == startupPath) ?? Projects.FirstOrDefault(p => p.IsExecutable) ?? Projects.FirstOrDefault();
        _allFiles = _allFiles.Concat(Workspace.Projects.SelectMany(p => p.Files).Where(f => !f.IsGenerated).Select(f => f.Path)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Explorer.Rebuild(keepState: true);
    }
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            string[] files; string[] directories;
            try { files = Directory.GetFiles(directory); directories = Directory.GetDirectories(directory); } catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in directories)
                if (Path.GetFileName(child) is not ("bin" or "obj" or ".git" or ".vs" or "node_modules" or "artifacts") && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
        }
    }
    [RelayCommand] private Task RestartWorkspaceAsync() => GuardAsync(async () => { Workspace = await _workspace.RestartAsync(_lifetime.Token); Status = "Language service restarted"; });
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task ReloadWorkspaceAsync(CancellationToken cancel) => GuardAsync(async () =>
    {
        if (Workspace == null) return;
        _contextReloadPending = true;
        // The operation holding the gate reloads when it ends.
        if (_operations.TryEnter(cancel) is not { } lease) return;
        try { await ReloadProjectModelAsync(lease.Token); }
        finally { lease.Dispose(); }
    });
    /// <summary>The caller holds the gate. Only the load can be cancelled: the steps after it are short and leave the editors consistent with the new model.</summary>
    private async Task ReloadProjectModelAsync(CancellationToken token)
    {
        _loading = true;
        try
        {
          do
          {
            _contextReloadPending = false;
            var requestedConfiguration = Configuration;
            var requestedFramework = TargetFramework;
            Status = "Updating project context…";
            var startupPath = StartupProject?.ProjectPath;
            Workspace = await _workspace.LoadAsync(new(Workspace!.Path, requestedConfiguration, Path.GetExtension(Workspace.Path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? requestedFramework : null), token);
            ApplyWorkspaceProjects(startupPath);
            foreach (var doc in Documents) await doc.SyncAsync(_lifetime.Token);
            await RefreshWpfAsync(); Status = "Workspace refreshed";
            if (requestedConfiguration != Configuration || requestedFramework != TargetFramework) _contextReloadPending = true;
          } while (_contextReloadPending && !token.IsCancellationRequested);
        }
        // A load stops the old worker before starting the new one, so nothing keeps serving the previous model.
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { Status = "Reload cancelled. Language services are off until you reload the workspace."; }
        finally { _loading = false; XamlAnalysis.Request(); }
    }
    private async Task RefreshLaunchChoicesAsync()
    {
        if (StartupProject == null) return;
        var path = StartupProject.ProjectPath;
        var configuration = Configuration;
        var generation = ++_launchChoicesGeneration;
        var targets = await _build.DiscoverLaunchTargetsAsync(path, configuration: configuration);
        if (generation != _launchChoicesGeneration || StartupProject?.ProjectPath != path || Configuration != configuration) return;
        var previousFramework = TargetFramework; var previousProfile = LaunchProfile;
        _updatingChoices = true;
        TargetFrameworks.Clear(); LaunchProfiles.Clear();
        var target = targets.FirstOrDefault();
        if (target != null) { foreach (var framework in target.TargetFrameworks) TargetFrameworks.Add(framework); foreach (var profile in target.Profiles) LaunchProfiles.Add(profile.Name); }
        TargetFramework = previousFramework != null && TargetFrameworks.Contains(previousFramework) ? previousFramework : TargetFrameworks.FirstOrDefault() ?? StartupProject.TargetFramework;
        LaunchProfile = previousProfile != null && LaunchProfiles.Contains(previousProfile) ? previousProfile : LaunchProfiles.FirstOrDefault();
        _updatingChoices = false;
    }
    private void RefreshConfigurations()
    {
        var current = Configuration;
        var configurations = (Workspace?.Configurations ?? ["Debug", "Release"]).Append(current).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _updatingChoices = true;
        Configurations.Clear(); foreach (var configuration in configurations) Configurations.Add(configuration);
        Configuration = current;
        _updatingChoices = false;
    }
    [RelayCommand] private Task OpenNodeAsync(ExplorerNode? node) => node switch
    {
        { Kind: ExplorerNodeKind.Dependency } => GuardAsync(() => OpenDependencyAsync(node)),
        null or { IsFolder: true } => Task.CompletedTask,
        _ => GuardAsync(() => OpenDocumentAsync(node.Path))
    };
    /// <summary>A package opens the package manager for its project and a project reference opens that project file. Assemblies and frameworks have no source to open.</summary>
    private async Task OpenDependencyAsync(ExplorerNode node)
    {
        if (node.Dependency?.Kind == "Package") await OpenProjectPackagesAsync(node.ProjectPath);
        else if (node.Dependency?.Kind == "Project" && File.Exists(node.Path)) await OpenDocumentAsync(node.Path);
        else Status = node.ToolTip;
    }
    public async Task OpenDocumentAsync(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".ico" or ".ttf" or ".otf" or ".zip")
        { Status = "Manage binary assets through WPF Explorer; this file is not editable source text"; return; }
        var doc = _store.Find(path) ?? await _store.OpenAsync(path);
        AddDocument(doc);
    }
    private void AddDocument(DocumentState state)
    {
        var existing = Documents.FirstOrDefault(d => d.State.Path.Equals(state.Path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActiveDocument = existing; return; }
        var vm = new EditorViewModel(state, _workspace, _xaml, _dispatcher, AppendOutput, _xamlResources)
        {
            Preferences = Editor,
            DataTipEvaluator = (expression, token) => Debugger.IsStopped ? Debugger.EvaluateDataTipAsync(expression, token) : Task.FromResult<string?>(null),
            XamlCodeActionApplier = ApplyXamlCodeActionAsync,
            CSharpCodeActionApplier = (editor, action) => GuardAsync(() => ApplyCSharpCodeActionAsync(editor, action)),
            ShowInlayHints = ShowInlayHints,
            ShowInlineBlame = ShowInlineBlame,
            ShowCodeLens = ShowCodeLens,
            ShowQuickInfo = ShowQuickInfo
        };
        state.ContentChanged += (_, _) => AutoSave.Schedule();
        vm.CodeLensRequested += (entry, position) => _ = GuardAsync(async () =>
        {
            ActiveDocument = vm;
            vm.Navigate(Math.Min(position, vm.State.Content.Length));
            await FindReferencesCommand.ExecuteAsync(null);
        });
        vm.RefreshGitAsync(Features.GitService, _lifetime.Token).Forget(vm.ReportFault);
        vm.BlameCommitRequested += line => _ = GuardAsync(async () => { await OpenGitCommand.ExecuteAsync(null); Features.Git.ShowCommit(line, vm.GitBase?.RelativePath); });
        ConfigureXamlContext(vm);
        vm.BreakpointRequested += line => _ = GuardAsync(() => Debugger.ToggleBreakpointAsync(state.Path, line));
        vm.ActionRequested += action => _ = GuardAsync(() => HandleEditorActionAsync(vm, action));
        vm.ContextChanged += () =>
        {
            if (ReferenceEquals(vm, ActiveDocument))
            {
                UpdateAssistantContext();
                if (!_navigatingBindingSource && !_revealingDesignerSelection) Designer.SelectSource(vm.State.Path, vm.State.CaretOffset);
            }
        };
        vm.Diagnostics.CollectionChanged += (_, _) => RefreshEditorDiagnostics();
        vm.OpenRequested += path => _ = GuardAsync(() => OpenDocumentAsync(path));
        RefreshRelated(vm);
        Documents.Add(vm); ActiveDocument = vm; Breakpoints.RefreshMarkers();
        XamlAnalysis.TrackDocument(state);
        OnPropertyChanged(nameof(IsWelcomeVisible));
    }
    public async Task<bool> CloseDocumentAsync(EditorViewModel document, bool discardChanges = false)
    {
        using var holdAutoSave = await AutoSave.SuspendAsync();
        if (discardChanges) await _store.DeleteRecoveryAsync(document.State.Path);
        else if (document.State.IsDirty)
        {
            var decision = await _dialogs.AskSaveAsync(document.State.Name);
            if (decision == SaveDecision.Cancel) return false;
            if (decision == SaveDecision.Save && !await SaveDocumentAsync(document)) return false;
            if (decision == SaveDecision.Discard) await _store.DeleteRecoveryAsync(document.State.Path);
        }
        if (Designer.SourcePath == document.State.Path) await Designer.CloseAsync();
        document.Dispose(); Documents.Remove(document); _store.Close(document.State);
        XamlAnalysis.UntrackDocument(document.State);
        // Queue the worker close before awaiting index refresh. A reopened
        // editor must synchronize after this old buffer has been retired.
        if (document.IsCSharp || document.IsXaml)
            try { await _workspace.CloseDocumentAsync(document.State.Path); } catch (Exception ex) { AppendOutput("Language document close: " + ex.Message); }
        if (document.State.Extension == ".xaml" && WpfExplorer.Index?.Texts.TryGetValue(document.State.Path, out var indexedText) == true && indexedText != document.State.Content)
            await RefreshWpfAsync(); // Reconcile saved/discarded text before exposing closed-file index issues again.
        else RefreshEditorDiagnostics();
        if (ReferenceEquals(ActiveDocument, document)) ActiveDocument = Documents.LastOrDefault();
        UpdateAssistantContext(); OnPropertyChanged(nameof(IsWelcomeVisible));
        return true;
    }
    [RelayCommand] private Task CloseActiveAsync() => GuardAsync(async () => { if (ActiveWorkbench is { } workbench) WorkbenchCloseRequested?.Invoke(workbench); else if (ActiveDocument != null) await CloseDocumentAsync(ActiveDocument); });
    private bool CanSaveActiveDocument() => ActiveDocument != null;
    [RelayCommand(CanExecute = nameof(CanSaveActiveDocument))] private Task SaveAsync() => GuardAsync(async () => { if (ActiveDocument != null) await SaveDocumentAsync(ActiveDocument); });
    [RelayCommand] private Task SaveAllAsync() => GuardAsync(async () => await SaveAllDocumentsAsync());
    private async Task<bool> SaveDocumentAsync(EditorViewModel doc)
    {
        if (doc.IsReadOnly) { Status = "Generated source snapshots are read-only"; return !doc.State.IsDirty; }
        try
        {
            TidyBeforeSave(doc);
            await _store.SaveAsync(doc.State);
            Messenger.Send(new DocumentSavedMessage(doc.State));
            XamlAnalysis.Request();
            Status = doc.State.IsDirty ? "The document changed while saving. Save again to include the latest edits." : "Saved " + doc.State.Name;
            return !doc.State.IsDirty;
        }
        catch (ExternalFileChangedException)
        {
            if (!await _dialogs.ConfirmAsync("File changed outside WpfStudio", $"Overwrite the external changes to {doc.State.Name} with the editor content?")) return false;
            await _store.SaveAsync(doc.State, true);
            Messenger.Send(new DocumentSavedMessage(doc.State));
            XamlAnalysis.Request();
            if (doc.State.IsDirty) Status = "The document changed while saving. Save again to include the latest edits.";
            return !doc.State.IsDirty;
        }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Save failed", ex.Message); return false; }
    }
    private async Task<bool> SaveAllDocumentsAsync()
    {
        foreach (var doc in Documents.Where(d => d.State.IsDirty || d.State.DiskHash == null).ToArray()) if (!await SaveDocumentAsync(doc)) return false;
        if (Documents.Any(d => d.State.IsDirty)) { Status = "Documents changed while saving. Save again before building or debugging."; return false; }
        return true;
    }
    [RelayCommand] private Task ReloadDocumentAsync() => GuardAsync(async () => { if (ActiveDocument == null) return; if (ActiveDocument.State.IsDirty && !await _dialogs.ConfirmAsync("Reload document", "Discard editor changes and reload this file from disk?")) return; await _store.ReloadAsync(ActiveDocument.State); ExternalChanges.Forget(ActiveDocument); });
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task BuildAsync(CancellationToken cancel) => RunBuildAsync(BuildOperation.Build, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task RestoreAsync(CancellationToken cancel) => RunBuildAsync(BuildOperation.Restore, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task RebuildAsync(CancellationToken cancel) => RunBuildAsync(BuildOperation.Rebuild, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task CleanAsync(CancellationToken cancel) => RunBuildAsync(BuildOperation.Clean, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task RunAsync(CancellationToken cancel) => RunBuildAsync(BuildOperation.Run, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task TestAsync(CancellationToken cancel) => RunBuildAsync(BuildOperation.Test, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task BuildSelectedAsync(CancellationToken cancel) => RunSelectedBuildAsync(BuildOperation.Build, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task RebuildSelectedAsync(CancellationToken cancel) => RunSelectedBuildAsync(BuildOperation.Rebuild, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task CleanSelectedAsync(CancellationToken cancel) => RunSelectedBuildAsync(BuildOperation.Clean, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task RestoreSelectedAsync(CancellationToken cancel) => RunSelectedBuildAsync(BuildOperation.Restore, cancel);
    /// <summary>The toolbar's one Stop button: it cancels whichever operation holds the gate, including a workspace load, which no command started.</summary>
    [RelayCommand(CanExecute = nameof(IsBusy))] private void CancelOperation() { _operations.Cancel(); Status = "Cancelling…"; }
    /// <summary>Builds the solution or project chosen in Solution Explorer instead of the whole workspace.</summary>
    private Task RunSelectedBuildAsync(BuildOperation operation, CancellationToken cancel)
    {
        if (Explorer.SelectedNode is { CanBuild: true } node) return RunBuildAsync(operation, cancel, node.Path);
        Status = "Select a solution or project in Solution Explorer";
        return Task.CompletedTask;
    }
    /// <summary>The opened solution, for a project built from it.</summary>
    private string? SolutionPathFor(string path) => Workspace is { } workspace && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        && !workspace.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? workspace.Path : null;
    private Task RunBuildAsync(BuildOperation operation, CancellationToken cancel, string? target = null) => GuardAsync(async () =>
    {
        if (Workspace == null || _operations.TryEnter(cancel) is not { } lease) return;
        ToolRequested?.Invoke(Workbenches.Output); Output = "";
        try
        {
            if (!await SaveAllDocumentsAsync()) return;
            var path = target ?? (operation == BuildOperation.Run ? StartupProject?.ProjectPath ?? Workspace.Path : Workspace.Path);
            var label = target == null ? operation.ToString() : $"{operation} {Path.GetFileNameWithoutExtension(path)}";
            Status = label + "…";
            // The selected framework belongs to the startup project; another project builds each of its frameworks.
            var framework = Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                && (target == null || path.Equals(StartupProject?.ProjectPath, StringComparison.OrdinalIgnoreCase)) ? TargetFramework : null;
            using var progress = CreateBuildProgress();
            var result = await _build.RunAsync(new(path, operation, Configuration, framework, LaunchProfile, SolutionPath: SolutionPathFor(path)), progress, lease.Token);
            progress.Flush();
            Status = result.Cancelled ? "Cancelled" : result.ExitCode == 0 ? label + " succeeded" : label + $" failed (exit {result.ExitCode})";
            if (result.ExitCode == 0 && operation is BuildOperation.Build or BuildOperation.Rebuild or BuildOperation.Restore) await ReloadProjectModelAsync(lease.Token);
        }
        finally { EndOperation(lease); }
    });
    // Continue is not a workspace operation, so F5 keeps working while, say, a build runs beside a paused session.
    private bool CanDebug() => !IsBusy || Debugger.IsStopped;
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanDebug))] private Task DebugAsync(CancellationToken cancel) => GuardAsync(async () =>
    {
        if (Debugger.IsStopped) { await Debugger.ContinueCommand.ExecuteAsync(null); return; }
        if (Debugger.IsActive) { Status = "The debugger is already running"; return; }
        if (IsBusy || StartupProject == null) return;
        if (ProjectDiscovery.TargetsNetFramework(StartupProject, TargetFramework))
        {
            // netcoredbg debugs .NET (Core) processes only; the .NET Framework runtime needs Visual Studio's debugger.
            if (await _dialogs.ConfirmAsync("Run without debugging", $"{StartupProject.Name} targets .NET Framework. The bundled debugger supports .NET 5 and later only. Run it without debugging instead?"))
                await ExecuteWhenAvailableAsync(RunCommand);
            else Status = "Debugging .NET Framework applications isn't supported. Use Run without debugging (Ctrl+F5).";
            return;
        }
        if (!await EnsureDebuggerAsync()) return;
        // The install prompt keeps the dispatcher running, so a build may have started behind it.
        if (_operations.TryEnter(cancel) is not { } lease) { Status = "Wait for the current workspace operation to finish"; return; }
        ToolRequested?.Invoke(Workbenches.Debugger);
        try
        {
            if (!await SaveAllDocumentsAsync()) return;
            var project = StartupProject;
            var configuration = Configuration;
            var framework = TargetFramework;
            var profile = LaunchProfile;
            Status = "Building for debug…";
            using var progress = CreateBuildProgress();
            var result = await _build.RunAsync(new(project.ProjectPath, BuildOperation.Build, configuration, framework, SolutionPath: SolutionPathFor(project.ProjectPath)), progress, lease.Token);
            progress.Flush();
            if (result.ExitCode != 0 || result.Cancelled) { Status = "Debug launch cancelled: build did not succeed"; return; }
            var launch = await _build.ResolveLaunchAsync(project.ProjectPath, configuration, framework, profile, lease.Token);
            await Debugger.LaunchAsync(new DebugLaunchConfiguration(launch.Program, launch.WorkingDirectory, launch.Arguments, launch.Environment), lease.Token);
            Status = "Debugging " + project.Name;
        }
        finally { EndOperation(lease); }
    });
    /// <summary>Offers to install the debug adapter when it is missing; false when debugging cannot start.</summary>
    private async Task<bool> EnsureDebuggerAsync()
    {
        if (!Debugger.IsAdapterMissing) return true;
        ToolRequested?.Invoke(Workbenches.Debugger);
        Status = "Installing the debugger…";
        var ready = await Debugger.EnsureAdapterAsync(() => _dialogs.ConfirmAsync("Install the debugger",
            $"Debugging needs {Debugger.AdapterDescription}, which isn't installed. Download it now (about 5 MB from GitHub), verify its checksum and install it in your WpfStudio profile?"));
        Status = ready ? "Debugger installed" : "Debugging needs the debugger. Choose Install debugger in the Debugger pane.";
        return ready;
    }
    private BufferedBuildProgress CreateBuildProgress()
    {
        _buildDiagnostics.Clear(); RefreshEditorDiagnostics();
        return new(_dispatcher, batch =>
        {
            AppendOutput(string.Join(Environment.NewLine, batch.Select(e => e.Text)));
            foreach (var entry in batch.Where(e => e.Diagnostic != null)) { if (_buildDiagnostics.Count >= 2000) _buildDiagnostics.RemoveAt(0); _buildDiagnostics.Add(entry.Diagnostic!); }
            RefreshEditorDiagnostics();
        });
    }
    private void RefreshEditorDiagnostics()
    {
        Diagnostics.Clear(); foreach (var item in CurrentEditorDiagnostics().Concat(XamlAnalysis.CurrentProjectDiagnostics()).Concat(CurrentWpfIndexDiagnostics()).Concat(_buildDiagnostics).Distinct()) Diagnostics.Add(item);
        Problems.Refresh();
        RefreshWpfIssues();
    }
    private IEnumerable<WorkspaceDiagnostic> CurrentEditorDiagnostics(bool xamlOnly = false) => Documents
        .Where(editor => (!xamlOnly || editor.IsXaml) && (XamlAnalysis.XamlContextsAvailable || !editor.IsXaml))
        .Where(editor => !editor.IsXaml || !XamlAnalysis.CoversEditor(editor))
        .SelectMany(editor => editor.Diagnostics);
    private IEnumerable<WorkspaceDiagnostic> CurrentWpfIndexDiagnostics() => (WpfExplorer.Index?.Diagnostics ?? []).Where(issue =>
        !XamlAnalysis.IsIndexDiagnosticStale(issue) && (issue.Path is null || _store.Find(issue.Path) is not { } document ||
         (WpfExplorer.Index!.Texts.TryGetValue(issue.Path, out var indexed) && indexed == document.Content)) &&
        (issue.Id != "XAML001" || !Documents.Any(editor => editor.State.Path.Equals(issue.Path, StringComparison.OrdinalIgnoreCase) &&
            editor.Diagnostics.Any(diagnostic => diagnostic.Id == "XAMLSYNTAX001"))));
    private void RefreshWpfIssues()
    {
        WpfExplorer.SetIssues(CurrentWpfIndexDiagnostics().Concat(XamlAnalysis.CurrentProjectDiagnostics()).Concat(CurrentEditorDiagnostics(xamlOnly: true)).Distinct());
    }
    [RelayCommand] private Task GoToDefinitionAsync() => GuardAsync(async () =>
    {
        var document = ActiveDocument;
        if (document == null) return;
        if (document.State.Extension == ".xaml")
        {
            var current = document.State;
            var version = current.Version;
            var contextRevision = document.XamlContextRevision;
            int caret = current.CaretOffset;
            if (_workspace.IsConnected)
            {
                try
                {
                    var definitions = await document.XamlDefinitionAsync(caret);
                    if (document != ActiveDocument || current.Version != version || current.CaretOffset != caret || document.XamlContextRevision != contextRevision) return;
                    if (definitions.Count > 0)
                    {
                        await NavigateAsync(definitions[0].Path, definitions[0].Line, definitions[0].Column);
                        return;
                    }
                }
                catch (Exception ex) { AppendOutput("XAML definition: " + ex.Message); }
            }
            if (document != ActiveDocument || current.Version != version || current.CaretOffset != caret || document.XamlContextRevision != contextRevision) return;
            // The resource index is usable offline only while its source still matches this buffer.
            var usage = WpfExplorer.Index?.Texts.TryGetValue(current.Path, out var indexed) == true && indexed == current.Content
                ? WpfExplorer.Index.Usages.FirstOrDefault(u => u.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase) && caret >= u.Start && caret <= u.Start + u.Length) : null;
            var declaration = usage?.ResolvedPath == null ? null : WpfExplorer.Index!.Resources.FirstOrDefault(r => r.Path == usage.ResolvedPath && r.ValueStart == usage.ResolvedDeclarationStart);
            if (declaration != null) await NavigateAsync(declaration.Path, declaration.Line, 1); else Status = "This XAML reference could not be resolved statically";
            return;
        }
        await document.SyncAsync(_lifetime.Token); var state = document.State;
        var results = await _workspace.GetDefinitionAsync(new(state.Path, state.CaretOffset, state.Version), _lifetime.Token);
        if (results.Count > 0) await NavigateAsync(results[0].Path, results[0].Line, results[0].Column); else Status = "No source definition found";
    });
    [RelayCommand] private Task FindReferencesAsync() => GuardAsync(FindLanguageSymbolReferencesAsync);
    [RelayCommand] private Task FormatAsync() => GuardAsync(FormatActiveDocumentAsync);
    [RelayCommand] private Task RenameSymbolAsync() => GuardAsync(RenameLanguageSymbolAsync);
    [RelayCommand] private Task UndoWorkspaceEditAsync() => GuardAsync(async () =>
    {
        foreach (var path in await _edits.UndoAsync())
        {
            var document = Documents.FirstOrDefault(d => d.State.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (document != null) { document.Dispose(); Documents.Remove(document); }
            await _workspace.CloseDocumentAsync(path);
        }
        if (ActiveDocument != null && !Documents.Contains(ActiveDocument)) ActiveDocument = Documents.LastOrDefault();
        OnPropertyChanged(nameof(IsWelcomeVisible)); Status = "Workspace edit undone";
        if (_workspace.IsConnected)
        {
            try
            {
                // Undo restores all buffers synchronously. Publish only this
                // completed state, including XAML and unchanged prerequisites.
                var result = await _workspace.ReconcileNameProjectionsAsync(OpenLanguageBuffers(), _lifetime.Token);
                if (!result.Accepted) throw new InvalidOperationException(result.Status ?? "The restored editor buffers could not be synchronized.");
                if (!string.IsNullOrWhiteSpace(result.Status))
                {
                    AppendOutput("Undo language-service refresh: " + result.Status);
                    Status += SymbolWarnings([result.Status]);
                }
            }
            catch (Exception exception)
            {
                Status = "Workspace edit undone; language-service refresh is pending.";
                AppendOutput("Workspace edit undone; language-service refresh: " + exception.Message);
            }
        }
    });
    [RelayCommand(IncludeCancelCommand = true)] private async Task SearchAsync(CancellationToken token)
    {
        await GuardAsync(async () =>
        {
            var query = SearchQuery; if (string.IsNullOrWhiteSpace(query)) return;
            SearchResults.Clear(); ToolRequested?.Invoke(Workbenches.Search);
            var results = await Task.Run(async () =>
            {
                var found = new List<NavigationResult>();
                foreach (var path in _allFiles.Where(p => Path.GetExtension(p).ToLowerInvariant() is ".cs" or ".xaml" or ".json" or ".xml" or ".sql" or ".csproj" or ".props" or ".targets" or ".md"))
                {
                    token.ThrowIfCancellationRequested();
                    string text; try { text = _store.Find(path)?.Content ?? await File.ReadAllTextAsync(path, token); } catch (IOException) { continue; }
                    var lines = text.Split('\n'); var offset = 0;
                    for (var i = 0; i < lines.Length; i++) { var at = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase); if (at >= 0) found.Add(new(path, i + 1, at + 1, lines[i].Trim(), offset + at)); offset += lines[i].Length + 1; if (found.Count >= 2000) return found; }
                }
                return found;
            }, token);
            foreach (var result in results) SearchResults.Add(result); Status = $"{results.Count} search result(s)" + (results.Count >= 2000 ? " · limit reached" : "");
        });
    }
    [RelayCommand] private Task NavigateSearchAsync(NavigationResult? result) => result == null ? Task.CompletedTask : GuardAsync(() => NavigateSymbolResultAsync(result));
    [RelayCommand] private Task NavigateDiagnosticAsync(WorkspaceDiagnostic? diagnostic) => diagnostic?.Path == null ? Task.CompletedTask : GuardAsync(async () =>
    {
        if (!await XamlAnalysis.VerifyDiagnosticAsync(diagnostic)) { Status = "The diagnostic source changed. Refresh project XAML analysis before navigating."; XamlAnalysis.Request(); return; }
        await OpenDocumentAsync(diagnostic.Path);
        if (ActiveDocument is not { } editor || !XamlAnalysis.IsOpenedDiagnosticCurrent(diagnostic, editor.State))
        { Status = "The diagnostic source changed while opening it. Refresh project XAML analysis before navigating."; return; }
        SelectXamlDiagnosticContext(editor, diagnostic);
        await NavigateAsync(diagnostic.Path, diagnostic.Line, diagnostic.Column);
    });
    [RelayCommand] private Task NavigateWpfAsync(WpfItem? item) => item == null ? Task.CompletedTask : GuardAsync(() => NavigateAsync(item.Path, item.Line, 1));
    public async Task NavigateAsync(string path, int line, int column, bool execution = false)
    {
        if (!execution) RecordNavigationOrigin();
        await OpenDocumentAsync(path);
        if (ActiveDocument is not { } doc || !doc.State.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        var text = doc.State.Content; var offset = 0;
        for (var i = 1; i < line; i++) { var next = text.IndexOf('\n', offset); if (next < 0) break; offset = next + 1; }
        doc.Navigate(Math.Min(text.Length, offset + Math.Max(0, column - 1)));
        if (execution) { foreach (var other in Documents) other.ExecutionLine = -1; doc.ExecutionLine = line; }
    }
    [RelayCommand] private Task RefreshWpfAsync() => GuardAsync(async () =>
    {
        if (Workspace == null) return;
        var workspace = Workspace;
        var revision = XamlAnalysis.IndexRevision;
        var buffers = _store.Documents.ToDictionary(d => d.Path, d => d.Content, StringComparer.OrdinalIgnoreCase);
        var index = await Task.Run(() => _indexer.IndexAsync(workspace, buffers, _lifetime.Token));
        if (!ReferenceEquals(workspace, Workspace) || revision != XamlAnalysis.IndexRevision) return;
        WpfExplorer.SetIndex(index); _xaml.Index = index;
        XamlAnalysis.MarkIndexRefreshed();
        RefreshEditorDiagnostics(); RefreshAllRelated();
        XamlAnalysis.Schedule();
    });
    [RelayCommand] private Task RenameResourceAsync() => GuardAsync(async () =>
    {
        if (WpfExplorer.Index == null || WpfExplorer.SelectedItem?.Key == null) { Status = "Select a keyed resource in WPF Explorer"; return; }
        var item = WpfExplorer.SelectedItem; var declaration = WpfExplorer.Index.Resources.First(r => r.Path == item.Path && r.Key == item.Key && r.Line == item.Line);
        var name = await _dialogs.PromptAsync("Rename resource", "New resource key:", declaration.Key); if (name == null) return;
        var changes = _indexer.RenameResource(WpfExplorer.Index, declaration, name);
        if (await PreviewAsync("Rename resource", changes, "Only references resolved to this declaration are changed. Code-generated keys and unresolved runtime references require review.")) { await ApplyChangesAsync(changes); await RefreshWpfAsync(); }
    });
    [RelayCommand] private Task SwitchRelatedAsync() => GuardAsync(async () =>
    {
        if (ActiveDocument == null) return;
        var path = ActiveDocument.State.Path;
        // Conventional names first (view → code-behind → view model), then declared DataContext links.
        var next = ConventionalRelated(path).FirstOrDefault() ?? DataContextRelated(path).FirstOrDefault();
        if (next != null) await OpenDocumentAsync(next); else Status = "No paired view, code-behind, or view model found";
    });
    [RelayCommand] private void NewWpfItem() { if (StartupProject == null) { Status = "Open a project first"; return; } IsScaffoldOpen = true; }
    [RelayCommand] private void CancelScaffold() => IsScaffoldOpen = false;
    [RelayCommand] private Task CreateWpfItemAsync() => GuardAsync(async () =>
    {
        if (StartupProject == null) return;
        var target = Projects.FirstOrDefault(p => p.ProjectPath == Explorer.SelectedNode?.ProjectPath) ?? StartupProject;
        var root = Path.GetDirectoryName(target.ProjectPath)!;
        var directory = Explorer.SelectedNode is { IsFolder: true } && Directory.Exists(Explorer.SelectedNode.Path) && (Explorer.SelectedNode.Path.Equals(root, StringComparison.OrdinalIgnoreCase) || Explorer.SelectedNode.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ? Explorer.SelectedNode.Path : root;
        var changes = await _scaffolding.CreateAsync(target.ProjectPath, directory, ScaffoldName, SelectedScaffoldKind, AddDataTemplate);
        IsScaffoldOpen = false;
        if (await PreviewAsync("Create WPF files", changes, AddDataTemplate ? "Merge the generated Templates.xaml dictionary into application resources to activate the view mapping." : "")) { await ApplyChangesAsync(changes); Status = "Created in editor buffers. Save All to write the files."; }
    });
    [RelayCommand] private Task InsertPropertyAsync() => GuardAsync(async () =>
    {
        var document = ActiveDocument;
        if (document == null || document.IsReadOnly || document.State.Extension != ".cs") return;
        var name = await _dialogs.PromptAsync("Observable property", "Property name:", "Title"); if (string.IsNullOrWhiteSpace(name)) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new ArgumentException("Enter a valid property name.");
        var owners = Projects.Where(p => p.Files.Any(f => f.Path.Equals(document.State.Path, StringComparison.OrdinalIgnoreCase))).ToArray();
        var owner = owners.Length == 1 ? owners[0] : null;
        var context = owner == null ? null : await ScaffoldProjectContext.TryEvaluateAsync(owner.ProjectPath, _lifetime.Token);
        var state = document.State; var template = ScaffoldTemplates.Property(name, context?.SupportsPartialProperties == true).Replace("[ObservableProperty]", "[CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]", StringComparison.Ordinal);
        var changes = new[] { new FileChange(state.Path, state.Content, state.Content.Insert(state.CaretOffset, template), "Insert an observable property") };
        if (await PreviewAsync("Insert observable property", changes, "Place inside a view model derived from ObservableObject. Partial properties require a partial class and the CommunityToolkit.Mvvm namespace.")) await ApplyChangesAsync(changes);
    });
    [RelayCommand] private Task InsertCommandAsync() => GuardAsync(async () =>
    {
        var document = ActiveDocument;
        if (document == null || document.IsReadOnly || document.State.Extension != ".cs") return;
        var name = await _dialogs.PromptAsync("Relay command", "Command method name:", "Refresh"); if (string.IsNullOrWhiteSpace(name)) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new ArgumentException("Enter a valid method name.");
        var state = document.State; var snippet = $"    [CommunityToolkit.Mvvm.Input.RelayCommand]\n    private async System.Threading.Tasks.Task {name}Async(System.Threading.CancellationToken cancellationToken)\n    {{\n        await System.Threading.Tasks.Task.CompletedTask;\n    }}\n";
        var changes = new[] { new FileChange(state.Path, state.Content, state.Content.Insert(state.CaretOffset, snippet), "Insert a generated relay command") };
        if (await PreviewAsync("Insert relay command", changes, "Place inside a partial view model. Add the command's implementation after generation.")) await ApplyChangesAsync(changes);
    });
    [RelayCommand] private Task ImportAssetAsync() => GuardAsync(async () =>
    {
        if (StartupProject == null) return;
        var targetProject = Projects.FirstOrDefault(p => p.ProjectPath == Explorer.SelectedNode?.ProjectPath) ?? StartupProject;
        var source = await _files.OpenFileAsync("Import resource asset", "Assets|*.png;*.jpg;*.jpeg;*.ico;*.svg;*.ttf;*.otf|All files|*.*"); if (source == null) return;
        var destination = Path.Combine(Path.GetDirectoryName(targetProject.ProjectPath)!, "Assets", Path.GetFileName(source));
        if (File.Exists(destination)) throw new IOException("An asset with that name already exists.");
        var project = await _store.OpenAsync(targetProject.ProjectPath);
        var change = ProjectFileEditor.SetBuildAction(project.Path, project.Content, destination, "Resource");
        if (!await PreviewAsync("Import asset", [change], $"Copy {source} to {destination}")) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, false);
        try { await ApplyChangesAsync([change]); }
        catch { File.Delete(destination); throw; }
        Status = ProjectFileEditor.PackUri(project.Path, destination, targetProject.AssemblyName);
    });
    [RelayCommand] private Task SetBuildActionAsync() => GuardAsync(async () =>
    {
        var path = WpfExplorer.SelectedItem?.Path ?? Explorer.SelectedNode?.Path; if (path == null) return;
        var owner = ResolveAssetOwner(path);
        var project = await _store.OpenAsync(owner.ProjectPath);
        var change = ProjectFileEditor.SetBuildAction(project.Path, project.Content, path, SelectedBuildAction);
        if (await PreviewAsync("Change build action", [change], "")) await ApplyChangesAsync([change]);
    });
    private WorkspaceProject ResolveAssetOwner(string path)
    {
        var owners = Projects.Where(p => p.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (Explorer.SelectedNode?.Path == path && owners.FirstOrDefault(p => p.ProjectPath == Explorer.SelectedNode.ProjectPath) is { } selected) return selected;
        if (owners.Length == 1) return owners[0];
        throw new InvalidOperationException(owners.Length == 0 ? "This file is not an evaluated project item. Refresh the workspace first." : "This linked asset belongs to multiple projects. Select it under the intended project in Solution Explorer.");
    }
    [RelayCommand] private Task ShowPackUriAsync() => GuardAsync(() =>
    {
        var path = WpfExplorer.SelectedItem?.Path ?? Explorer.SelectedNode?.Path;
        if (path != null) { var owner = ResolveAssetOwner(path); var logicalPath = owner.Files.First(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).LogicalPath; var uri = ProjectFileEditor.PackUri(owner.ProjectPath, path, owner.AssemblyName, logicalPath); AppendOutput(uri); ToolRequested?.Invoke(Workbenches.Output); Status = uri; }
        return Task.CompletedTask;
    });
    private Task<bool> PreviewAsync(string title, IReadOnlyList<FileChange> changes, string warnings) => ChangeReview.ShowAsync(title, changes, warnings);
    private async Task ApplyChangesAsync(IReadOnlyList<FileChange> changes) { await _edits.ApplyAsync(changes); foreach (var change in changes) AddDocument(_store.Find(change.Path)!); }
    /// <summary>Prepares, reviews and applies a worker edit result: the shared tail of the edit commands that need no further verification.</summary>
    private async Task<bool> ReviewAndApplyAsync(string title, WorkspaceEditResult result)
    {
        var changes = await _edits.PrepareAsync(result);
        if (!await PreviewAsync(title, changes, string.Join(Environment.NewLine, result.Warnings))) return false;
        await ApplyChangesAsync(changes);
        return true;
    }
    [RelayCommand] private void QuickOpen() => Palette.OpenFiles();
    [RelayCommand] private void CommandPalette() => Palette.OpenCommands();
    [RelayCommand] private void ToggleTheme() => ThemeName = ThemeName == "Dark" ? "Light" : "Dark";
    [RelayCommand] private void ShowTool(string? name)
    {
        if (name == Workbenches.Packages) OpenPackagesCommand.ExecuteAsync(null).Forget(ReportBackgroundFault);
        else if (name == Workbenches.Git) OpenGitCommand.ExecuteAsync(null).Forget(ReportBackgroundFault);
        else if (name == Workbenches.Assistant) OpenAssistantCommand.ExecuteAsync(null).Forget(ReportBackgroundFault);
        else if (name == Workbenches.Designer) OpenDesignerCommand.ExecuteAsync(null).Forget(ReportBackgroundFault);
        else if (name != null) ToolRequested?.Invoke(name);
    }
    [RelayCommand] private void SaveLayout() { LayoutSaveRequested?.Invoke(); Status = "Layout saved"; }
    [RelayCommand] private void ResetLayout() => LayoutResetRequested?.Invoke();
    [RelayCommand] private void ClearOutput() => Output = "";
    public void AppendOutput(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        Output += line + Environment.NewLine;
        if (Output.Length > 250_000) Output = Output[^200_000..];
    }
    public async Task<bool> TryCloseAsync()
    {
        if (IsPreviewOpen) { Status = "Apply or cancel the open change preview before closing"; return false; }
        using var holdAutoSave = await AutoSave.SuspendAsync();
        if (!await Database.ConfirmCloseAllAsync()) return false;
        foreach (var doc in Documents.Where(d => d.State.IsDirty).ToArray())
        {
            var decision = await _dialogs.AskSaveAsync(doc.State.Name);
            if (decision == SaveDecision.Cancel) return false;
            if (decision == SaveDecision.Save && !await SaveDocumentAsync(doc)) return false;
        }
        // Closing for good: documents whose edits were discarded must never be saved afterwards.
        AutoSave.Stop();
        _operations.Cancel();
        LayoutSaveRequested?.Invoke(); await SaveSettingsAsync();
        await Database.CompleteCloseAsync();
        _lifetime.Cancel(); if (_recoveryLoop != null) await _recoveryLoop;
        foreach (var doc in Documents) await _store.DeleteRecoveryAsync(doc.State.Path);
        return true;
    }
    private Task SaveSettingsAsync() => _settings.SaveAsync(new StudioSettings { RecentWorkspaces = RecentWorkspaces.ToList(), OpenDocuments = Documents.Select(d => d.State.Path).ToList(), WorkspacePath = Workspace?.Path, Configuration = Configuration, StartupProject = StartupProject?.ProjectPath, Theme = ThemeName, ShowInlayHints = ShowInlayHints, ShowInlineBlame = ShowInlineBlame, ShowCodeLens = ShowCodeLens, AutoReloadUnchangedFiles = ExternalChanges.AutoReloadUnchangedFiles, ShowQuickInfo = ShowQuickInfo, NestRelatedFiles = Explorer.NestRelatedFiles, Editor = Editor.ToSettings() });
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _logger.LogError(ex, "Operation failed"); Status = ex.Message; AppendOutput(ex.ToString()); }
    }
    /// <summary>For work nobody awaits: logs and shows its fault as a guarded command would. Safe on any thread.</summary>
    private void ReportBackgroundFault(Exception fault)
    {
        _logger.LogError(fault, "Background operation failed");
        _dispatcher.Post(() => { if (!_disposed) { Status = fault.Message; AppendOutput(fault.ToString()); } });
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Messenger.UnregisterAll(this); _workspace.WorkerExited -= WorkerExited;
        XamlAnalysis.Dispose(); ExternalChanges.Dispose(); AutoSave.Dispose(); Editor.PropertyChanged -= EditorPreferenceChanged; TaskList.Dispose(); Palette.Dispose();
        _settingsSave?.Cancel(); _settingsSave?.Dispose(); _settingsSave = null;
        Breakpoints.Dispose();
        Features.Dispose();
        _operations.Cancel(); _lifetime.Cancel(); if (_recoveryLoop != null) await _recoveryLoop;
        foreach (var document in Documents) document.Dispose();
        // Dispose UI-bound commands while the shell still owns its dispatcher
        // context; DI may continue its remaining asynchronous disposal off-thread.
        await Database.DisposeAsync();
        await DesignerContext.DisposeAsync();
        await Designer.DisposeAsync();
        await LiveInspection.DisposeAsync();
        await MemoryProfiler.DisposeAsync();
        await _workspace.DisposeAsync(); _lifetime.Dispose();
    }
}
