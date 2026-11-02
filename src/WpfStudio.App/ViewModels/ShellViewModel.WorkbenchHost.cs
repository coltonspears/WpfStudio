using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

/// <summary>The shell as seen by the workbench tools (Task List, file groups).</summary>
public sealed partial class ShellViewModel : IWorkbenchHost
{
    public TaskListViewModel TaskList { get; }
    public FileGroupsViewModel FileGroups { get; }

    string? IWorkbenchHost.WorkspacePath => Workspace?.Path;
    IReadOnlyList<string> IWorkbenchHost.WorkspaceFiles => _allFiles;
    IReadOnlyList<string> IWorkbenchHost.OpenDocumentPaths => Documents.Select(d => d.State.Path).ToArray();
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
