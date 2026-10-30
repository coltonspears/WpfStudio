using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private readonly HashSet<BreakpointViewModel> _observedBreakpoints = [];
    private bool _gitToolOpened;
    private bool _packagesToolOpened;
    private EditorViewModel? _assistantContextDocument;
    public StudioFeatures? Features { get; private set; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    private void InitializeFeatures(StudioFeatures? features)
    {
        Features = features;
        if (features == null) return;
        features.SaveBeforeMutation = async () =>
        {
            if (IsBusy || _loading || IsPreviewOpen || Debugger.IsActive) throw new InvalidOperationException("Finish the current build, debug session, or change preview before modifying the workspace.");
            return await SaveAllDocumentsAsync();
        };
        features.WorkspaceChanged = async () => { await CheckExternalChangesAsync(fromAppOperation: true); await ReloadWorkspaceAsync(); };
        features.Git.OpenFileRequested += path => _ = GuardAsync(() => OpenDocumentAsync(path));
        // Commits, checkouts and merges change what each editor's change margin and blame compare with.
        features.Git.RepositoryChanged += () => { foreach (var document in Documents) _ = document.RefreshGitAsync(features.GitService, _lifetime.Token); };
        features.Git.WorkspaceFilesChanged += () => _ = GuardAsync(() => features.WorkspaceChanged());
        features.Assistant.SettingsRequested += () => _ = OpenSettingsCommand.ExecuteAsync(AssistantPage);
        features.Assistant.RefreshContextRequested += UpdateAssistantContext;
    }
    partial void OnActiveDocumentChanged(EditorViewModel? value)
    {
        if (value != null) { ActiveWorkbench = null; _assistantContextDocument = value; }
        if (ProblemScope == CurrentFileScope) RefreshVisibleProblems();
        UpdateAssistantContext();
    }
    private void UpdateAssistantContext()
    {
        var document = ActiveDocument ?? (Documents.Contains(_assistantContextDocument!) ? _assistantContextDocument : null);
        Features?.Assistant.SetEditorContext(document?.State.Path, document?.State.Content, document?.SelectedText);
    }
    public void ActivateWorkbench(string? name)
    {
        ActiveWorkbench = name;
        ActiveDocument = null;
        // Restored tabs get the same lazy initialization as a menu-opened tab.
        if (Features == null) return;
        if (name == "Git" && !_gitToolOpened)
        {
            _gitToolOpened = true;
            _ = GuardAsync(() => Features.Git.SetWorkspaceAsync(Workspace?.Path));
        }
        else if (name == "Packages" && !_packagesToolOpened)
        {
            _packagesToolOpened = true;
            Features.Packages.SetWorkspace(Workspace);
            _ = GuardAsync(() => Features.Packages.ActivateAsync());
        }
    }
    [RelayCommand] private Task OpenSettingsAsync(string? page) => GuardAsync(async () =>
    {
        if (Features != null) await Features.Assistant.InitializeAsync();
        if (!string.IsNullOrEmpty(page)) SettingsPage = page;
        IsSettingsOpen = true;
    });
    [RelayCommand] private void CloseSettings() => IsSettingsOpen = false;
    [RelayCommand] private Task OpenPackagesAsync() => GuardAsync(async () =>
    {
        if (Features == null) return;
        _packagesToolOpened = true;
        ToolRequested?.Invoke("Packages"); Features.Packages.SetWorkspace(Workspace);
        await Features.Packages.ActivateAsync();
    });
    [RelayCommand] private Task OpenSelectedProjectPackagesAsync() => OpenProjectPackagesAsync(SelectedNode?.ProjectPath);
    private Task OpenProjectPackagesAsync(string? projectPath) => GuardAsync(async () =>
    {
        if (Features == null) return;
        _packagesToolOpened = true;
        ToolRequested?.Invoke("Packages"); Features.Packages.SetWorkspace(Workspace);
        if (Features.Packages.Projects.FirstOrDefault(project => project.ProjectPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase)) is { } selected)
            Features.Packages.SelectedProject = selected;
        await Features.Packages.ActivateAsync();
    });
    [RelayCommand] private Task OpenGitAsync() => GuardAsync(async () => { if (Features != null) { _gitToolOpened = true; ToolRequested?.Invoke("Git"); await Features.Git.SetWorkspaceAsync(Workspace?.Path); } });
    [RelayCommand] private Task OpenAssistantAsync() => GuardAsync(async () => { if (Features != null) { UpdateAssistantContext(); ToolRequested?.Invoke("ColtonGPT"); await Features.Assistant.InitializeAsync(); } });
    [RelayCommand] private Task RevealSelectedAsync() => GuardAsync(() => { if (SelectedNode is { Path.Length: > 0 }) DesktopNavigation.Reveal(SelectedNode.Path); return Task.CompletedTask; });
    [RelayCommand] private Task CopySelectedPathAsync() => GuardAsync(() => { if (SelectedNode is { Path.Length: > 0 }) DesktopNavigation.Copy(SelectedNode.Path); return Task.CompletedTask; });
    [RelayCommand] private Task EditProjectAsync() => GuardAsync(async () => { var path = SelectedNode?.ProjectPath ?? StartupProject?.ProjectPath; if (path != null) await OpenDocumentAsync(path); });
    [RelayCommand] private void SetStartupProject() { if (Projects.FirstOrDefault(p => p.ProjectPath == SelectedNode?.ProjectPath) is { } project) { StartupProject = project; Status = project.Name + " is the startup project"; } }
    [RelayCommand] private Task RefactorAsync(string? action) => GuardAsync(async () =>
    {
        if (ActiveDocument is not { IsReadOnly: false } document || document.State.Extension != ".cs" || action == null) { Status = "Open a C# source document to use this refactoring"; return; }
        await document.SyncAsync(); var state = document.State;
        var result = await _workspace.RefactorAsync(new(state.Path, state.CaretOffset, state.Version, action));
        var changes = await _edits.PrepareAsync(result);
        if (await PreviewAsync("Review C# refactoring", changes, string.Join(Environment.NewLine, result.Warnings))) await ApplyChangesAsync(changes);
    });
    private async Task ApplyCSharpCodeActionAsync(EditorViewModel editor, WpfStudio.Contracts.CodeActionEntry action)
    {
        var state = editor.State;
        Status = action.Title + "…";
        var result = await _workspace.ApplyCodeActionAsync(new(state.Path, state.Version, action.Id), _lifetime.Token);
        var changes = await _edits.PrepareAsync(result);
        if (await PreviewAsync(action.Title, changes, string.Join(Environment.NewLine, result.Warnings)))
        {
            await ApplyChangesAsync(changes);
            Status = "Applied: " + action.Title;
        }
    }
    [RelayCommand] private void ShowBreakpoints() { Debugger.SelectedTab = 2; ToolRequested?.Invoke("Debugger"); }
    [RelayCommand] private Task BreakpointConditionAsync() => GuardAsync(async () =>
    {
        if (ActiveDocument is not { } doc || doc.State.Extension != ".cs") return;
        var line = doc.State.CaretLine;
        var breakpoint = Debugger.Breakpoints.FirstOrDefault(b => b.Path.Equals(doc.State.Path, StringComparison.OrdinalIgnoreCase) && b.Line == line);
        var condition = await _dialogs.PromptAsync("Breakpoint condition", "Break when this C# expression is true (leave empty for every hit):", breakpoint?.Condition ?? "");
        if (condition == null) return;
        if (breakpoint == null) { await Debugger.ToggleBreakpointAsync(doc.State.Path, line); breakpoint = Debugger.Breakpoints.FirstOrDefault(b => b.Path.Equals(doc.State.Path, StringComparison.OrdinalIgnoreCase) && b.Line == line); }
        if (breakpoint == null) return;
        breakpoint.Condition = condition; Debugger.SelectedBreakpoint = breakpoint;
        await Debugger.ApplyBreakpointsCommand.ExecuteAsync(null); ShowBreakpoints();
    });
    [RelayCommand] private Task BreakpointHitCountAsync() => GuardAsync(async () =>
    {
        if (ActiveDocument is not { } doc || doc.State.Extension != ".cs") return;
        var breakpoint = BreakpointAtCaret(doc);
        var text = await _dialogs.PromptAsync("Breakpoint hit count", "Break on hit 5, >=5 from the fifth hit, or %5 on every fifth hit (leave empty for every hit):", breakpoint?.HitCondition ?? "");
        if (text == null) return;
        if (!WpfStudio.Contracts.HitCondition.TryParse(text, out _, out var error)) { await _dialogs.ShowErrorAsync("Breakpoint hit count", error!); return; }
        breakpoint ??= await AddBreakpointAtCaretAsync(doc);
        if (breakpoint == null) return;
        breakpoint.HitCondition = text.Trim(); Debugger.SelectedBreakpoint = breakpoint;
        await Debugger.ApplyBreakpointsCommand.ExecuteAsync(null);
        Status = "Breakpoint " + breakpoint.Summary.ToLowerInvariant();
    });
    [RelayCommand] private Task BreakpointLogMessageAsync() => GuardAsync(async () =>
    {
        if (ActiveDocument is not { } doc || doc.State.Extension != ".cs") return;
        var breakpoint = BreakpointAtCaret(doc);
        var text = await _dialogs.PromptAsync("Tracepoint", "Write this message to Output and continue. Use {expression} for values and $FUNCTION, $TID, $FILEPOS or $HITCOUNT (leave empty to break instead):", breakpoint?.LogMessage ?? "");
        if (text == null) return;
        breakpoint ??= await AddBreakpointAtCaretAsync(doc);
        if (breakpoint == null) return;
        breakpoint.LogMessage = text.Trim(); Debugger.SelectedBreakpoint = breakpoint;
        await Debugger.ApplyBreakpointsCommand.ExecuteAsync(null);
        Status = breakpoint.IsTracepoint ? "Tracepoint set; its messages appear in Debugger > Output" : "Breakpoint breaks again instead of logging";
    });
    [RelayCommand] private Task AddWatchAtCaretAsync() => GuardAsync(async () =>
    {
        if (ActiveDocument is not { IsCSharp: true } doc || doc.DebugExpressionAt(doc.State.CaretOffset) is not { } expression)
        { Status = "Place the caret on a variable or member, or select an expression, to add a watch"; return; }
        await Debugger.AddWatchAsync(expression);
        Debugger.SelectedTab = 1; ToolRequested?.Invoke("Debugger");
    });
    private BreakpointViewModel? BreakpointAtCaret(EditorViewModel doc) =>
        Debugger.Breakpoints.FirstOrDefault(b => b.Path.Equals(doc.State.Path, StringComparison.OrdinalIgnoreCase) && b.Line == doc.State.CaretLine);
    private async Task<BreakpointViewModel?> AddBreakpointAtCaretAsync(EditorViewModel doc)
    {
        await Debugger.ToggleBreakpointAsync(doc.State.Path, doc.State.CaretLine);
        return BreakpointAtCaret(doc);
    }
    [RelayCommand] private Task DisableBreakpointAsync() => GuardAsync(async () =>
    {
        if (ActiveDocument is not { } doc) return;
        var breakpoint = Debugger.Breakpoints.FirstOrDefault(b => b.Path.Equals(doc.State.Path, StringComparison.OrdinalIgnoreCase) && b.Line == doc.State.CaretLine);
        if (breakpoint == null) { Status = "No breakpoint on this line"; return; }
        breakpoint.Enabled = !breakpoint.Enabled; await Debugger.ApplyBreakpointsCommand.ExecuteAsync(null);
    });
    private void ObserveBreakpoints()
    {
        foreach (var removed in _observedBreakpoints.Where(b => !Debugger.Breakpoints.Contains(b)).ToArray()) { removed.PropertyChanged -= BreakpointChanged; _observedBreakpoints.Remove(removed); }
        foreach (var added in Debugger.Breakpoints.Where(b => !_observedBreakpoints.Contains(b))) { added.PropertyChanged += BreakpointChanged; _observedBreakpoints.Add(added); }
        RefreshBreakpointMarkers();
    }
    private void BreakpointChanged(object? sender, PropertyChangedEventArgs e) => RefreshBreakpointMarkers();
    private async Task HandleEditorActionAsync(EditorViewModel document, EditorAction action)
    {
        ActiveDocument = document;
        switch (action)
        {
            case EditorAction.Definition: await GoToDefinitionCommand.ExecuteAsync(null); break;
            case EditorAction.OpenDesigner: await OpenDesignerCommand.ExecuteAsync(null); break;
            case EditorAction.Implementation: await GoToImplementationCommand.ExecuteAsync(null); break;
            case EditorAction.References: await FindReferencesCommand.ExecuteAsync(null); break;
            case EditorAction.Rename: await RenameSymbolCommand.ExecuteAsync(null); break;
            case EditorAction.Format: await FormatCommand.ExecuteAsync(null); break;
            case EditorAction.OrganizeUsings: case EditorAction.UseVar: case EditorAction.UseExplicitType: await RefactorCommand.ExecuteAsync(action.ToString()); break;
            case EditorAction.ToggleBreakpoint: await ToggleBreakpointCommand.ExecuteAsync(null); break;
            case EditorAction.BreakpointCondition: await BreakpointConditionCommand.ExecuteAsync(null); break;
            case EditorAction.BreakpointHitCount: await BreakpointHitCountCommand.ExecuteAsync(null); break;
            case EditorAction.BreakpointLogMessage: await BreakpointLogMessageCommand.ExecuteAsync(null); break;
            case EditorAction.AddWatch: await AddWatchAtCaretCommand.ExecuteAsync(null); break;
            case EditorAction.DisableBreakpoint: await DisableBreakpointCommand.ExecuteAsync(null); break;
            case EditorAction.ShowBreakpoints: ShowBreakpoints(); break;
            case EditorAction.SwitchRelated: await SwitchRelatedCommand.ExecuteAsync(null); break;
            case EditorAction.InsertProperty: await InsertPropertyCommand.ExecuteAsync(null); break;
            case EditorAction.InsertCommand: await InsertCommandCommand.ExecuteAsync(null); break;
            case EditorAction.InsertSnippet: InsertSnippet(); break;
            case EditorAction.AskColtonGpt:
                await OpenAssistantCommand.ExecuteAsync(null);
                if (Features != null) Features.Assistant.ContextMode = document.SelectionLength > 0 ? "Selected text" : "No editor context";
                break;
        }
    }
}
