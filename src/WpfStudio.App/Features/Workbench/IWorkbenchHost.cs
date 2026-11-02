using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Workbench;

/// <summary>
/// What the workbench tools (Task List, file groups) need from the shell, as a typed contract. Each tool is a view
/// model of its own that reads workspace facts and asks for navigation through this interface, instead of sharing the
/// shell's fields as a partial class.
/// </summary>
public interface IWorkbenchHost
{
    /// <summary>The open solution or project path; null when nothing is open.</summary>
    string? WorkspacePath { get; }
    /// <summary>Every source file of the workspace, as listed for Solution Explorer.</summary>
    IReadOnlyList<string> WorkspaceFiles { get; }
    /// <summary>Paths of the open documents, in tab order.</summary>
    IReadOnlyList<string> OpenDocumentPaths { get; }
    /// <summary>The editor text of each open document, keyed by path (case-insensitive).</summary>
    IReadOnlyDictionary<string, string> OpenDocumentTexts();
    /// <summary>The active document's path and caret line; null when no tab is active.</summary>
    (string Path, int Line)? ActiveLocation { get; }
    /// <summary>The file selected in Solution Explorer; null when the selection is not a file.</summary>
    string? SelectedExplorerFile { get; }
    IUserDialogService Dialogs { get; }
    /// <summary>Cancelled when the shell shuts down.</summary>
    CancellationToken Lifetime { get; }
    /// <summary>Runs an operation, reporting a failure in the status bar and Output instead of faulting the caller.</summary>
    Task GuardAsync(Func<Task> action);
    void ReportStatus(string status);
    Task NavigateAsync(string path, int line, int column);
    Task OpenDocumentAsync(string path);
    /// <summary>Closes the tab for <paramref name="path"/>, asking about unsaved edits; false when the user cancelled.</summary>
    Task<bool> CloseDocumentAsync(string path);
    void ShowTool(string toolId);
}
