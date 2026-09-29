using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WpfStudio.App.Services;
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
    private readonly XamlCompletionService _xaml;
    private readonly XamlResourceContext _xamlResources = new();
    private readonly IFileDialogService _files;
    private readonly IUserDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private TaskCompletionSource<bool>? _previewCompletion;
    private WpfIndexSnapshot? _wpfIndex;
    private string[] _allFiles = [];
    private bool _initialized;
    private bool _disposed;
    private bool _loading;
    private bool _checkingFiles;
    private bool _contextReloadPending;
    private bool _updatingChoices;
    private int _launchChoicesGeneration;
    private Task? _recoveryLoop;

    public ShellViewModel(DocumentStore store, SettingsStore settings, WorkspaceClient workspace, BuildService build, WpfIndexService indexer, ScaffoldingService scaffolding, WorkspaceEditTransaction edits, XamlCompletionService xaml, IFileDialogService files, IUserDialogService dialogs, IUiDispatcher dispatcher, DebuggerViewModel debugger, TerminalViewModel terminal, DatabasePaneViewModel database, ILogger<ShellViewModel> logger, StudioFeatures? features = null, Features.Designer.DesignerViewModel? designer = null)
    {
        _store = store; _settings = settings; _workspace = workspace; _build = build; _indexer = indexer; _scaffolding = scaffolding; _edits = edits; _xaml = xaml; _files = files; _dialogs = dialogs; _dispatcher = dispatcher; _logger = logger;
        Debugger = debugger; Terminal = terminal; Database = database;
        Designer = designer ?? new(new WpfStudio.Runtime.Design.PreviewClient(), dispatcher);
        Designer.SourceRequested += source => _ = GuardAsync(() => NavigateAsync(source.Path, source.Line, source.Column));
        Designer.SourceEditRequested += PreviewDesignerEditAsync;
        Designer.LayoutEditRequested += PreviewDesignerLayoutEditAsync;
        Designer.BindingSourceRequested += NavigatePreviewBindingSourceAsync;
        Designer.SelectionSourceChanged += RevealDesignerSelection;
        InitializeDesignerContext();
        InitializeChangeReview();
        LiveInspection = new(dispatcher);
        LiveInspection.LaunchRequested += debug => LaunchWithInspectionAsync(debug);
        LiveInspection.SourceRequested += NavigateInspectionSourceAsync;
        LiveInspection.BindingSourceRequested += NavigateInspectionBindingSourceAsync;
        LiveInspection.SourceEditRequested += EditInspectionSourceAsync;
        InitializeFeatures(features);
        InitializeExperience();
        InitializeProjectXamlAnalysis();
        debugger.SourceRequested += (path, line) => _ = GuardAsync(() => NavigateAsync(path, line, 1, true));
        debugger.Breakpoints.CollectionChanged += (_, _) => ObserveBreakpoints();
        debugger.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DebuggerViewModel.IsStopped)) return;
            LiveInspection.SetDebuggerState(debugger.IsStopped);
            if (!debugger.IsStopped) foreach (var doc in Documents) doc.ExecutionLine = -1;
        };
        workspace.WorkerExited += (_, reason) => dispatcher.Post(() => { Status = "Language worker stopped. Use Restart language service to reconnect."; AppendOutput(reason); });
    }
    public DebuggerViewModel Debugger { get; }
    public TerminalViewModel Terminal { get; }
    public DatabasePaneViewModel Database { get; }
    public ObservableCollection<EditorViewModel> Documents { get; } = [];
    public ObservableCollection<ExplorerNode> Explorer { get; } = [];
    public ObservableCollection<WorkspaceProject> Projects { get; } = [];
    public ObservableCollection<string> RecentWorkspaces { get; } = [];
    public ObservableCollection<NavigationResult> SearchResults { get; } = [];
    public ObservableCollection<WorkspaceDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<WpfItem> WpfItems { get; } = [];
    public ObservableCollection<PaletteEntry> PaletteResults { get; } = [];
    public ObservableCollection<FileChange> PreviewChanges { get; } = [];
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
    public event Action<string>? ThemeChanged;

    [ObservableProperty] public partial WorkspaceSnapshot? Workspace { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial EditorViewModel? ActiveDocument { get; set; }
    [ObservableProperty] public partial string? ActiveWorkbench { get; set; }
    [ObservableProperty] public partial WorkspaceProject? StartupProject { get; set; }
    [ObservableProperty] public partial ExplorerNode? SelectedNode { get; set; }
    [ObservableProperty] public partial WpfItem? SelectedWpfItem { get; set; }
    [ObservableProperty] public partial WorkspaceDiagnostic? SelectedDiagnostic { get; set; }
    [ObservableProperty] public partial NavigationResult? SelectedSearchResult { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Ready";
    [ObservableProperty] public partial string Output { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Configuration { get; set; } = "Debug";
    [ObservableProperty] public partial string? TargetFramework { get; set; }
    [ObservableProperty] public partial string? LaunchProfile { get; set; }
    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    [ObservableProperty] public partial string WpfFilter { get; set; } = "";
    [ObservableProperty] public partial string PaletteQuery { get; set; } = "";
    [ObservableProperty] public partial bool IsPaletteOpen { get; set; }
    [ObservableProperty] public partial bool IsCommandPalette { get; set; }
    [ObservableProperty] public partial PaletteEntry? SelectedPaletteEntry { get; set; }
    [ObservableProperty] public partial bool IsPreviewOpen { get; set; }
    [ObservableProperty] public partial FileChange? SelectedPreviewChange { get; set; }
    [ObservableProperty] public partial string PreviewTitle { get; set; } = "Preview changes";
    [ObservableProperty] public partial string PreviewWarnings { get; set; } = "";
    [ObservableProperty] public partial bool IsScaffoldOpen { get; set; }
    [ObservableProperty] public partial ScaffoldKind SelectedScaffoldKind { get; set; } = ScaffoldKind.ViewAndViewModel;
    [ObservableProperty] public partial string ScaffoldName { get; set; } = "CustomerView";
    [ObservableProperty] public partial bool AddDataTemplate { get; set; }
    [ObservableProperty] public partial string SelectedBuildAction { get; set; } = "Resource";
    [ObservableProperty] public partial string ThemeName { get; set; } = "Dark";
    public string WindowTitle => Workspace == null ? "WpfStudio" : $"{Path.GetFileNameWithoutExtension(Workspace.Path)} — WpfStudio";
    public bool HasWorkspace => Workspace != null;
    public bool IsWelcomeVisible => Documents.Count == 0;
    partial void OnWorkspaceChanged(WorkspaceSnapshot? value) { OnPropertyChanged(nameof(WindowTitle)); OnPropertyChanged(nameof(HasWorkspace)); Features?.Packages.SetWorkspace(value); NotifyWorkspaceSummary(); ResetProjectXamlAnalysis(value); }
    partial void OnWpfFilterChanged(string value) => RefreshWpfItems();
    partial void OnPaletteQueryChanged(string value) => RefreshPalette();
    partial void OnStartupProjectChanged(WorkspaceProject? value) { if (value != null) _ = GuardAsync(RefreshLaunchChoicesAsync); }
    partial void OnConfigurationChanged(string value) { if (!string.IsNullOrWhiteSpace(value)) ScheduleContextReload(); }
    partial void OnTargetFrameworkChanged(string? value) { if (value != null) ScheduleContextReload(); }
    private void ScheduleContextReload()
    {
        if (!_initialized || Workspace == null || _updatingChoices) return;
        _contextReloadPending = true;
        if (!_loading && !IsBusy) _ = ReloadWorkspaceAsync();
    }

    public async Task InitializeAsync(string[] arguments)
    {
        var settings = await _settings.LoadAsync();
        if (Features != null) await Features.Assistant.InitializeAsync();
        ThemeName = settings.Theme; ThemeChanged?.Invoke(ThemeName);
        Configuration = settings.Configuration;
        foreach (var recent in settings.RecentWorkspaces.Where(File.Exists)) RecentWorkspaces.Add(recent);
        _initialized = true;
        var requested = arguments.FirstOrDefault(a => File.Exists(a) && Path.GetExtension(a).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj");
        if (requested != null) await LoadWorkspaceAsync(requested);
        else if (settings.WorkspacePath != null && File.Exists(settings.WorkspacePath)) await LoadWorkspaceAsync(settings.WorkspacePath);
        if (Workspace != null && requested == null)
        {
            StartupProject = Projects.FirstOrDefault(p => p.ProjectPath == settings.StartupProject) ?? StartupProject;
            foreach (var path in settings.OpenDocuments.Where(File.Exists)) await GuardAsync(() => OpenDocumentAsync(path));
        }
        var recovered = await _store.ReadRecoveryAsync();
        if (recovered.Count > 0 && await _dialogs.ConfirmAsync("Recover unsaved work", $"Recover {recovered.Count} unsaved document(s) from the previous session?"))
        {
            foreach (var item in recovered)
                await GuardAsync(async () => { var state = File.Exists(item.Path) ? await _store.OpenAsync(item.Path) : _store.Create(item.Path); state.Content = item.Content; AddDocument(state); });
        }
        else foreach (var item in recovered) _store.DeleteRecovery(item.Path);
        LayoutRestoreRequested?.Invoke();
        _recoveryLoop = RecoveryLoopAsync(_lifetime.Token);
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
        if (IsBusy || _loading) { Status = "Wait for the current workspace operation to finish"; return; }
        if (IsPreviewOpen) { Status = "Finish or cancel the change preview before switching workspaces"; return; }
        if (Documents.Count > 0)
        {
            var closing = Documents.ToArray();
            var discard = new List<EditorViewModel>();
            foreach (var doc in closing.Where(d => d.State.IsDirty))
            {
                var decision = await _dialogs.AskSaveAsync(doc.State.Name);
                if (decision == SaveDecision.Cancel) return;
                if (decision == SaveDecision.Save && !await SaveDocumentAsync(doc)) return;
                if (decision == SaveDecision.Discard) discard.Add(doc);
            }
            // Do not discard anything until all close decisions have succeeded.
            foreach (var doc in closing)
            {
                if (discard.Contains(doc)) _store.DeleteRecovery(doc.State.Path);
                if (Designer.SourcePath == doc.State.Path) await Designer.CloseAsync();
                doc.Dispose(); Documents.Remove(doc); _store.Close(doc.State); UntrackDiagnosticDocument(doc.State);
            }
            ActiveDocument = null; OnPropertyChanged(nameof(IsWelcomeVisible));
        }
        if (Debugger.IsActive) await Debugger.StopCommand.ExecuteAsync(null);
        if (_gitToolOpened && Features != null) await Features.Git.SetWorkspaceAsync(null);
        IsBusy = true; _loading = true; _operation = new();
        var token = _operation.Token;
        try
        {
            Status = "Loading " + Path.GetFileName(path) + "…";
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Terminal.WorkingDirectory = directory;
            _allFiles = await Task.Run(() => EnumerateSourceFiles(directory).ToArray(), token);
            Explorer.Clear(); foreach (var node in ExplorerNode.FromFiles(directory, _allFiles)) Explorer.Add(node);
            TargetFramework = null;
            Workspace = await _workspace.LoadAsync(new(path, Configuration), token);
            RefreshConfigurations();
            Projects.Clear(); foreach (var project in Workspace.Projects) Projects.Add(project);
            StartupProject = Projects.FirstOrDefault(p => p.IsExecutable) ?? Projects.FirstOrDefault();
            _allFiles = _allFiles.Concat(Workspace.Projects.SelectMany(p => p.Files).Where(f => !f.IsGenerated).Select(f => f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Explorer.Clear();
            foreach (var project in Projects)
            {
                var local = project;
                var projectFiles = project.Files.Where(f => !f.IsGenerated).Select(f => f.Path).Append(project.ProjectPath).ToArray();
                Explorer.Add(new ExplorerNode(project.Name, project.ProjectPath, true, () => ExplorerNode.FromFiles(Path.GetDirectoryName(local.ProjectPath)!, projectFiles, local.ProjectPath), local.ProjectPath));
            }
            if (Explorer.Count == 0) foreach (var node in ExplorerNode.FromFiles(directory, _allFiles)) Explorer.Add(node);
            foreach (var issue in Workspace.Issues) AppendOutput(issue.Severity + ": " + issue.Message);
            RecentWorkspaces.Remove(path); RecentWorkspaces.Insert(0, path); while (RecentWorkspaces.Count > 10) RecentWorkspaces.RemoveAt(10);
            await Debugger.SetWorkspaceAsync(path);
            if (_gitToolOpened && Features != null) await Features.Git.SetWorkspaceAsync(path);
            await RefreshWpfAsync();
            Status = $"{Projects.Count} projects · SDK {Workspace.SdkVersion}";
            await SaveSettingsAsync();
        }
        finally { IsBusy = false; _loading = false; _operation.Dispose(); _operation = null; RequestProjectXamlAnalysis(); if (_contextReloadPending) _ = ReloadWorkspaceAsync(); }
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
    [RelayCommand] private Task RestartWorkspaceAsync() => GuardAsync(async () => { Workspace = await _workspace.RestartAsync(); Status = "Language service restarted"; });
    [RelayCommand] private Task ReloadWorkspaceAsync() => GuardAsync(async () =>
    {
        if (Workspace == null) return;
        _contextReloadPending = true;
        if (_loading) return;
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
            Workspace = await _workspace.LoadAsync(new(Workspace.Path, requestedConfiguration, Path.GetExtension(Workspace.Path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? requestedFramework : null));
            RefreshConfigurations();
            Projects.Clear(); foreach (var project in Workspace.Projects) Projects.Add(project);
            StartupProject = Projects.FirstOrDefault(p => p.ProjectPath == startupPath) ?? Projects.FirstOrDefault(p => p.IsExecutable) ?? Projects.FirstOrDefault();
            _allFiles = _allFiles.Concat(Workspace.Projects.SelectMany(p => p.Files).Where(f => !f.IsGenerated).Select(f => f.Path)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Explorer.Clear();
            foreach (var project in Projects)
            {
                var local = project;
                var projectFiles = project.Files.Where(f => !f.IsGenerated).Select(f => f.Path).Append(project.ProjectPath).ToArray();
                Explorer.Add(new ExplorerNode(project.Name, project.ProjectPath, true, () => ExplorerNode.FromFiles(Path.GetDirectoryName(local.ProjectPath)!, projectFiles, local.ProjectPath), local.ProjectPath));
            }
            foreach (var doc in Documents) await doc.SyncAsync();
            await RefreshWpfAsync(); Status = "Workspace refreshed";
            if (requestedConfiguration != Configuration || requestedFramework != TargetFramework) _contextReloadPending = true;
          } while (_contextReloadPending && !_lifetime.IsCancellationRequested);
        }
        finally { _loading = false; RequestProjectXamlAnalysis(); }
    });
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
    [RelayCommand] private Task OpenNodeAsync(ExplorerNode? node) => node == null || node.IsFolder ? Task.CompletedTask : GuardAsync(() => OpenDocumentAsync(node.Path));
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
        var vm = new EditorViewModel(state, _workspace, _xaml, _dispatcher, AppendOutput, _xamlResources);
        ConfigureXamlContext(vm);
        vm.BreakpointRequested += line => _ = GuardAsync(() => Debugger.ToggleBreakpointAsync(state.Path, line));
        vm.ActionRequested += action => _ = GuardAsync(() => HandleEditorActionAsync(vm, action));
        vm.CodeActionRequested += action => ApplyXamlCodeActionAsync(vm, action);
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
        Documents.Add(vm); ActiveDocument = vm; RefreshBreakpointMarkers();
        TrackDiagnosticDocument(state);
        OnPropertyChanged(nameof(IsWelcomeVisible));
    }
    public async Task<bool> CloseDocumentAsync(EditorViewModel document)
    {
        if (document.State.IsDirty)
        {
            var decision = await _dialogs.AskSaveAsync(document.State.Name);
            if (decision == SaveDecision.Cancel) return false;
            if (decision == SaveDecision.Save && !await SaveDocumentAsync(document)) return false;
            if (decision == SaveDecision.Discard) _store.DeleteRecovery(document.State.Path);
        }
        if (Designer.SourcePath == document.State.Path) await Designer.CloseAsync();
        document.Dispose(); Documents.Remove(document); _store.Close(document.State);
        UntrackDiagnosticDocument(document.State);
        // Queue the worker close before awaiting index refresh. A reopened
        // editor must synchronize after this old buffer has been retired.
        if (document.IsCSharp || document.IsXaml)
            try { await _workspace.CloseDocumentAsync(document.State.Path); } catch (Exception ex) { AppendOutput("Language document close: " + ex.Message); }
        if (document.State.Extension == ".xaml" && _wpfIndex?.Texts.TryGetValue(document.State.Path, out var indexedText) == true && indexedText != document.State.Content)
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
            await _store.SaveAsync(doc.State);
            if (doc.IsXaml) _xamlResources.Invalidate();
            RequestProjectXamlAnalysis();
            Status = doc.State.IsDirty ? "The document changed while saving. Save again to include the latest edits." : "Saved " + doc.State.Name;
            return !doc.State.IsDirty;
        }
        catch (ExternalFileChangedException)
        {
            if (!await _dialogs.ConfirmAsync("File changed outside WpfStudio", $"Overwrite the external changes to {doc.State.Name} with the editor content?")) return false;
            await _store.SaveAsync(doc.State, true);
            if (doc.IsXaml) _xamlResources.Invalidate();
            RequestProjectXamlAnalysis();
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
    public async Task CheckExternalChangesAsync()
    {
        if (_checkingFiles) return; _checkingFiles = true;
        try
        {
            foreach (var doc in Documents.ToArray())
            {
                if (!await _store.IsExternallyChangedAsync(doc.State)) continue;
                if (doc.State.IsDirty || !File.Exists(doc.State.Path)) doc.State.HasExternalChange = true;
                else await _store.ReloadAsync(doc.State);
            }
        }
        catch (Exception ex) { AppendOutput(ex.Message); }
        finally { _checkingFiles = false; RequestProjectXamlAnalysis(); }
    }
    [RelayCommand] private Task ReloadDocumentAsync() => GuardAsync(async () => { if (ActiveDocument == null) return; if (ActiveDocument.State.IsDirty && !await _dialogs.ConfirmAsync("Reload document", "Discard editor changes and reload this file from disk?")) return; await _store.ReloadAsync(ActiveDocument.State); });
    [RelayCommand] private Task BuildAsync() => RunBuildAsync(BuildOperation.Build);
    [RelayCommand] private Task RestoreAsync() => RunBuildAsync(BuildOperation.Restore);
    [RelayCommand] private Task RebuildAsync() => RunBuildAsync(BuildOperation.Rebuild);
    [RelayCommand] private Task CleanAsync() => RunBuildAsync(BuildOperation.Clean);
    [RelayCommand] private Task RunAsync() => RunBuildAsync(BuildOperation.Run);
    [RelayCommand] private Task TestAsync() => RunBuildAsync(BuildOperation.Test);
    [RelayCommand] private void CancelOperation() { _operation?.Cancel(); Status = "Cancelling…"; }
    private Task RunBuildAsync(BuildOperation operation) => GuardAsync(async () =>
    {
        if (Workspace == null || IsBusy || _loading) return;
        IsBusy = true; _operation = new(); ToolRequested?.Invoke("Output"); Output = "";
        try
        {
            if (!await SaveAllDocumentsAsync()) return;
            var path = operation == BuildOperation.Run ? StartupProject?.ProjectPath ?? Workspace.Path : Workspace.Path;
            Status = operation + "…";
            var framework = Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? TargetFramework : null;
            using var progress = CreateBuildProgress();
            var result = await _build.RunAsync(new(path, operation, Configuration, framework, LaunchProfile), progress, _operation.Token);
            progress.Flush();
            Status = result.Cancelled ? "Cancelled" : result.ExitCode == 0 ? operation + " succeeded" : operation + $" failed (exit {result.ExitCode})";
            if (result.ExitCode == 0 && operation is BuildOperation.Build or BuildOperation.Rebuild or BuildOperation.Restore) await ReloadWorkspaceAsync();
        }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; if (_contextReloadPending) _ = ReloadWorkspaceAsync(); }
    });
    [RelayCommand] private Task DebugAsync() => GuardAsync(async () =>
    {
        if (Debugger.IsStopped) { await Debugger.ContinueCommand.ExecuteAsync(null); return; }
        if (Debugger.IsActive) { Status = "The debugger is already running"; return; }
        if (IsBusy || _loading || StartupProject == null) return;
        IsBusy = true; _operation = new(); ToolRequested?.Invoke("Debugger");
        try
        {
            if (!await SaveAllDocumentsAsync()) return;
            var project = StartupProject;
            var configuration = Configuration;
            var framework = TargetFramework;
            var profile = LaunchProfile;
            Status = "Building for debug…";
            using var progress = CreateBuildProgress();
            var result = await _build.RunAsync(new(project.ProjectPath, BuildOperation.Build, configuration, framework), progress, _operation.Token);
            progress.Flush();
            if (result.ExitCode != 0 || result.Cancelled) { Status = "Debug launch cancelled: build did not succeed"; return; }
            var launch = await _build.ResolveLaunchAsync(project.ProjectPath, configuration, framework, profile, _operation.Token);
            await Debugger.LaunchAsync(new DebugLaunchConfiguration(launch.Program, launch.WorkingDirectory, launch.Arguments, launch.Environment), _operation.Token);
            Status = "Debugging " + project.Name;
        }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; if (_contextReloadPending) _ = ReloadWorkspaceAsync(); }
    });
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
    [RelayCommand] private Task ToggleBreakpointAsync() => ActiveDocument?.State.Extension != ".cs" ? Task.CompletedTask : GuardAsync(() => Debugger.ToggleBreakpointAsync(ActiveDocument.State.Path, ActiveDocument.State.CaretLine));
    private void RefreshBreakpointMarkers()
    {
        foreach (var doc in Documents)
        {
            doc.BreakpointLines.Clear(); doc.BreakpointMarkers.Clear();
            foreach (var bp in Debugger.Breakpoints.Where(b => b.Path.Equals(doc.State.Path, StringComparison.OrdinalIgnoreCase)))
            {
                if (bp.Enabled) doc.BreakpointLines.Add(bp.Line);
                doc.BreakpointMarkers.Add(new(bp.Line, bp.Enabled, bp.Status == "Bound", bp.Condition, bp.Status));
            }
        }
    }
    private void RefreshEditorDiagnostics()
    {
        Diagnostics.Clear(); foreach (var item in CurrentEditorDiagnostics().Concat(CurrentProjectXamlDiagnostics()).Concat(CurrentWpfIndexDiagnostics()).Concat(_buildDiagnostics).Distinct()) Diagnostics.Add(item);
        RefreshWpfIssues();
    }
    private IEnumerable<WorkspaceDiagnostic> CurrentWpfIndexDiagnostics() => (_wpfIndex?.Diagnostics ?? []).Where(issue =>
        !IsIndexDiagnosticStale(issue) && (issue.Path is null || _store.Find(issue.Path) is not { } document ||
         (_wpfIndex!.Texts.TryGetValue(issue.Path, out var indexed) && indexed == document.Content)) &&
        (issue.Id != "XAML001" || !Documents.Any(editor => editor.State.Path.Equals(issue.Path, StringComparison.OrdinalIgnoreCase) &&
            editor.Diagnostics.Any(diagnostic => diagnostic.Id == "XAMLSYNTAX001"))));
    private void RefreshWpfIssues()
    {
        WpfIssues.Clear();
        foreach (var issue in CurrentWpfIndexDiagnostics().Concat(CurrentProjectXamlDiagnostics()).Concat(CurrentEditorDiagnostics(xamlOnly: true)).Distinct()) WpfIssues.Add(issue);
        OnPropertyChanged(nameof(WpfIssueTitle));
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
            var usage = _wpfIndex?.Texts.TryGetValue(current.Path, out var indexed) == true && indexed == current.Content
                ? _wpfIndex.Usages.FirstOrDefault(u => u.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase) && caret >= u.Start && caret <= u.Start + u.Length) : null;
            var declaration = usage?.ResolvedPath == null ? null : _wpfIndex!.Resources.FirstOrDefault(r => r.Path == usage.ResolvedPath && r.ValueStart == usage.ResolvedDeclarationStart);
            if (declaration != null) await NavigateAsync(declaration.Path, declaration.Line, 1); else Status = "This XAML reference could not be resolved statically";
            return;
        }
        await document.SyncAsync(); var state = document.State;
        var results = await _workspace.GetDefinitionAsync(new(state.Path, state.CaretOffset, state.Version));
        if (results.Count > 0) await NavigateAsync(results[0].Path, results[0].Line, results[0].Column); else Status = "No source definition found";
    });
    [RelayCommand] private Task FindReferencesAsync() => GuardAsync(FindLanguageSymbolReferencesAsync);
    [RelayCommand] private Task FormatAsync() => GuardAsync(FormatActiveDocumentAsync);
    [RelayCommand] private Task RenameSymbolAsync() => GuardAsync(RenameLanguageSymbolAsync);
    [RelayCommand] private Task UndoWorkspaceEditAsync() => GuardAsync(async () =>
    {
        foreach (var path in _edits.Undo())
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
            SearchResults.Clear(); ToolRequested?.Invoke("Search");
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
        if (!await VerifyProjectDiagnosticAsync(diagnostic)) { Status = "The diagnostic source changed. Refresh project XAML analysis before navigating."; RequestProjectXamlAnalysis(); return; }
        await OpenDocumentAsync(diagnostic.Path);
        if (ActiveDocument is not { } editor || !IsOpenedProjectDiagnosticCurrent(diagnostic, editor.State))
        { Status = "The diagnostic source changed while opening it. Refresh project XAML analysis before navigating."; return; }
        SelectXamlDiagnosticContext(editor, diagnostic);
        await NavigateAsync(diagnostic.Path, diagnostic.Line, diagnostic.Column);
    });
    [RelayCommand] private Task NavigateWpfAsync(WpfItem? item) => item == null ? Task.CompletedTask : GuardAsync(() => NavigateAsync(item.Path, item.Line, 1));
    public async Task NavigateAsync(string path, int line, int column, bool execution = false)
    {
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
        var revision = Volatile.Read(ref _wpfIndexRevision);
        var buffers = _store.Documents.ToDictionary(d => d.Path, d => d.Content, StringComparer.OrdinalIgnoreCase);
        var index = await Task.Run(() => _indexer.IndexAsync(workspace, buffers, _lifetime.Token));
        if (!ReferenceEquals(workspace, Workspace) || revision != Volatile.Read(ref _wpfIndexRevision)) return;
        _wpfIndex = index; _xaml.Index = _wpfIndex;
        _invalidIndexPaths.Clear(); _indexTextHashes.Clear(); _indexDiagnosticsInvalid = false;
        RefreshWpfItems(); RefreshEditorDiagnostics(); RefreshAllRelated();
        _ = _projectAnalysis?.Schedule();
    });
    private void RefreshWpfItems()
    {
        var selected = SelectedWpfItem;
        WpfItems.Clear();
        if (_wpfIndex != null)
            foreach (var item in _wpfIndex.Items.Where(i => MatchesWpfCategory(i) && (i.Name + " " + i.Kind + " " + i.Path).Contains(WpfFilter, StringComparison.OrdinalIgnoreCase))) WpfItems.Add(item);
        SelectedWpfItem = WpfItems.FirstOrDefault(i => i == selected);
        RefreshWpfIssues();
        OnPropertyChanged(nameof(WpfSummary)); OnPropertyChanged(nameof(WpfResultSummary)); OnPropertyChanged(nameof(WpfIssueTitle));
        RefreshWpfSelection();
    }
    [RelayCommand] private Task RenameResourceAsync() => GuardAsync(async () =>
    {
        if (_wpfIndex == null || SelectedWpfItem?.Key == null) { Status = "Select a keyed resource in WPF Explorer"; return; }
        var item = SelectedWpfItem; var declaration = _wpfIndex.Resources.First(r => r.Path == item.Path && r.Key == item.Key && r.Line == item.Line);
        var name = await _dialogs.PromptAsync("Rename resource", "New resource key:", declaration.Key); if (name == null) return;
        var changes = _indexer.RenameResource(_wpfIndex, declaration, name);
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
        var target = Projects.FirstOrDefault(p => p.ProjectPath == SelectedNode?.ProjectPath) ?? StartupProject;
        var root = Path.GetDirectoryName(target.ProjectPath)!;
        var directory = SelectedNode is { IsFolder: true } && Directory.Exists(SelectedNode.Path) && (SelectedNode.Path.Equals(root, StringComparison.OrdinalIgnoreCase) || SelectedNode.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ? SelectedNode.Path : root;
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
        var state = document.State; var template = ScaffoldingService.PropertyTemplate(name, context?.SupportsPartialProperties == true).Replace("[ObservableProperty]", "[CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]", StringComparison.Ordinal);
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
        var targetProject = Projects.FirstOrDefault(p => p.ProjectPath == SelectedNode?.ProjectPath) ?? StartupProject;
        var source = await _files.OpenFileAsync("Import resource asset", "Assets|*.png;*.jpg;*.jpeg;*.ico;*.svg;*.ttf;*.otf|All files|*.*"); if (source == null) return;
        var destination = Path.Combine(Path.GetDirectoryName(targetProject.ProjectPath)!, "Assets", Path.GetFileName(source));
        if (File.Exists(destination)) throw new IOException("An asset with that name already exists.");
        var project = await _store.OpenAsync(targetProject.ProjectPath);
        var change = ScaffoldingService.SetBuildAction(project.Path, project.Content, destination, "Resource");
        if (!await PreviewAsync("Import asset", [change], $"Copy {source} to {destination}")) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, false);
        try { await ApplyChangesAsync([change]); }
        catch { File.Delete(destination); throw; }
        Status = ScaffoldingService.PackUri(project.Path, destination, targetProject.AssemblyName);
    });
    [RelayCommand] private Task SetBuildActionAsync() => GuardAsync(async () =>
    {
        var path = SelectedWpfItem?.Path ?? SelectedNode?.Path; if (path == null) return;
        var owner = ResolveAssetOwner(path);
        var project = await _store.OpenAsync(owner.ProjectPath);
        var change = ScaffoldingService.SetBuildAction(project.Path, project.Content, path, SelectedBuildAction);
        if (await PreviewAsync("Change build action", [change], "")) await ApplyChangesAsync([change]);
    });
    private WorkspaceProject ResolveAssetOwner(string path)
    {
        var owners = Projects.Where(p => p.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (SelectedNode?.Path == path && owners.FirstOrDefault(p => p.ProjectPath == SelectedNode.ProjectPath) is { } selected) return selected;
        if (owners.Length == 1) return owners[0];
        throw new InvalidOperationException(owners.Length == 0 ? "This file is not an evaluated project item. Refresh the workspace first." : "This linked asset belongs to multiple projects. Select it under the intended project in Solution Explorer.");
    }
    [RelayCommand] private Task ShowPackUriAsync() => GuardAsync(() =>
    {
        var path = SelectedWpfItem?.Path ?? SelectedNode?.Path;
        if (path != null) { var owner = ResolveAssetOwner(path); var logicalPath = owner.Files.First(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).LogicalPath; var uri = ScaffoldingService.PackUri(owner.ProjectPath, path, owner.AssemblyName, logicalPath); AppendOutput(uri); ToolRequested?.Invoke("Output"); Status = uri; }
        return Task.CompletedTask;
    });
    private async Task<bool> PreviewAsync(string title, IReadOnlyList<FileChange> changes, string warnings)
    {
        if (_previewCompletion is { Task.IsCompleted: false }) { Status = "Finish the current change preview first"; return false; }
        if (changes.Count == 0) { Status = "No changes required"; return false; }
        PreviewTitle = title; PreviewWarnings = warnings; PreviewChanges.Clear(); foreach (var change in changes) PreviewChanges.Add(change); SelectedPreviewChange = changes[0];
        _previewCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); IsPreviewOpen = true;
        return await _previewCompletion.Task;
    }
    [RelayCommand] private void AcceptPreview() { IsPreviewOpen = false; _previewCompletion?.TrySetResult(true); }
    [RelayCommand] private void CancelPreview() { IsPreviewOpen = false; _previewCompletion?.TrySetResult(false); }
    private async Task ApplyChangesAsync(IReadOnlyList<FileChange> changes) { await _edits.ApplyAsync(changes); foreach (var change in changes) AddDocument(_store.Find(change.Path)!); }
    [RelayCommand] private void QuickOpen() { IsCommandPalette = false; PaletteQuery = ""; IsPaletteOpen = true; RefreshPalette(); }
    [RelayCommand] private void CommandPalette() { IsCommandPalette = true; PaletteQuery = ">"; IsPaletteOpen = true; RefreshPalette(); }
    [RelayCommand] private void ClosePalette() => IsPaletteOpen = false;
    [RelayCommand] private Task ExecutePaletteAsync() => GuardAsync(async () => { var entry = SelectedPaletteEntry; if (entry == null) return; IsPaletteOpen = false; await entry.Execute(); });
    [RelayCommand] private void ToggleTheme() { ThemeName = ThemeName == "Dark" ? "Light" : "Dark"; ThemeChanged?.Invoke(ThemeName); }
    [RelayCommand] private void ShowTool(string? name)
    {
        if (name == "Packages") _ = OpenPackagesCommand.ExecuteAsync(null);
        else if (name == "Git") _ = OpenGitCommand.ExecuteAsync(null);
        else if (name == "ColtonGPT") _ = OpenAssistantCommand.ExecuteAsync(null);
        else if (name == "Designer") _ = OpenDesignerCommand.ExecuteAsync(null);
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
        if (!await Database.ConfirmCloseAllAsync()) return false;
        foreach (var doc in Documents.Where(d => d.State.IsDirty).ToArray())
        {
            var decision = await _dialogs.AskSaveAsync(doc.State.Name);
            if (decision == SaveDecision.Cancel) return false;
            if (decision == SaveDecision.Save && !await SaveDocumentAsync(doc)) return false;
        }
        _operation?.Cancel();
        LayoutSaveRequested?.Invoke(); await SaveSettingsAsync();
        await Database.CompleteCloseAsync();
        _lifetime.Cancel(); if (_recoveryLoop != null) await _recoveryLoop;
        foreach (var doc in Documents) _store.DeleteRecovery(doc.State.Path);
        return true;
    }
    private Task SaveSettingsAsync() => _settings.SaveAsync(new StudioSettings { RecentWorkspaces = RecentWorkspaces.ToList(), OpenDocuments = Documents.Select(d => d.State.Path).ToList(), WorkspacePath = Workspace?.Path, Configuration = Configuration, StartupProject = StartupProject?.ProjectPath, Theme = ThemeName });
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _logger.LogError(ex, "Operation failed"); Status = ex.Message; AppendOutput(ex.ToString()); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeProjectXamlAnalysis();
        foreach (var breakpoint in _observedBreakpoints) breakpoint.PropertyChanged -= BreakpointChanged;
        Features?.Dispose();
        _operation?.Cancel(); _lifetime.Cancel(); if (_recoveryLoop != null) await _recoveryLoop;
        foreach (var document in Documents) document.Dispose();
        // Dispose UI-bound commands while the shell still owns its dispatcher
        // context; DI may continue its remaining asynchronous disposal off-thread.
        await Database.DisposeAsync();
        await DisposeDesignerContextAsync();
        await Designer.DisposeAsync();
        await LiveInspection.DisposeAsync();
        await _workspace.DisposeAsync(); _operation?.Dispose(); _lifetime.Dispose();
    }
}
