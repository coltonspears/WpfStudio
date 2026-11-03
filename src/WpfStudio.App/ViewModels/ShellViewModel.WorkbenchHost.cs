using WpfStudio.App.Features.Debugging;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Documents;
using WpfStudio.App.Features.Inspection;
using WpfStudio.App.Features.Review;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Features.WpfExplorer;
using WpfStudio.Core.Wpf;
using WpfStudio.App.Features.XamlAnalysis;
using WpfStudio.App.Services;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

/// <summary>The shell as seen by the tools that were split out of it: Task List, file groups, Problems and the external-changes review.</summary>
public sealed partial class ShellViewModel : ICommandPaletteHost, IExternalChangesHost, IProjectXamlAnalysisHost, IAutoSaveHost, IBreakpointEditingHost, ISolutionExplorerHost, IStudioToolsHost, IInspectionSourceHost, IDesignerSourceHost, IInspectionRequestHost
{
    public TaskListViewModel TaskList { get; }
    public FileGroupsViewModel FileGroups { get; }
    public ProblemsViewModel Problems { get; }
    public ExternalChangesViewModel ExternalChanges { get; }
    public CommandPaletteViewModel Palette { get; }
    public ChangeReviewViewModel ChangeReview { get; }
    public ProjectXamlAnalysisCoordinator XamlAnalysis { get; }
    public SolutionExplorerViewModel Explorer { get; }
    public WpfExplorerViewModel WpfExplorer { get; }
    public AutoSaveCoordinator AutoSave { get; }
    public BreakpointEditingViewModel Breakpoints { get; }
    public InspectionSourceVerifier InspectionSources { get; }
    public DesignerSourceTracker DesignerContext { get; }
    /// <summary>True while a change review is open; workspace operations wait for the decision.</summary>
    public bool IsPreviewOpen => ChangeReview.IsOpen;
    private Action? _openDocumentsChanged, _documentsChanged;
    /// <summary>Raised when a tab is opened or closed.</summary>
    private void NotifyOpenDocumentsChanged() { _openDocumentsChanged?.Invoke(); _documentsChanged?.Invoke(); }
    /// <summary>Raised when the active tab changes.</summary>
    private void NotifyActiveDocumentChanged() => _documentsChanged?.Invoke();

    // ------------------------------------------------------------------ IWorkbenchHost
    string? IWorkbenchHost.WorkspacePath => Workspace?.Path;
    IReadOnlyList<string> IWorkbenchHost.WorkspaceFiles => _allFiles;
    IReadOnlyList<string> IWorkbenchHost.OpenDocumentPaths => Documents.Select(d => d.State.Path).ToArray();
    event Action? IWorkbenchHost.DocumentsChanged { add => _documentsChanged += value; remove => _documentsChanged -= value; }
    IReadOnlyDictionary<string, string> IWorkbenchHost.OpenDocumentTexts() => Documents.ToDictionary(d => d.State.Path, d => d.State.Content, StringComparer.OrdinalIgnoreCase);
    (string Path, int Line)? IWorkbenchHost.ActiveLocation => ActiveDocument is { } active ? (active.State.Path, active.State.CaretLine) : null;
    string? IWorkbenchHost.SelectedExplorerFile => Explorer.SelectedNode is { Kind: ExplorerNodeKind.File, Path.Length: > 0 } node ? node.Path : null;
    IUserDialogService IWorkbenchHost.Dialogs => _dialogs;
    CancellationToken IWorkbenchHost.Lifetime => _lifetime.Token;
    Task IWorkbenchHost.GuardAsync(Func<Task> action) => GuardAsync(action);
    void IWorkbenchHost.ReportStatus(string status) => Status = status;
    Task IWorkbenchHost.NavigateAsync(string path, int line, int column) => NavigateAsync(path, line, column);
    Task IWorkbenchHost.OpenDocumentAsync(string path) => OpenDocumentAsync(path);
    Task<bool> IWorkbenchHost.CloseDocumentAsync(string path) =>
        Documents.FirstOrDefault(d => d.State.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } document ? CloseDocumentAsync(document) : Task.FromResult(true);
    void IWorkbenchHost.ShowTool(string toolId) => ShowToolCommand.Execute(toolId);

    // ------------------------------------------------------------------ ICommandPaletteHost
    IEnumerable<PaletteEntry> ICommandPaletteHost.CommandEntries() => CommandEntries();
    PaletteEntry ICommandPaletteHost.FileEntry(string path) => FileEntry(path);
    string ICommandPaletteHost.WorkspaceRoot => WorkspaceRoot;
    bool ICommandPaletteHost.IsSymbolSearchAvailable => _workspace.IsConnected;
    Task<IReadOnlyList<SymbolSearchResult>> ICommandPaletteHost.SearchSymbolsAsync(string query, CancellationToken token) => _workspace.SearchSymbolsAsync(new(query, 80), token);

