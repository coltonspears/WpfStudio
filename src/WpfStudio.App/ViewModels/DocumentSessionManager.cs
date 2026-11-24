using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using WpfStudio.App.Features.Debugging;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Documents;
using WpfStudio.App.Features.WpfExplorer;
using WpfStudio.App.Features.XamlAnalysis;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Workspace;

namespace WpfStudio.App.ViewModels;

internal interface IDocumentSessionHost
{
    ObservableCollection<EditorViewModel> Documents { get; }
    EditorViewModel? ActiveDocument { get; set; }
    string Status { get; set; }
    bool ShowInlayHints { get; }
    bool ShowInlineBlame { get; }
    bool ShowCodeLens { get; }
    bool ShowQuickInfo { get; }
    bool IsNavigatingBindingSource { get; }
    bool IsRevealingDesignerSelection { get; }
    CancellationToken Lifetime { get; }
    DocumentStore Store { get; }
    WorkspaceClient WorkspaceClient { get; }
    XamlCompletionService XamlCompletion { get; }
    XamlResourceContext XamlResources { get; }
    IUiDispatcher Dispatcher { get; }
    IUserDialogService Dialogs { get; }
    IMessenger Messenger { get; }
    ILogger Logger { get; }
    EditorPreferences Editor { get; }
    PerfLog Performance { get; }
    CodeAssistPreferences CodeAssist { get; }
    DebuggerViewModel Debugger { get; }
    DesignerViewModel Designer { get; }
    StudioFeatures Features { get; }
    AutoSaveCoordinator AutoSave { get; }
    ExternalChangesViewModel ExternalChanges { get; }
    BreakpointEditingViewModel Breakpoints { get; }
    ProjectXamlAnalysisCoordinator XamlAnalysis { get; }
    WpfExplorerViewModel WpfExplorer { get; }
    IAsyncRelayCommand FindReferencesCommand { get; }
    IAsyncRelayCommand OpenGitCommand { get; }
    Task GuardAsync(Func<Task> action);
    void AppendOutput(string line);
    Task ApplyXamlCodeActionAsync(EditorViewModel editor, XamlCodeAction action);
    Task ApplyCSharpCodeActionAsync(EditorViewModel editor, CodeActionEntry action);
    void ConfigureXamlContext(EditorViewModel editor);
    Task HandleEditorActionAsync(EditorViewModel editor, EditorAction action);
    void UpdateAssistantContext();
    void RefreshEditorDiagnostics();
    void RefreshRelated(EditorViewModel editor);
    void NotifyWelcomeVisibleChanged();
    Task RefreshWpfAsync();
    void TidyBeforeSave(EditorViewModel document);
}

/// <summary>
/// Manages open editor documents: opening, closing, saving, <see cref="IDocumentSessionHost.ActiveDocument"/>
/// transitions, <see cref="DocumentState.ContentChanged"/> subscription lifetime, and crash-recovery persistence.
/// </summary>
internal sealed class DocumentSessionManager(IDocumentSessionHost host)
{
    private Task? _recoveryLoop;

