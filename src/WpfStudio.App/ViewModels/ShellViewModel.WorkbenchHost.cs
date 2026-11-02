using WpfStudio.App.Features.Documents;
using WpfStudio.App.Features.Review;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

/// <summary>The shell as seen by the tools that were split out of it: Task List, file groups, Problems and the external-changes review.</summary>
public sealed partial class ShellViewModel : ICommandPaletteHost, IExternalChangesHost
{
    public TaskListViewModel TaskList { get; }
    public FileGroupsViewModel FileGroups { get; }
    public ProblemsViewModel Problems { get; }
    public ExternalChangesViewModel ExternalChanges { get; }
    public CommandPaletteViewModel Palette { get; }
    public ChangeReviewViewModel ChangeReview { get; }
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
    string? IWorkbenchHost.SelectedExplorerFile => SelectedNode is { Kind: ExplorerNodeKind.File, Path.Length: > 0 } node ? node.Path : null;
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
        RequestProjectXamlAnalysis();
        // A commit or checkout in the terminal changes what change margins and blame compare with.
        if (Features?.GitService is { } git) foreach (var document in Documents) _ = document.RefreshGitAsync(git, _lifetime.Token);
    }
    void IExternalChangesHost.SettingChanged() { if (_initialized) _ = SaveSettingsAsync(); }

    /// <summary>Compares open files with disk; see <see cref="ExternalChangesViewModel.CheckAsync"/>.</summary>
    public Task CheckExternalChangesAsync(bool fromAppOperation = false) => ExternalChanges.CheckAsync(fromAppOperation);

    private IEnumerable<PaletteEntry> FileGroupCommands()
    {
        yield return Command("View", "File groups", ShowToolCommand, FileGroupsViewModel.ToolId, icon: "Pin", detail: "Pinned sets of files for a feature");
        yield return Command("Window", "Pin open tabs as a file group…", FileGroups.AddOpenTabsCommand, icon: "Pin");
        yield return Command("Window", "Add file to a file group…", FileGroups.AddActiveCommand, icon: "Pin");
        foreach (var group in FileGroups.Groups)
        {
            yield return new PaletteEntry("Open file group: " + group.Name, group.Summary, () => FileGroups.OpenGroupCommand.ExecuteAsync(group), "File groups", Icon: "Pin");
            yield return new PaletteEntry("Switch to file group: " + group.Name, "Close other tabs, then open " + group.Summary, () => FileGroups.SwitchToGroupCommand.ExecuteAsync(group), "File groups", Icon: "Pin");
        }
    }
}
