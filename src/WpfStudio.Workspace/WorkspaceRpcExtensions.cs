using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public static class WorkspaceRpcExtensions
{
    /// <summary>
    /// Verifies every known closed document. One call reads a bounded part of a large workspace, so this continues until the
    /// pass ends, is not accepted or stops making progress. The result's change flag covers the whole pass.
    /// </summary>
    public static async Task<RefreshDiskDocumentsResult> RefreshAllDiskDocumentsAsync(this IWorkspaceRpc workspace, CancellationToken cancellationToken)
    {
        bool changed = false;
        while (true)
        {
            var result = await workspace.RefreshDiskDocumentsAsync(new(), cancellationToken).ConfigureAwait(false);
            changed |= result.Changed;
            if (!result.Accepted || result.PendingFiles == 0 || result.RefreshedFiles == 0) return result with { Changed = changed };
        }
    }
}