    public async Task OpenDocumentAsync(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".ico" or ".ttf" or ".otf" or ".zip")
        { host.Status = "Manage binary assets through WPF Explorer; this file is not editable source text"; return; }
        var doc = host.Store.Find(path) ?? await host.Store.OpenAsync(path);
        AddDocument(doc);
    }

    public void AddDocument(DocumentState state)
    {
        var existing = host.Documents.FirstOrDefault(d => d.State.Path.Equals(state.Path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { host.ActiveDocument = existing; return; }
        var vm = new EditorViewModel(state, host.WorkspaceClient, host.XamlCompletion, host.Dispatcher, host.AppendOutput, host.XamlResources)
        {
            Preferences = host.Editor,
            Performance = host.Performance.Monitor,
            CodeAssist = host.CodeAssist.Engine,
            DataTipEvaluator = (expression, token) => host.Debugger.IsStopped ? host.Debugger.EvaluateDataTipAsync(expression, token) : Task.FromResult<string?>(null),
            XamlCodeActionApplier = host.ApplyXamlCodeActionAsync,
            CSharpCodeActionApplier = (editor, action) => host.GuardAsync(() => host.ApplyCSharpCodeActionAsync(editor, action)),
            ShowInlayHints = host.ShowInlayHints,
            ShowInlineBlame = host.ShowInlineBlame,
            ShowCodeLens = host.ShowCodeLens,
            ShowQuickInfo = host.ShowQuickInfo
        };
        state.ContentChanged += OnDocumentContentChanged;
        vm.CodeLensRequested += (entry, position) => _ = host.GuardAsync(async () =>
        {
            host.ActiveDocument = vm;
            vm.Navigate(Math.Min(position, vm.State.Content.Length));
            await host.FindReferencesCommand.ExecuteAsync(null);
        });
        vm.RefreshGitAsync(host.Features.GitService, host.Lifetime).Forget(vm.ReportFault);
        vm.BlameCommitRequested += line => _ = host.GuardAsync(async () => { await host.OpenGitCommand.ExecuteAsync(null); host.Features.Git.ShowCommit(line, vm.GitBase?.RelativePath); });
        host.ConfigureXamlContext(vm);
        vm.BreakpointRequested += line => _ = host.GuardAsync(() => host.Debugger.ToggleBreakpointAsync(state.Path, line));
        vm.ActionRequested += action => _ = host.GuardAsync(() => host.HandleEditorActionAsync(vm, action));
        vm.ContextChanged += () =>
        {
            if (ReferenceEquals(vm, host.ActiveDocument))
            {
                host.UpdateAssistantContext();
                if (!host.IsNavigatingBindingSource && !host.IsRevealingDesignerSelection) host.Designer.SelectSource(vm.State.Path, vm.State.CaretOffset);
            }
        };
        vm.Diagnostics.CollectionChanged += (_, _) => host.RefreshEditorDiagnostics();
        vm.OpenRequested += path => _ = host.GuardAsync(() => OpenDocumentAsync(path));
        host.RefreshRelated(vm);
        host.Documents.Add(vm); host.ActiveDocument = vm; host.Breakpoints.RefreshMarkers();
        host.XamlAnalysis.TrackDocument(state);
        host.NotifyWelcomeVisibleChanged();
    }

    private void OnDocumentContentChanged(object? sender, EventArgs args) => host.AutoSave.Schedule();

    public async Task<bool> CloseDocumentAsync(EditorViewModel document, bool discardChanges = false)
    {
        using var holdAutoSave = await host.AutoSave.SuspendAsync();
        if (discardChanges) await host.Store.DeleteRecoveryAsync(document.State.Path);
        else if (document.State.IsDirty)
        {
            var decision = await host.Dialogs.AskSaveAsync(document.State.Name);
            if (decision == SaveDecision.Cancel) return false;
            if (decision == SaveDecision.Save && !await SaveDocumentAsync(document)) return false;
            if (decision == SaveDecision.Discard) await host.Store.DeleteRecoveryAsync(document.State.Path);
        }
        if (host.Designer.SourcePath == document.State.Path) await host.Designer.CloseAsync();
        document.State.ContentChanged -= OnDocumentContentChanged;
        document.Dispose(); host.Documents.Remove(document); host.Store.Close(document.State);
        // Update ActiveDocument immediately before awaiting worker close / WPF refresh so no disposed document remains active.
        if (ReferenceEquals(host.ActiveDocument, document)) host.ActiveDocument = host.Documents.LastOrDefault();
        host.UpdateAssistantContext(); host.NotifyWelcomeVisibleChanged();
        // Reconcile saved/discarded text before exposing closed-file index issues again. The watcher may not have reported the save yet.
        bool indexIsBehind = document.State.Extension == ".xaml" && host.WpfExplorer.Index?.Texts.TryGetValue(document.State.Path, out var indexedText) == true && indexedText != document.State.Content;
        if (indexIsBehind) host.XamlAnalysis.MarkIndexStale(document.State.Path);
        host.XamlAnalysis.UntrackDocument(document.State);
        // Queue the worker close before awaiting index refresh. A reopened
        // editor must synchronize after this old buffer has been retired.
        if (document.IsCSharp || document.IsXaml)
            try { await host.WorkspaceClient.CloseDocumentAsync(document.State.Path); } catch (Exception ex) { host.AppendOutput("Language document close: " + ex.Message); }
        if (indexIsBehind) await host.RefreshWpfAsync();
        else host.RefreshEditorDiagnostics();
        return true;
    }

    /// <summary>
    /// Closes every tab before the workspace changes, asking about each unsaved document first. Nothing is closed when any
    /// answer is Cancel or a save fails.
    /// </summary>
    public async Task<bool> CloseAllDocumentsForWorkspaceAsync()
    {
        if (host.Documents.Count == 0) return true;
        // Until every tab is closed, automatic saving must not write edits that are about to be discarded.
        using var holdAutoSave = await host.AutoSave.SuspendAsync();
        var closing = host.Documents.ToArray();
        var discard = new List<EditorViewModel>();
        foreach (var doc in closing.Where(d => d.State.IsDirty))
        {
            var decision = await host.Dialogs.AskSaveAsync(doc.State.Name);
            if (decision == SaveDecision.Cancel) return false;
            if (decision == SaveDecision.Save && !await SaveDocumentAsync(doc)) return false;
            if (decision == SaveDecision.Discard) discard.Add(doc);
        }
        // Do not discard anything until all close decisions have succeeded.
        foreach (var doc in closing)
        {
            if (discard.Contains(doc)) await host.Store.DeleteRecoveryAsync(doc.State.Path);
            if (host.Designer.SourcePath == doc.State.Path) await host.Designer.CloseAsync();
            doc.State.ContentChanged -= OnDocumentContentChanged;
            doc.Dispose(); host.Documents.Remove(doc); host.Store.Close(doc.State); host.XamlAnalysis.UntrackDocument(doc.State);
        }
        host.ActiveDocument = null; host.NotifyWelcomeVisibleChanged();
        return true;
    }

    /// <summary>Closes tabs one at a time; Cancel on a save prompt keeps that tab and the ones after it open.</summary>
    public async Task CloseDocumentsAsync(IReadOnlyList<EditorViewModel> documents)
    {
        var closed = 0;
        foreach (var document in documents)
        {
            if (!host.Documents.Contains(document)) continue;
            if (!await CloseDocumentAsync(document)) break;
            closed++;
        }
        host.Status = closed == 1 ? "Closed 1 tab" : $"Closed {closed} tabs";
    }

    /// <summary>Tears down an open document removed by an undone workspace edit without touching the document store.</summary>
    public async Task UntrackAndDisposeDocumentAsync(EditorViewModel document)
    {
        if (host.Designer.SourcePath == document.State.Path) await host.Designer.CloseAsync();
        document.State.ContentChanged -= OnDocumentContentChanged;
        document.Dispose(); host.Documents.Remove(document); host.XamlAnalysis.UntrackDocument(document.State);
    }

    public async Task<bool> SaveDocumentAsync(EditorViewModel doc)
    {
        if (doc.IsReadOnly) { host.Status = "Generated source snapshots are read-only"; return !doc.State.IsDirty; }
        try
        {
            host.TidyBeforeSave(doc);
            await host.Store.SaveAsync(doc.State);
            host.Messenger.Send(new DocumentSavedMessage(doc.State));
            host.XamlAnalysis.RequestKeepingResults();
            host.Status = doc.State.IsDirty ? "The document changed while saving. Save again to include the latest edits." : "Saved " + doc.State.Name;
            return !doc.State.IsDirty;
        }
        catch (ExternalFileChangedException)
        {
            if (!await host.Dialogs.ConfirmAsync("File changed outside WpfStudio", $"Overwrite the external changes to {doc.State.Name} with the editor content?")) return false;
            await host.Store.SaveAsync(doc.State, true);
            host.Messenger.Send(new DocumentSavedMessage(doc.State));
            host.XamlAnalysis.RequestKeepingResults();
            if (doc.State.IsDirty) host.Status = "The document changed while saving. Save again to include the latest edits.";
            return !doc.State.IsDirty;
        }
        catch (Exception ex) { await host.Dialogs.ShowErrorAsync("Save failed", ex.Message); return false; }
    }

    public async Task<bool> SaveAllDocumentsAsync()
    {
        foreach (var doc in host.Documents.Where(d => d.State.IsDirty || d.State.DiskHash == null).ToArray())
            if (!await SaveDocumentAsync(doc)) return false;
        if (host.Documents.Any(d => d.State.IsDirty)) { host.Status = "Documents changed while saving. Save again before building or debugging."; return false; }
        return true;
    }

    public async Task ReloadActiveDocumentAsync()
    {
        if (host.ActiveDocument == null) return;
        if (host.ActiveDocument.State.IsDirty && !await host.Dialogs.ConfirmAsync("Reload document", "Discard editor changes and reload this file from disk?")) return;
        await host.Store.ReloadAsync(host.ActiveDocument.State);
        host.ExternalChanges.Forget(host.ActiveDocument);
    }

    public async Task<DocumentState?> PrefetchDocumentAsync(string path, CancellationToken token)
    {
        // Failures are reported when the tab itself is opened.
        try { return await Task.Run(() => host.Store.OpenAsync(path, token)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { return null; }
    }

    public async Task RecoverPreviousSessionAsync()
    {
        var recovered = await host.Store.ReadRecoveryAsync();
        if (recovered.Count > 0 && await host.Dialogs.ConfirmAsync("Recover unsaved work", $"Recover {recovered.Count} unsaved document(s) from the previous session?"))
        {
            foreach (var item in recovered)
                await host.GuardAsync(async () =>
                {
                    var state = File.Exists(item.Path) ? await host.Store.OpenAsync(item.Path) : host.Store.Create(item.Path);
                    state.Content = item.Content;
                    AddDocument(state);
                });
        }
        else foreach (var item in recovered) await host.Store.DeleteRecoveryAsync(item.Path);
    }

    public void StartRecoveryLoop(CancellationToken token) => _recoveryLoop = RecoveryLoopAsync(token);

    public async Task StopRecoveryLoopAsync()
    {
        if (_recoveryLoop != null) await _recoveryLoop;
    }

    public void DisposeAllDocuments()
    {
        foreach (var document in host.Documents)
        {
            document.State.ContentChanged -= OnDocumentContentChanged;
            document.Dispose();
        }
    }

    private async Task RecoveryLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        // A failed tick must not end the loop: one locked record or a full disk would otherwise leave every later edit without a recovery copy.
        // The failure is reported once, and again only after a tick has succeeded in between.
        var failing = false;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try { await host.Store.WriteRecoveryAsync(token); failing = false; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (failing) continue;
                    failing = true;
                    host.Logger.LogError(ex, "Recovery persistence failed");
                    host.Dispatcher.Post(() => host.AppendOutput("Recovery: " + ex.Message));
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
