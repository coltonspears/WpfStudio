using System.IO;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.Features.Documents;

/// <summary>What a verified edit needs from the shell: the change review and the open tabs.</summary>
public interface IVerifiedEditHost
{
    Task<bool> ReviewChangesAsync(string title, IReadOnlyList<FileChange> changes, string warnings);
    void ShowDocument(DocumentState document);
    bool IsDocumentOpen(DocumentState document);
}

/// <summary>Where a verified edit stands after its review.</summary>
public enum VerifiedEditReview { Accepted, Unchanged, Declined, StaleBeforeReview, StaleAfterReview }

/// <summary>
/// The shared steps of the edits whose source can change while they wait for a worker, a disk read or the user: rename, XAML
/// quick fix, designer edit, inspection source edit and formatting. This fixes when a guard is consulted. What makes an edit
/// stale differs per edit and stays with its caller, as the guard it passes in.
/// </summary>
public sealed class VerifiedEditPipeline(IVerifiedEditHost host, DocumentStore store, WorkspaceEditTransaction edits)
{
    /// <summary>Shows the actual changes for review, unless the edit is already stale or changes nothing, and checks the guard again on the way back.</summary>
    public async Task<VerifiedEditReview> ReviewAsync(string title, IReadOnlyList<FileChange> changes, string warnings, Func<bool> isCurrent)
    {
        if (!isCurrent()) return VerifiedEditReview.StaleBeforeReview;
        var changed = changes.Where(IsChange).ToArray();
        if (changed.Length == 0) return VerifiedEditReview.Unchanged;
        if (!await host.ReviewChangesAsync(title, changed, warnings)) return VerifiedEditReview.Declined;
        return isCurrent() ? VerifiedEditReview.Accepted : VerifiedEditReview.StaleAfterReview;
    }

    /// <summary>
    /// Applies under the guard, which the transaction consults after its asynchronous disk checks and immediately before
    /// changing a buffer, then opens what changed. Unchanged entries stay in the transaction as validated prerequisites.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<FileChange> changes, Func<bool> isCurrent, CancellationToken token)
    {
        await edits.ApplyAsync(changes, token, isCurrent);
        foreach (var change in changes.Where(IsChange)) host.ShowDocument(store.Find(change.Path)!);
    }

    /// <summary>A check that this buffer is still the same one with the same text. A changed-then-restored buffer or a closed and reopened tab is a new context.</summary>
    public Func<bool> Remember(DocumentState document)
    {
        var version = document.Version;
        var content = document.Content;
        return () => ReferenceEquals(store.Find(document.Path), document) && document.Version == version && document.Content == content;
    }

    /// <summary>Refuses a worker's edit that has no verified snapshot for a target, or two edits for one file.</summary>
    public static void RequireVerifiedTargets(IReadOnlyCollection<DocumentEdits> targets, string subject, string retry)
    {
        if (targets.Any(edit => string.IsNullOrEmpty(edit.ExpectedTextHash)))
            throw new InvalidOperationException($"{subject} has no verified source snapshot. {retry}");
        if (targets.Select(edit => Path.GetFullPath(edit.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new InvalidOperationException($"{subject} contains duplicate document targets. {retry}");
    }

    /// <summary>
    /// Preparing an edit opens closed files to validate them. Runs the edit, then closes the ones it opened and nobody kept,
    /// on success and on refusal, so they do not stay as user-owned buffers and a later request reads the latest disk bytes.
    /// </summary>
    public async Task ReleasingOpenedTargetsAsync(IEnumerable<string> targetPaths, Func<Task> edit)
    {
        var existing = store.Documents.ToHashSet();
        try { await edit(); }
        finally
        {
            var paths = targetPaths.Select(path => Path.GetFullPath(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var document in store.Documents.Where(document => !existing.Contains(document) && !document.IsDirty
                && paths.Contains(document.Path) && !host.IsDocumentOpen(document)).ToArray())
                if (ReferenceEquals(store.Find(document.Path), document)) store.Close(document);
        }
    }

    private static bool IsChange(FileChange change) => change.Before != change.After;
}
