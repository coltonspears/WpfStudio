using WpfStudio.Contracts;
using System.Text;

namespace WpfStudio.Core.Documents;

public sealed record FileChange(string Path, string Before, string After, string Summary);
public sealed class WorkspaceEditTransaction
{
    private readonly DocumentStore _store;
    private sealed record AppliedChange(FileChange Change, DocumentState Document, bool Created);
    private readonly Stack<IReadOnlyList<AppliedChange>> _undo = new();
    public WorkspaceEditTransaction(DocumentStore store) => _store = store;
    public bool CanUndo => _undo.Count != 0;
    public async Task<IReadOnlyList<FileChange>> PrepareAsync(WorkspaceEditResult result, CancellationToken token = default)
    {
        var changes = new List<FileChange>();
        foreach (var edit in result.Documents)
        {
            var doc = await _store.OpenAsync(edit.Path, token);
            if (edit.Version > 0 && doc.Version != edit.Version) throw new InvalidOperationException("A document changed while the edit was being computed. Try again.");
            if (edit.ExpectedTextHash != null && DocumentStore.Hash(Encoding.UTF8.GetBytes(doc.Content)) != edit.ExpectedTextHash)
                throw new InvalidOperationException($"{doc.Name} differs from the language-service snapshot. Reload the workspace and recompute the edit.");
            changes.Add(new FileChange(doc.Path, doc.Content, ApplyTextEdits(doc.Content, edit.Edits), $"{edit.Edits.Count} change(s)"));
        }
        return changes;
    }
    public async Task ApplyAsync(IReadOnlyList<FileChange> changes, CancellationToken token = default, Func<bool>? canApply = null)
    {
        var resolved = new List<(DocumentState? Document, FileChange Change)>();
        if (changes.Select(c => System.IO.Path.GetFullPath(c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count)
            throw new InvalidOperationException("A workspace transaction cannot contain duplicate file paths.");
        foreach (var change in changes)
        {
            var doc = _store.Find(change.Path) ?? (File.Exists(change.Path) ? await _store.OpenAsync(change.Path, token) : null);
            if (doc == null)
            {
                if (change.Before.Length != 0) throw new ExternalFileChangedException(change.Path);
                resolved.Add((null, change));
                continue;
            }
            if (doc.Content != change.Before) throw new InvalidOperationException($"{doc.Name} changed after the preview. Recompute the edit.");
            if (doc.DiskHash != null && await _store.IsExternallyChangedAsync(doc, token)) throw new ExternalFileChangedException(doc.Path);
            resolved.Add((doc, change));
        }
        // Earlier buffers may change while later files are checked asynchronously.
        // Revalidate the complete transaction synchronously before changing any buffer.
        token.ThrowIfCancellationRequested();
        foreach (var item in resolved)
        {
            if (item.Document != null && item.Document.Content != item.Change.Before) throw new InvalidOperationException($"{item.Document.Name} changed after the preview. Recompute the edit.");
            if (item.Document == null && (_store.Find(item.Change.Path) != null || File.Exists(item.Change.Path))) throw new ExternalFileChangedException(item.Change.Path);
        }
        if (canApply is not null && !canApply())
            throw new InvalidOperationException("The context for this reviewed edit changed. Recompute the edit.");
        var applied = new List<AppliedChange>();
        foreach (var item in resolved)
        {
            // An unchanged document can be a prerequisite for a cross-file action.
            // Check it above, but do not turn it into an edit or an undo dependency.
            if (item.Document is not null && item.Change.Before == item.Change.After) continue;
            var document = item.Document ?? _store.Create(item.Change.Path);
            document.Content = item.Change.After;
            applied.Add(new(item.Change, document, item.Document == null));
        }
        if (applied.Count > 0) _undo.Push(applied);
    }
    /// <summary>Restores buffers and returns newly-created, never-saved documents removed from the store.</summary>
    public IReadOnlyList<string> Undo()
    {
        if (!_undo.TryPeek(out var changes)) return [];
        foreach (var applied in changes)
            if (_store.Find(applied.Change.Path)?.Content != applied.Change.After ||
                (applied.Created && !ReferenceEquals(_store.Find(applied.Change.Path), applied.Document)))
                throw new InvalidOperationException("A changed document has newer edits. Undo those edits before undoing the workspace operation.");
        var removed = new List<string>();
        foreach (var applied in changes)
        {
            var document = _store.Find(applied.Change.Path)!;
            if (applied.Created && document.DiskHash == null && !File.Exists(document.Path))
            {
                _store.Close(document);
                _store.DeleteRecovery(document.Path);
                removed.Add(document.Path);
            }
            else document.Content = applied.Change.Before;
        }
        _undo.Pop();
        return removed;
    }
    public static string ApplyTextEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0) return text;
        var ordered = edits.OrderByDescending(edit => edit.Start).ToArray();
        var lastStart = text.Length;
        foreach (var edit in ordered)
        {
            if (edit.Start < 0 || edit.Start > lastStart || edit.Length < 0 || edit.Length > lastStart - edit.Start || edit.NewText is null)
                throw new InvalidOperationException("Overlapping or invalid edits received.");
            lastStart = edit.Start;
        }
        // Formatting can contain thousands of trivia edits. Copy each unchanged
        // range once, retaining the previous ordering for same-offset insertions.
        var output = new StringBuilder(text.Length);
        int cursor = 0;
        for (int index = ordered.Length - 1; index >= 0; index--)
        {
            var edit = ordered[index];
            output.Append(text, cursor, edit.Start - cursor);
            output.Append(edit.NewText);
            cursor = edit.Start + edit.Length;
        }
        output.Append(text, cursor, text.Length - cursor);
        return output.ToString();
    }
}
