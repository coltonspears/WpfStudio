using System.IO;
using System.Runtime.CompilerServices;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private long _symbolSemanticRevision;
    private sealed record SymbolReferenceOrigin(WorkspaceSnapshot? Workspace);
    private readonly ConditionalWeakTable<NavigationResult, SymbolReferenceOrigin> _symbolReferenceOrigins = new();

    private Func<bool> CaptureSymbolContext(EditorViewModel editor, bool includeSemantic = true)
    {
        var workspace = Workspace;
        var configuration = Configuration;
        var source = editor.State;
        var selection = editor.SelectionRevision;
        var projectRevision = editor.XamlProjectRevision;
        var semanticRevision = Volatile.Read(ref _symbolSemanticRevision);
        var sourceEpoch = Volatile.Read(ref _diagnosticSourceEpoch);
        var contextRevision = editor.XamlContextRevision;
        var connected = _workspace.IsConnected;
        var reloadRequired = _projectReloadRequired;
        var typesPending = _projectTypesPending;
        var openEditors = Documents.ToArray();
        var buffers = _store.Documents.Select(document => (Document: document, document.Version)).ToArray();
        return () => !_disposed && ReferenceEquals(ActiveDocument, editor) && _store.Find(source.Path) == source
            && ReferenceEquals(Workspace, workspace) && Configuration == configuration
            && _projectReloadRequired == reloadRequired && _projectTypesPending == typesPending
            && editor.SelectionRevision == selection && editor.XamlProjectRevision == projectRevision
            && Documents.Count == openEditors.Length && openEditors.All(Documents.Contains)
            && buffers.All(buffer => _store.Find(buffer.Document.Path) == buffer.Document && buffer.Document.Version == buffer.Version)
            && (!includeSemantic || connected == _workspace.IsConnected && contextRevision == editor.XamlContextRevision
                && semanticRevision == Volatile.Read(ref _symbolSemanticRevision) && sourceEpoch == Volatile.Read(ref _diagnosticSourceEpoch));
    }

    private async Task SynchronizeSymbolBuffersAsync(EditorViewModel editor)
    {
        var current = CaptureSymbolContext(editor, includeSemantic: false);
        var synchronized = await _workspace.ReconcileNameProjectionsAsync(OpenLanguageBuffers(), _lifetime.Token);
        if (!synchronized.Accepted) throw new InvalidOperationException(synchronized.Status ?? "Editor buffers could not be synchronized.");
        if (!current()) throw new InvalidOperationException("The document or project context changed. Request the symbol operation again.");
        // Settle closed-file types and their public invalidation notifications
        // before capturing the semantic snapshot used by the query and review.
        var refresh = await _workspace.RefreshDiskDocumentsAsync(new(), _lifetime.Token);
        await _dispatcher.InvokeAsync(() => { }, _lifetime.Token);
        if (!current()) throw new InvalidOperationException("The document or project context changed. Request the symbol operation again.");
        if (!refresh.Accepted || refresh.Truncated || refresh.PendingFiles != 0)
            throw new InvalidOperationException(refresh.Status ?? "Project source refresh is incomplete. Refresh or reload the workspace before finding or renaming symbols.");
    }

    private IReadOnlyList<XamlDocumentOverlay> SymbolXamlOverlays() => _xamlResources.Capture().Overlays;

    private UpdateDocumentRequest[] OpenLanguageBuffers() => Documents
        .Where(document => (document.IsCSharp || document.IsXaml) && !document.IsReadOnly)
        .Select(document => new UpdateDocumentRequest(document.State.Path, document.State.Content, document.State.Version, Analyze: false)).ToArray();

    private bool CanQueryLanguageSymbols()
    {
        if (_projectReloadRequired || _projectTypesPending)
        {
            Status = "Project sources changed. Refresh or reload the workspace before finding or renaming symbols.";
            return false;
        }
        return true;
    }

    private async Task FindLanguageSymbolReferencesAsync()
    {
        if (ActiveDocument is not { } editor || !editor.IsXaml && !editor.IsCSharp) return;
        if (!_workspace.IsConnected)
        {
            if (editor.IsXaml) FindResourceReferences();
            else Status = "Symbol references are unavailable while the language worker is disconnected.";
            return;
        }
        if (!CanQueryLanguageSymbols()) return;
        await SynchronizeSymbolBuffersAsync(editor);
        var current = CaptureSymbolContext(editor);
        var state = editor.State;
        var result = await _workspace.FindSymbolReferencesAsync(new(state.Path, state.CaretOffset, state.Version,
            state.Content, editor.XamlProjectPath, SymbolXamlOverlays()), _lifetime.Token);
        if (!current()) { Status = "The document or its types changed. Find references again."; return; }
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
            _symbolReferenceOrigins.Add(item, new(Workspace));
        }
        ToolRequested?.Invoke("Search");
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
        foreach (var usage in _wpfIndex!.Usages.Where(usage => usage.ResolvedPath == declaration.Path && usage.ResolvedDeclarationStart == declaration.ValueStart))
            SearchResults.Add(new(usage.Path, usage.Line, 1, (usage.IsDynamic ? "DynamicResource " : "StaticResource ") + usage.Key));
        ToolRequested?.Invoke("Search");
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
        if (!CanQueryLanguageSymbols()) return;
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
        SelectedWpfItem = _wpfIndex!.Items.FirstOrDefault(item => item.Path == declaration.Path && item.Line == declaration.Line && item.Key == declaration.Key);
        await RenameResourceCommand.ExecuteAsync(null);
    }

    internal async Task PreviewSymbolRenameAsync(EditorViewModel editor, WorkspaceEditResult result, Func<bool>? requestCurrent = null)
    {
        var existing = _store.Documents.ToHashSet();
        try { await PreviewSymbolRenameCoreAsync(editor, result, requestCurrent); }
        finally
        {
            // Preparing a diff opens closed files for validation. Do not retain
            // clean, invisible snapshots as if they were user-owned buffers.
            foreach (var document in _store.Documents.Where(document => !existing.Contains(document) && !document.IsDirty
                && !Documents.Any(open => ReferenceEquals(open.State, document))
                && result.Documents.Any(edit => Path.GetFullPath(edit.Path).Equals(document.Path, StringComparison.OrdinalIgnoreCase))).ToArray())
                if (ReferenceEquals(_store.Find(document.Path), document)) _store.Close(document);
        }
    }

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
        if (result.Documents.Any(edit => string.IsNullOrEmpty(edit.ExpectedTextHash)))
            throw new InvalidOperationException("Rename has no verified source snapshot. Request rename again.");
        if (result.Documents.Select(edit => Path.GetFullPath(edit.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Documents.Count)
            throw new InvalidOperationException("Rename contains duplicate document targets. Request rename again.");
        foreach (var edit in result.Documents.Where(edit => edit.Edits.Count != 0)) EnsureCodeActionTargetEditable(edit.Path);
        var changes = new List<FileChange>();
        var targets = new List<(DocumentState Document, long Version, string Content)>();
        foreach (var edit in result.Documents)
        {
            changes.AddRange(await _edits.PrepareAsync(new([edit], []), _lifetime.Token));
            var document = _store.Find(edit.Path) ?? throw new InvalidOperationException("A rename target was closed.");
            targets.Add((document, document.Version, document.Content));
        }
        bool Current()
        {
            if (!SourceCurrent() || targets.Any(target => _store.Find(target.Document.Path) != target.Document
                || target.Document.Version != target.Version || target.Document.Content != target.Content)) return false;
            foreach (var change in changes.Where(change => change.Before != change.After)) EnsureCodeActionTargetEditable(change.Path);
            return true;
        }
        if (!Current()) throw new InvalidOperationException("The rename context changed. Request rename again.");
        var actualChanges = changes.Where(change => change.Before != change.After).ToArray();
        if (actualChanges.Length == 0) { Status = "No symbol changes required" + SymbolWarnings(result.Warnings); return; }
        if (!await PreviewAsync("Rename symbol", actualChanges, string.Join(Environment.NewLine, result.Warnings))) return;
        await _edits.ApplyAsync(changes, _lifetime.Token, Current);
        foreach (var change in actualChanges) AddDocument(_store.Find(change.Path)!);
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
        bool WorkspaceCurrent() => !_disposed && !_projectReloadRequired && !_projectTypesPending
            && (!_symbolReferenceOrigins.TryGetValue(result, out var origin) || ReferenceEquals(origin.Workspace, Workspace));
        var workspace = Workspace;
        var text = _store.Find(result.Path)?.Content ?? await ProjectXamlDiagnosticText.ReadAsync(result.Path, _lifetime.Token);
        if (!WorkspaceCurrent() || !ReferenceEquals(workspace, Workspace) || text is null || TextHash(text) != result.ExpectedTextHash)
        { Status = "The reference source or project changed. Find references again before navigating."; return; }
        await OpenDocumentAsync(result.Path);
        if (ActiveDocument is not { } editor || !editor.State.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase)
            || !WorkspaceCurrent() || !ReferenceEquals(workspace, Workspace) || TextHash(editor.State.Content) != result.ExpectedTextHash)
        { Status = "The reference source or project changed. Find references again before navigating."; return; }
        if (editor.IsXaml && result.ProjectPath is { } projectPath)
        {
            var projects = editor.XamlProjects.Where(project => project.ProjectPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (projects.Length != 1) { Status = "The reference project context is unavailable. Reload the workspace and find references again."; return; }
            editor.XamlProject = projects[0];
        }
        editor.Navigate(result.Start);
    }
}
