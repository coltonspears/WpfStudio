using WpfStudio.Core.Git;

namespace WpfStudio.App.Features.Git;

/// <summary>
/// What the Git tools (history, conflict resolver) need from the view model that owns the repository: its state, and
/// its one busy-guarded way of running Git, so an action in one tool cannot overlap another's.
/// </summary>
public interface IGitOperationHost
{
    string? RepositoryRoot { get; }
    string Branch { get; }
    GitRemote? Remote { get; }
    /// <summary>A repository is open and no Git action is running.</summary>
    bool CanOperate { get; }
    /// <summary>Changes when another workspace opens; an answer for an older version is stale.</summary>
    int WorkspaceVersion { get; }
    void ReportStatus(string status);
    void ReportError(string error);
    /// <summary>Runs a read as the one Git action, reporting a failure; does nothing while another action runs.</summary>
    Task RunAsync(string label, Func<CancellationToken, Task> action, CancellationToken cancel);
    /// <summary>As <see cref="RunAsync"/> for an action that changes the repository: logs its output and refreshes afterwards.</summary>
    Task MutateAsync(string label, Func<CancellationToken, Task<string>> action, CancellationToken cancel, bool changesWorkspaceFiles);
    /// <summary>False when open documents could not be saved, so Git must not touch their files.</summary>
    Task<bool> SaveOpenDocumentsAsync(CancellationToken cancellationToken);
    void OpenFile(string path);
    void OpenUrl(string url);
    void ShowHistoryTab();
}