    // ------------------------------------------------------------------ IExternalChangesHost
    IReadOnlyList<EditorViewModel> IExternalChangesHost.Documents => Documents;
    event Action? IExternalChangesHost.DocumentsChanged { add => _openDocumentsChanged += value; remove => _openDocumentsChanged -= value; }
    CancellationToken IExternalChangesHost.Lifetime => _lifetime.Token;
    Task IExternalChangesHost.GuardAsync(Func<Task> action) => GuardAsync(action);
    void IExternalChangesHost.ReportStatus(string status) => Status = status;
    void IExternalChangesHost.AppendOutput(string line) => AppendOutput(line);
    Task<bool> IExternalChangesHost.CloseDocumentAsync(EditorViewModel document, bool discardChanges) => CloseDocumentAsync(document, discardChanges);
    void IExternalChangesHost.DiskVersionsChecked()
    {
        XamlAnalysis.Request();
        // A commit or checkout in the terminal changes what change margins and blame compare with.
        RefreshEditorGit();
    }
    void IExternalChangesHost.SettingChanged() { if (_initialized) SaveSettingsAsync().Forget(ReportBackgroundFault); }

    // ------------------------------------------------------------------ IProjectXamlAnalysisHost
    WorkspaceSnapshot? IProjectXamlAnalysisHost.Workspace => Workspace;
    bool IProjectXamlAnalysisHost.IsLoading => _loading;
    IReadOnlyList<EditorViewModel> IProjectXamlAnalysisHost.Documents => Documents;
    IReadOnlyDictionary<string, string>? IProjectXamlAnalysisHost.IndexTexts => WpfExplorer.Index?.Texts;
    void IProjectXamlAnalysisHost.DiagnosticsChanged() => RefreshEditorDiagnostics();
    void IProjectXamlAnalysisHost.XamlContextsChanged() => RefreshXamlContexts();
    void IProjectXamlAnalysisHost.InvalidateXamlResources() => _xamlResources.Invalidate();
    void IProjectXamlAnalysisHost.OpenXamlDocumentsChanged() => _xamlResources.SetOpenDocuments(Documents.Select(editor => editor.State));
    void IProjectXamlAnalysisHost.ForgetIndexedState() { _buildDiagnostics.Clear(); WpfExplorer.SetIndex(null); _xaml.Index = null; }
    void IProjectXamlAnalysisHost.SemanticStateChanged() => Interlocked.Increment(ref _symbolSemanticRevision);
    void IProjectXamlAnalysisHost.AppendOutput(string line) => AppendOutput(line);

    // ------------------------------------------------------------------ IAutoSaveHost
    IReadOnlyList<EditorViewModel> IAutoSaveHost.Documents => Documents;
    CancellationToken IAutoSaveHost.Lifetime => _lifetime.Token;
    bool IAutoSaveHost.IsReviewOpen => IsPreviewOpen;
    void IAutoSaveHost.ReportStatus(string status) => Status = status;
    void IAutoSaveHost.AppendOutput(string line) => AppendOutput(line);
    void IAutoSaveHost.DocumentAutoSaved(EditorViewModel document) { if (document.IsXaml) _xamlResources.Invalidate(); }
    void IAutoSaveHost.AutoSaveCompleted() => XamlAnalysis.Request();
    void IAutoSaveHost.ExternalChangeDetected() => CheckExternalChangesAsync().Forget(ReportBackgroundFault);

    // ------------------------------------------------------------------ IBreakpointEditingHost
    EditorViewModel? IBreakpointEditingHost.ActiveDocument => ActiveDocument;
    IReadOnlyList<EditorViewModel> IBreakpointEditingHost.Documents => Documents;
    IUserDialogService IBreakpointEditingHost.Dialogs => _dialogs;
    Task IBreakpointEditingHost.GuardAsync(Func<Task> action) => GuardAsync(action);
    void IBreakpointEditingHost.ReportStatus(string status) => Status = status;
    void IBreakpointEditingHost.ShowTool(string toolId) => ShowToolCommand.Execute(toolId);

