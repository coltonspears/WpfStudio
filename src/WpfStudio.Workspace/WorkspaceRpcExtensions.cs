using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public static class WorkspaceRpcExtensions
{
    /// <summary>
    /// Verifies every known closed document. One call reads a bounded part of a large workspace, so this continues until the
    /// pass ends, is not accepted or stops making progress. The result's change flag covers the whole pass.
    /// </summary>
    public static Task<RefreshDiskDocumentsResult> RefreshAllDiskDocumentsAsync(this IWorkspaceRpc workspace, CancellationToken cancellationToken) =>
        workspace.RefreshAllDiskDocumentsAsync(partChanged: null, cancellationToken);

    /// <summary>
    /// As above, for a caller that reports changes itself. <paramref name="partChanged"/> runs after each part that changed
    /// something, because that part is applied even if a later one fails or is cancelled and the pass returns nothing.
    /// </summary>
    public static async Task<RefreshDiskDocumentsResult> RefreshAllDiskDocumentsAsync(this IWorkspaceRpc workspace, Action? partChanged, CancellationToken cancellationToken)
    {
        bool changed = false;
        while (true)
        {
            var result = await workspace.RefreshDiskDocumentsAsync(new(), cancellationToken).ConfigureAwait(false);
            if (result.Changed) { changed = true; partChanged?.Invoke(); }
            if (!result.Accepted || result.PendingFiles == 0 || result.RefreshedFiles == 0) return result with { Changed = changed };
        }
    }
}
