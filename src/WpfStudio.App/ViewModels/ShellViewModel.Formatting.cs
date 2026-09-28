using System.IO;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private Func<bool> CaptureFormattingContext(EditorViewModel editor, bool usesSemanticContext)
    {
        var state = editor.State;
        var version = state.Version;
        var text = state.Content;
        var selectionRevision = editor.SelectionRevision;
        var projectRevision = editor.XamlProjectRevision;
        var workspace = Workspace;
        var configuration = Configuration;
        var semanticRevision = Volatile.Read(ref _symbolSemanticRevision);
        var sourceEpoch = Volatile.Read(ref _diagnosticSourceEpoch);
        var connected = _workspace.IsConnected;
        return () => !_disposed && ReferenceEquals(ActiveDocument, editor) && Documents.Contains(editor)
            && ReferenceEquals(_store.Find(state.Path), state) && !editor.IsReadOnly
            && state.Version == version && state.Content == text && editor.SelectionRevision == selectionRevision
            && editor.XamlProjectRevision == projectRevision && ReferenceEquals(Workspace, workspace) && Configuration == configuration
            && (!usesSemanticContext || !_projectReloadRequired && !_projectTypesPending
                && connected == _workspace.IsConnected && semanticRevision == Volatile.Read(ref _symbolSemanticRevision)
                && sourceEpoch == Volatile.Read(ref _diagnosticSourceEpoch));
    }

    private async Task FormatActiveDocumentAsync()
    {
        if (ActiveDocument is not { } editor) return;
        if (editor.IsReadOnly) { Status = "This document is read-only."; return; }
        if (!editor.IsXaml && !editor.IsCSharp) { Status = "Format document is available for C# and XAML."; return; }
        EnsureCodeActionTargetEditable(editor.State.Path);
        if (editor.IsCSharp)
        {
            if (!_workspace.IsConnected) { Status = "C# formatting is unavailable while the language worker is disconnected."; return; }
            var current = CaptureFormattingContext(editor, usesSemanticContext: false);
            await editor.SyncAsync(_lifetime.Token);
            await _dispatcher.InvokeAsync(() => { }, _lifetime.Token);
            if (!current()) throw new InvalidOperationException("The document or selection changed. Request formatting again.");
            var request = new DocumentRequest(editor.State.Path, editor.State.Version);
            await FormatEditorAsync(editor, token => _workspace.FormatDocumentAsync(request, token), usesSemanticContext: true);
            return;
        }
        var inputsCurrent = CaptureFormattingContext(editor, usesSemanticContext: false);
        bool hadProjectContext = Workspace is not null || editor.XamlProjects.Count > 0 || _workspace.IsConnected;
        bool useProjectTypes = _workspace.IsConnected && !_projectReloadRequired && !_projectTypesPending;
        string? fallbackReason = useProjectTypes ? null : "Project types are unavailable; conservative XAML formatting was used.";
        if (useProjectTypes)
        {
            try { await SynchronizeSymbolBuffersAsync(editor); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                if (!inputsCurrent()) throw new InvalidOperationException("The document or formatting context changed. Request formatting again.");
                useProjectTypes = false;
                fallbackReason = "Project types could not be refreshed; conservative XAML formatting was used.";
                AppendOutput("Formatting type refresh: " + exception.Message);
            }
        }
        if (!inputsCurrent()) throw new InvalidOperationException("The document or formatting context changed. Request formatting again.");
        if (useProjectTypes && (!_workspace.IsConnected || _projectReloadRequired || _projectTypesPending))
        {
            useProjectTypes = false;
            fallbackReason = "Project types are unavailable; conservative XAML formatting was used.";
        }
        XamlFormattingOptions? options = null;
        if (!useProjectTypes && hadProjectContext)
        {
            options = new(UseKnownFrameworkContent: false);
            fallbackReason = "Project content metadata unavailable; content spacing preserved.";
        }
        var xamlRequest = new XamlFormattingRequest(editor.State.Path, editor.State.Content, editor.State.Version, editor.XamlProjectPath, options);
        if (useProjectTypes)
            await FormatEditorAsync(editor, token => _workspace.FormatXamlAsync(xamlRequest, token), usesSemanticContext: true);
        else
            await FormatEditorAsync(editor, token => Task.Run(() =>
            {
                var result = new XamlFormattingService().Format(xamlRequest.Text, options: xamlRequest.Options, token: token);
                return new WorkspaceEditResult(result.Accepted
                    ? [new DocumentEdits(xamlRequest.Path, xamlRequest.Version, result.Edits, TextHash(xamlRequest.Text))] : [],
                    fallbackReason is null ? result.Warnings : result.Accepted
                        ? [fallbackReason, .. result.Warnings] : [.. result.Warnings, fallbackReason]);
            }, token));
    }

    internal async Task FormatEditorAsync(EditorViewModel editor, Func<CancellationToken, Task<WorkspaceEditResult>> formatter,
        bool usesSemanticContext = false)
    {
        var current = CaptureFormattingContext(editor, usesSemanticContext);
        var state = editor.State;
        int caret = state.CaretOffset, start = editor.SelectionStart, length = editor.SelectionLength;
        if (!current()) throw new InvalidOperationException("The formatting context changed. Request formatting again.");
        EnsureCodeActionTargetEditable(state.Path);
        var result = await formatter(_lifetime.Token);
        if (!current()) throw new InvalidOperationException("The document or formatting context changed. Request formatting again.");
        var warnings = FormattingWarningSummary(result.Warnings);
        if (result.Documents.Count == 0)
        {
            Status = warnings.Length == 0 ? "No formatting changes required." : "Formatting skipped: " + warnings;
            foreach (var warning in result.Warnings) AppendOutput("Formatting: " + warning);
            return;
        }
        if (result.Documents.Count != 1 || result.Documents[0] is not { } edit
            || !Path.GetFullPath(edit.Path).Equals(state.Path, StringComparison.OrdinalIgnoreCase)
            || edit.Version != state.Version || string.IsNullOrEmpty(edit.ExpectedTextHash))
            throw new InvalidOperationException("Formatting returned an unverified document snapshot. Request formatting again.");
        var changes = await _edits.PrepareAsync(result, _lifetime.Token);
        bool CanApply()
        {
            if (!current()) return false;
            EnsureCodeActionTargetEditable(state.Path);
            return true;
        }
        // Map original positions through the formatter's minimal edits before
        // applying. Buffer synchronization may update the editor selection.
        int mappedCaret = MapFormattingOffset(caret, edit.Edits, rightAffinity: true);
        int mappedStart = length == 0 ? mappedCaret : MapFormattingOffset(start, edit.Edits, rightAffinity: true);
        int mappedEnd = length == 0 ? mappedCaret : MapFormattingOffset(start + length, edit.Edits, rightAffinity: false);
        await _edits.ApplyAsync(changes, _lifetime.Token, CanApply);
        if (changes.Any(change => change.Before != change.After))
        {
            editor.RestoreSelection(mappedCaret, mappedStart, Math.Max(0, mappedEnd - mappedStart));
            Status = "Document formatted. Changes are in the editor buffer.";
        }
        else Status = "Document is already formatted.";
        if (warnings.Length != 0) Status += " " + warnings;
        foreach (var warning in result.Warnings) AppendOutput("Formatting: " + warning);
    }

    private static string FormattingWarningSummary(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return "";
        string first = warnings[0];
        return (first.Length > 160 ? first[..160] + "…" : first)
            + (first.Length > 160 || warnings.Count > 1 ? " See Output for details." : "");
    }

    private static int MapFormattingOffset(int offset, IReadOnlyList<TextEdit> edits, bool rightAffinity)
    {
        int shift = 0;
        foreach (var edit in edits.OrderBy(edit => edit.Start))
        {
            if (offset < edit.Start || offset == edit.Start && edit.Length == 0 && !rightAffinity) break;
            if (offset >= edit.Start + edit.Length)
            {
                shift += edit.NewText.Length - edit.Length;
                continue;
            }
            return edit.Start + shift + Math.Min(offset - edit.Start, edit.NewText.Length);
        }
        return offset + shift;
    }
}
