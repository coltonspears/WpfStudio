using System.IO;
using WpfStudio.App.Features.Documents;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    // Resource prerequisites are observations, not user-open buffers.
    internal Task ApplyXamlCodeActionAsync(EditorViewModel editor, XamlCodeAction action) =>
        _verifiedEdits.ReleasingOpenedTargetsAsync(new[] { action.Edit }.Concat(action.AdditionalEdits ?? []).Select(edit => edit.Path), () => ApplyXamlCodeActionCoreAsync(editor, action));

    private async Task ApplyXamlCodeActionCoreAsync(EditorViewModel editor, XamlCodeAction action)
    {
        var state = editor.State;
        var version = state.Version; var content = state.Content;
        var contextRevision = editor.XamlContextRevision; var selectionRevision = editor.QuickFixContextRevision;
        var caret = state.CaretOffset; var selectionStart = editor.SelectionStart; var selectionLength = editor.SelectionLength;
        var additional = action.AdditionalEdits ?? [];
        var edits = new[] { action.Edit }.Concat(additional).ToArray();
        bool SourceCurrent() => !_disposed && ReferenceEquals(ActiveDocument, editor) && Documents.Contains(editor)
            && _store.Find(state.Path) == state && !editor.IsReadOnly && state.Version == version && state.Content == content
            && state.CaretOffset == caret && editor.SelectionStart == selectionStart && editor.SelectionLength == selectionLength
            && editor.QuickFixContextRevision == selectionRevision && editor.XamlContextRevision == contextRevision;
        if (!SourceCurrent() || !editor.IsXaml || action.Edit.Version != version ||
            !Path.GetFullPath(action.Edit.Path).Equals(state.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The XAML document or quick-fix context changed. Request the quick fix again.");
        VerifiedEditPipeline.RequireVerifiedTargets(edits, "The quick fix", "Request it again.");
        if (additional.Any(edit => !(Path.GetExtension(edit.Path).Equals(".xaml", StringComparison.OrdinalIgnoreCase) && edit.Edits.Count == 0)
            && !(Path.GetExtension(edit.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(edit.Path))))
            throw new InvalidOperationException("Additional quick-fix targets must be existing C# source files or unchanged XAML resource prerequisites.");
        foreach (var edit in edits.Where(edit => edit.Edits.Count != 0)) EnsureCodeActionTargetEditable(edit.Path);
        var openedTargets = edits.Select(edit => _store.Find(edit.Path)).OfType<DocumentState>().Select(_verifiedEdits.Remember).ToArray();

        var changes = await _edits.PrepareAsync(new WorkspaceEditResult(edits, []), _lifetime.Token);
        // Capture every prepared buffer, including newly opened code-behind. A
        // changed-then-restored buffer or closed/reopened tab is still a new context.
        var targets = edits.Select(edit =>
        {
            var document = _store.Find(edit.Path) ?? throw new InvalidOperationException("A quick-fix target was closed.");
            if (edit.Version > 0 && document.Version != edit.Version)
                throw new InvalidOperationException("A target document changed while the quick fix was prepared. Request it again.");
            return _verifiedEdits.Remember(document);
        }).ToArray();
        bool Current()
        {
            if (!SourceCurrent() || !openedTargets.All(unchanged => unchanged()) || !targets.All(unchanged => unchanged())) return false;
            foreach (var edit in edits.Where(edit => edit.Edits.Count != 0)) EnsureCodeActionTargetEditable(edit.Path);
            return true;
        }
        if (!Current()) throw new InvalidOperationException("The document, selection or XAML project context changed. Request the quick fix again.");
        // No-op XAML remains a version/hash/disk prerequisite. The transaction
        // validates it but records only actual mutations in the shared undo entry.
        await _verifiedEdits.ApplyAsync(changes, Current, _lifetime.Token);

        var navigation = additional.FirstOrDefault(edit => changes.Any(change => change.Path.Equals(Path.GetFullPath(edit.Path),
            StringComparison.OrdinalIgnoreCase) && change.Before != change.After));
        if (navigation is not null && Documents.FirstOrDefault(document => document.State.Path.Equals(Path.GetFullPath(navigation.Path),
            StringComparison.OrdinalIgnoreCase)) is { } targetEditor)
        {
            ActiveDocument = targetEditor;
            if (navigation.Edits.OrderBy(edit => edit.Start).FirstOrDefault(edit => !string.IsNullOrWhiteSpace(edit.NewText)) is { } insertion)
                targetEditor.Navigate(insertion.Start + insertion.NewText.TakeWhile(char.IsWhiteSpace).Count());
        }
        Status = action.Title;
        foreach (var change in changes.Where(change => change.Before != change.After && Path.GetExtension(change.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var changedEditor = Documents.FirstOrDefault(document => document.State.Path.Equals(change.Path, StringComparison.OrdinalIgnoreCase));
            if (changedEditor is null) continue;
            try { await changedEditor.SyncAsync(_lifetime.Token); }
            catch (Exception exception)
            {
                Status = action.Title + ". Applied in the editor; language-service refresh is pending.";
                AppendOutput("Handler created; language-service refresh: " + exception.Message);
            }
        }
    }

    private void EnsureCodeActionTargetEditable(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Contains(Path.Combine("WpfStudio", "GeneratedSources"), StringComparison.OrdinalIgnoreCase) ||
            Documents.Any(document => document.IsReadOnly && document.State.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ||
            Workspace?.Projects.Any(project => project.Files.Any(file => file.IsGenerated && file.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) == true ||
            File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new InvalidOperationException($"{Path.GetFileName(path)} is read-only. A workspace edit cannot change that file.");
    }
}
