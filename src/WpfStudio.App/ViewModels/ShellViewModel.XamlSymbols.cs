using System.IO;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

using WpfStudio.App.Features.Documents;
using WpfStudio.App.Features.XamlAnalysis;
using WpfStudio.App.Features.Workbench;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private long _symbolSemanticRevision;

    private Func<bool> CaptureSymbolContext(EditorViewModel editor, bool includeSemantic = true)
    {
        var workspace = Workspace;
        var configuration = Configuration;
        var source = editor.State;
        var selection = editor.SelectionRevision;
        var projectRevision = editor.XamlProjectRevision;
        var semanticRevision = Volatile.Read(ref _symbolSemanticRevision);
        var sourceEpoch = XamlAnalysis.SourceEpoch;
        var contextRevision = editor.XamlContextRevision;
        var connected = _workspace.IsConnected;
        var reloadRequired = XamlAnalysis.ProjectReloadRequired;
        var openEditors = Documents.ToArray();
        var buffers = _store.Documents.Select(document => (Document: document, document.Version)).ToArray();
        // Changed files being read is not a change in itself: when one did change, the semantic revision and source epoch say so.
        return () => !_disposed && ReferenceEquals(ActiveDocument, editor) && _store.Find(source.Path) == source
            && ReferenceEquals(Workspace, workspace) && Configuration == configuration
            && XamlAnalysis.ProjectReloadRequired == reloadRequired
            && editor.SelectionRevision == selection && editor.XamlProjectRevision == projectRevision
            && Documents.Count == openEditors.Length && openEditors.All(Documents.Contains)
            && buffers.All(buffer => _store.Find(buffer.Document.Path) == buffer.Document && buffer.Document.Version == buffer.Version)
            && (!includeSemantic || connected == _workspace.IsConnected && contextRevision == editor.XamlContextRevision
                && semanticRevision == Volatile.Read(ref _symbolSemanticRevision) && sourceEpoch == XamlAnalysis.SourceEpoch);
    }

    /// <summary>
    /// Brings the worker up to date with the open buffers and with source files changed on disk. A search can go ahead on
    /// an incomplete reading and say so, which is what the returned note is for; an edit cannot, so it throws instead.
    /// </summary>
    private async Task<string?> SynchronizeSymbolBuffersAsync(EditorViewModel editor, bool requireComplete = true)
    {
        // A save a moment ago is still being read. Waiting for that is quicker than asking the user to try again.
        if (XamlAnalysis.ProjectTypesPending)
        {
            Status = "Reading changed source files…";
            await XamlAnalysis.WaitForProjectTypesAsync(TimeSpan.FromSeconds(8), _lifetime.Token);
        }
        var current = CaptureSymbolContext(editor, includeSemantic: false);
        var synchronized = await _workspace.ReconcileNameProjectionsAsync(OpenLanguageBuffers(), _lifetime.Token);
        if (!synchronized.Accepted) throw new InvalidOperationException(synchronized.Status ?? "Editor buffers could not be synchronized.");
        if (!current()) throw new InvalidOperationException("The document or project context changed. Request the symbol operation again.");
        // Settle closed-file types and their public invalidation notifications
        // before capturing the semantic snapshot used by the query and review.
        var refresh = await _workspace.RefreshAllDiskDocumentsAsync(_lifetime.Token);
        await _dispatcher.InvokeAsync(() => { }, _lifetime.Token);
        if (!current()) throw new InvalidOperationException("The document or project context changed. Request the symbol operation again.");
        if (refresh.Accepted && !refresh.Truncated && refresh.PendingFiles == 0) return null;
        if (requireComplete)
            throw new InvalidOperationException(refresh.Status ?? "Some changed source files could not be read yet. Try again in a moment.");
        return refresh.Status ?? "Some changed source files could not be read yet, so results may be incomplete.";
    }

    private IReadOnlyList<XamlDocumentOverlay> SymbolXamlOverlays() => _xamlResources.Capture().Overlays;

    private UpdateDocumentRequest[] OpenLanguageBuffers() => Documents
        .Where(document => (document.IsCSharp || document.IsXaml) && !document.IsReadOnly)
        .Select(document => new UpdateDocumentRequest(document.State.Path, document.State.Content, document.State.Version, Analyze: false)).ToArray();

    private async Task FindLanguageSymbolReferencesAsync()
    {
        if (ActiveDocument is not { } editor || !editor.IsXaml && !editor.IsCSharp) return;
        if (!_workspace.IsConnected)
        {
            if (editor.IsXaml) FindResourceReferences();
            else Status = "Symbol references are unavailable while the language worker is disconnected.";
            return;
        }
        // A search reads and changes nothing, so it runs on the model that is loaded and says what that may leave out.
        var notes = new List<string>();
        if (XamlAnalysis.ProjectReloadRequired)
        {
            notes.Add(RefreshesProjectModel ? "Project files changed on disk and the project model is being refreshed; files added or removed since it was loaded are not covered yet."
                : "Project files changed on disk; files added or removed since the workspace was loaded are not covered until it is reloaded.");
            ScheduleProjectModelRefresh();
        }
        SymbolReferenceResult? result = null;
        // A file read from disk while the search ran outdates it. That settles by itself, so look again rather than hand the retry to the user.
        for (int attempt = 0; attempt < 3 && result is null; attempt++)
        {
            if (!ReferenceEquals(ActiveDocument, editor)) return;
            string? incomplete;
            try { incomplete = await SynchronizeSymbolBuffersAsync(editor, requireComplete: false); }
            // The project model was swapped, or a buffer changed, while the buffers were being synchronized.
            catch (InvalidOperationException) when (attempt < 2 && ReferenceEquals(ActiveDocument, editor)) { continue; }
            if (incomplete is not null && !notes.Contains(incomplete)) notes.Add(incomplete);
            var current = CaptureSymbolContext(editor);
            var state = editor.State;
            Status = "Finding references…";
            var found = await _workspace.FindSymbolReferencesAsync(new(state.Path, state.CaretOffset, state.Version,
                state.Content, editor.XamlProjectPath, SymbolXamlOverlays()), _lifetime.Token);
            if (current()) result = found;
        }
        if (result is null) { Status = "The document or its types kept changing. Find references again."; return; }
        if (notes.Count > 0) result = result with { Warnings = [.. notes, .. result.Warnings] };
        if (editor.IsXaml && !result.SymbolFound) { FindResourceReferences(result.Warnings); return; }
        PublishSymbolReferences(result);
    }

    internal void PublishSymbolReferences(SymbolReferenceResult result)
    {
        SearchResults.Clear();
        foreach (var location in result.Locations)
        {
            var item = new NavigationResult(location.Path, location.Line, location.Column, location.DisplayText ?? "Reference",
                location.Start, location.ExpectedTextHash, location.ProjectPath, location.ProjectName);
            SearchResults.Add(item);
        }
        ToolRequested?.Invoke(Workbenches.Search);
        Status = $"{result.Locations.Count} symbol reference(s)" + SymbolWarnings(result.Warnings);
    }

    private string SymbolWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return "";
        foreach (var warning in warnings) AppendOutput("Symbol coverage: " + warning);
        string first = warnings[0].Length <= 160 ? warnings[0] : warnings[0][..160] + "…";
        return " · Partial coverage: " + first + (warnings.Count > 1 ? $" (+{warnings.Count - 1} notes; see Output)" : "");
    }

    private void FindResourceReferences(IReadOnlyList<string>? warnings = null)
    {
        var declaration = ResourceAtCaret();
        SearchResults.Clear();
        if (declaration is null)
        {
            Status = "Place the caret on a resolved binding member, named element, event handler or resource key" + SymbolWarnings(warnings ?? []);
            return;
        }
        foreach (var usage in WpfExplorer.Index!.Usages.Where(usage => usage.ResolvedPath == declaration.Path && usage.ResolvedDeclarationStart == declaration.ValueStart))
            SearchResults.Add(new(usage.Path, usage.Line, 1, (usage.IsDynamic ? "DynamicResource " : "StaticResource ") + usage.Key));
        ToolRequested?.Invoke(Workbenches.Search);
        Status = $"{SearchResults.Count} resolved resource reference(s); runtime and code resource references are not indexed" + SymbolWarnings(warnings ?? []);
    }

    private async Task RenameLanguageSymbolAsync()
    {
        if (ActiveDocument is not { } editor || editor.IsReadOnly || !editor.IsXaml && !editor.IsCSharp) return;
        if (!_workspace.IsConnected)
        {
            if (editor.IsXaml) await RenameResourceAtCaretAsync();
            else Status = "Symbol rename is unavailable while the language worker is disconnected.";
            return;
        }
        // A rename edits every use, so it needs a project model that lists every file.
        if (XamlAnalysis.ProjectReloadRequired)
        {
            ScheduleProjectModelRefresh();
            Status = RefreshesProjectModel ? "Project files changed on disk. The project model is being refreshed; rename again when it has finished."
                : "Project files changed on disk. Reload the workspace before renaming symbols.";
            return;
        }
        await SynchronizeSymbolBuffersAsync(editor);
        var current = CaptureSymbolContext(editor);
        var state = editor.State;
        var overlays = SymbolXamlOverlays();
        if (editor.IsXaml)
        {
            var symbol = await _workspace.FindSymbolReferencesAsync(new(state.Path, state.CaretOffset, state.Version,
                state.Content, editor.XamlProjectPath, overlays), _lifetime.Token);
            if (!current()) throw new InvalidOperationException("The document or its types changed. Request rename again.");
            if (!symbol.SymbolFound) { await RenameResourceAtCaretAsync(symbol.Warnings); return; }
        }
        var name = await _dialogs.PromptAsync("Rename symbol", "New symbol name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!current()) throw new InvalidOperationException("The document or its types changed. Request rename again.");
        var result = await _workspace.RenameAsync(new(state.Path, state.CaretOffset, state.Version, name,
            state.Content, editor.XamlProjectPath, overlays), _lifetime.Token);
        if (!current()) throw new InvalidOperationException("The document or its types changed. Request rename again.");
        await PreviewSymbolRenameAsync(editor, result, current);
    }

    private async Task RenameResourceAtCaretAsync(IReadOnlyList<string>? warnings = null)
    {
        var declaration = ResourceAtCaret();
        if (declaration is null)
        {
            Status = "Place the caret on a resolved binding member, named element, event handler or resource key" + SymbolWarnings(warnings ?? []);
            return;
        }
        WpfExplorer.SelectedItem = WpfExplorer.Index!.Items.FirstOrDefault(item => item.Path == declaration.Path && item.Line == declaration.Line && item.Key == declaration.Key);
        await RenameResourceCommand.ExecuteAsync(null);
    }

    internal Task PreviewSymbolRenameAsync(EditorViewModel editor, WorkspaceEditResult result, Func<bool>? requestCurrent = null) =>
        _verifiedEdits.ReleasingOpenedTargetsAsync(result.Documents.Select(edit => edit.Path), () => PreviewSymbolRenameCoreAsync(editor, result, requestCurrent));

    private async Task PreviewSymbolRenameCoreAsync(EditorViewModel editor, WorkspaceEditResult result, Func<bool>? requestCurrent)
    {
        var snapshotCurrent = CaptureSymbolContext(editor);
        bool SourceCurrent() => snapshotCurrent() && (requestCurrent?.Invoke() ?? true) && !editor.IsReadOnly;
        if (!SourceCurrent()) throw new InvalidOperationException("The rename context changed. Request rename again.");
        if (result.Documents.Count == 0)
        {
            Status = "No symbol changes available" + SymbolWarnings(result.Warnings);
            return;
        }
        VerifiedEditPipeline.RequireVerifiedTargets(result.Documents, "Rename", "Request rename again.");
        foreach (var edit in result.Documents.Where(edit => edit.Edits.Count != 0)) EnsureCodeActionTargetEditable(edit.Path);
        var changes = new List<FileChange>();
        var targets = new List<Func<bool>>();
        foreach (var edit in result.Documents)
        {
            changes.AddRange(await _edits.PrepareAsync(new([edit], []), _lifetime.Token));
            var document = _store.Find(edit.Path) ?? throw new InvalidOperationException("A rename target was closed.");
            targets.Add(_verifiedEdits.Remember(document));
        }
        bool Current()
        {
            if (!SourceCurrent() || !targets.All(unchanged => unchanged())) return false;
            foreach (var change in changes.Where(change => change.Before != change.After)) EnsureCodeActionTargetEditable(change.Path);
            return true;
        }
        switch (await _verifiedEdits.ReviewAsync("Rename symbol", changes, string.Join(Environment.NewLine, result.Warnings), Current))
        {
            case VerifiedEditReview.Unchanged: Status = "No symbol changes required" + SymbolWarnings(result.Warnings); return;
            case VerifiedEditReview.Declined: return;
            case VerifiedEditReview.StaleBeforeReview or VerifiedEditReview.StaleAfterReview: throw new InvalidOperationException("The rename context changed. Request rename again.");
        }
        await _verifiedEdits.ApplyAsync(changes, Current, _lifetime.Token);
        var actualChanges = changes.Where(change => change.Before != change.After).ToArray();
        Status = $"Renamed symbol in {actualChanges.Length} file(s). Save All to write the changes." + SymbolWarnings(result.Warnings);
        if (result.NameProjection is { } plan)
        {
            try
            {
                // The reviewed edit is committed before its generated-field
                // projection. Cancellation of the review never reaches this RPC.
                var projection = await _workspace.ApplyXamlNameProjectionAsync(new(plan, OpenLanguageBuffers()), _lifetime.Token);
                if (!projection.Accepted) throw new InvalidOperationException(projection.Status ?? "The generated-name projection was rejected.");
                if (!string.IsNullOrWhiteSpace(projection.Status))
                {
                    AppendOutput("Generated-name projection: " + projection.Status);
                    Status += SymbolWarnings([projection.Status]);
                }
            }
            catch (Exception exception)
            {
                Status = "Rename applied in editor buffers; language-service refresh is pending.";
                AppendOutput("Rename applied; generated-name refresh: " + exception.Message);
            }
            return;
        }
        foreach (var changedEditor in Documents.Where(document => (document.IsCSharp || document.IsXaml) && actualChanges.Any(change =>
            change.Path.Equals(document.State.Path, StringComparison.OrdinalIgnoreCase))).ToArray())
        {
            try { await changedEditor.SyncAsync(_lifetime.Token); }
            catch (Exception exception)
            {
                Status = "Rename applied in editor buffers; language-service refresh is pending.";
                AppendOutput("Rename applied; language-service refresh: " + exception.Message);
            }
        }
    }

    private async Task NavigateSymbolResultAsync(NavigationResult result)
    {
        if (result.ExpectedTextHash is null)
        {
            await NavigateAsync(result.Path, result.Line, result.Column);
            return;
        }
        if (!File.Exists(result.Path) && _store.Find(result.Path) is null) { Status = "That file no longer exists. Find references again."; return; }
        RecordNavigationOrigin();
        await OpenDocumentAsync(result.Path);
        if (ActiveDocument is not { } editor || !editor.State.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase)) return;
        // The project the reference was found in is the context its XAML is read in, while that project is still loaded.
        if (editor.IsXaml && result.ProjectPath is { } projectPath
            && editor.XamlProjects.Where(project => project.ProjectPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase)).ToArray() is [var context])
            editor.XamlProject = context;
        if (ProjectXamlAnalysisCoordinator.TextHash(editor.State.Content) == result.ExpectedTextHash) { editor.Navigate(result.Start); return; }
        // The text is not what was searched. Its line is the best guess left, and going there beats refusing to go anywhere.
        editor.Navigate(OffsetOf(editor.State.Content, result.Line, result.Column));
        Status = "This file changed since the search, so the reference may have moved. Find references again for exact positions.";
    }

    /// <summary>The offset of a one-based line and column, clamped to the text.</summary>
    private static int OffsetOf(string text, int line, int column)
    {
        int offset = 0;
        for (int i = 1; i < line; i++) { int next = text.IndexOf('\n', offset); if (next < 0) break; offset = next + 1; }
        return Math.Min(text.Length, offset + Math.Max(0, column - 1));
    }
}