    // ------------------------------------------------------------------ ISolutionExplorerHost / IStudioToolsHost
    string? ISolutionExplorerHost.StartupProjectPath => StartupProject?.ProjectPath;
    void ISolutionExplorerHost.SetStartupProject(WorkspaceProject project) { StartupProject = project; Status = project.Name + " is the startup project"; }
    async Task<bool> IWorkspaceMutationHost.SaveBeforeMutationAsync()
    {
        if (IsBusy || IsPreviewOpen || Debugger.IsActive) throw new InvalidOperationException("Finish the current build, debug session, or change preview before modifying the workspace.");
        return await SaveAllDocumentsAsync();
    }
    Task IWorkspaceMutationHost.WorkspaceChangedAsync() => WorkspaceMutatedAsync();
    /// <summary>After a tool changed workspace files: re-check open files against disk, then reload the project model.</summary>
    private async Task WorkspaceMutatedAsync() { await CheckExternalChangesAsync(fromAppOperation: true); await ReloadWhenFreeAsync(); }
    void IStudioToolsHost.OpenFile(string path) => GuardAsync(() => OpenDocumentAsync(path)).Forget(ReportBackgroundFault);
    // Commits, checkouts and merges change what each editor's change margin and blame compare with.
    void IStudioToolsHost.GitRepositoryChanged() => RefreshEditorGit();
    void IStudioToolsHost.GitWorkspaceFilesChanged() => GuardAsync(WorkspaceMutatedAsync).Forget(ReportBackgroundFault);
    void IStudioToolsHost.OpenAssistantSettings() => OpenSettingsCommand.ExecuteAsync(AssistantPage).Forget(ReportBackgroundFault);
    void IStudioToolsHost.RefreshAssistantContext() => UpdateAssistantContext();
    void IStudioToolsHost.AssistantReadinessChanged() => OnPropertyChanged(nameof(IsColtonGptReady));
    private void RefreshEditorGit() { foreach (var document in Documents) document.RefreshGitAsync(Features.GitService, _lifetime.Token).Forget(document.ReportFault); }

    WorkspaceSnapshot? IInspectionSourceHost.Workspace => Workspace;

    // ------------------------------------------------------------------ IDesignerSourceHost
    Task<bool> IDesignerSourceHost.ReviewPropertyEditAsync(XamlPropertyEditResult proposal) => PreviewDesignerEditAsync(proposal);
    Task<bool> IDesignerSourceHost.ReviewLayoutEditAsync(DesignerLayoutSourceEdit edit) => PreviewDesignerLayoutEditAsync(edit);
    Task<string> IDesignerSourceHost.NavigateBindingSourceAsync(PreviewBindingSourceNavigation navigation, CancellationToken cancellationToken) => NavigatePreviewBindingSourceAsync(navigation, cancellationToken);

    // ------------------------------------------------------------------ IInspectionRequestHost
    Task IInspectionRequestHost.LaunchAsync(bool debug) => ExecuteWhenAvailableAsync(debug ? DebugWithInspectionCommand : RunWithInspectionCommand);
    Task<string> IInspectionRequestHost.NavigateSourceAsync(InspectionSourceRequest request, CancellationToken cancellationToken) => NavigateInspectionSourceAsync(request, cancellationToken);
    Task<string> IInspectionRequestHost.NavigateBindingSourceAsync(InspectionBindingSourceNavigation navigation, CancellationToken cancellationToken) => NavigateInspectionBindingSourceAsync(navigation, cancellationToken);
    Task<InspectionSourceEditOutcome> IInspectionRequestHost.EditSourceAsync(InspectionSourceEditRequest request, CancellationToken cancellationToken) => EditInspectionSourceAsync(request, cancellationToken);

    // ------------------------------------------------------------------ WPF Explorer
    /// <summary>The resource declared at, or referenced from, the active caret; null (with a status) when the index no longer matches the buffer.</summary>
    private ResourceDeclaration? ResourceAtCaret()
    {
        if (ActiveDocument is not { } document || WpfExplorer.Index is null) return null;
        var state = document.State;
        // Never use old index offsets after edits. Refresh first so refactor previews correspond to exactly the buffer being inspected.
        if (!WpfExplorer.IsIndexCurrent(state.Path, state.Content)) { Status = "Refresh WPF Explorer after editing resource keys"; return null; }
        return WpfExplorer.ResourceAt(state.Path, state.CaretOffset);
    }

    /// <summary>Compares open files with disk; see <see cref="ExternalChangesViewModel.CheckAsync"/>.</summary>
    public Task CheckExternalChangesAsync(bool fromAppOperation = false) => ExternalChanges.CheckAsync(fromAppOperation);

    private IEnumerable<PaletteEntry> FileGroupCommands()
    {
        yield return Command("View", "File groups", ShowToolCommand, Workbenches.FileGroups, icon: "Pin", detail: "Pinned sets of files for a feature");
        yield return Command("Window", "Pin open tabs as a file group…", FileGroups.AddOpenTabsCommand, icon: "Pin");
        yield return Command("Window", "Add file to a file group…", FileGroups.AddActiveCommand, icon: "Pin");
        foreach (var group in FileGroups.Groups)
        {
            yield return new PaletteEntry("Open file group: " + group.Name, group.Summary, () => FileGroups.OpenGroupCommand.ExecuteAsync(group), "File groups", Icon: "Pin");
            yield return new PaletteEntry("Switch to file group: " + group.Name, "Close other tabs, then open " + group.Summary, () => FileGroups.SwitchToGroupCommand.ExecuteAsync(group), "File groups", Icon: "Pin");
        }
    }
}
