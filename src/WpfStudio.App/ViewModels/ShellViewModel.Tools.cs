using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _gitToolOpened;
    private bool _packagesToolOpened;
    private EditorViewModel? _assistantContextDocument;
    public StudioFeatures Features { get; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    partial void OnIsSettingsOpenChanged(bool value) => UpdateDesignerOverlay();
    partial void OnActiveDocumentChanged(EditorViewModel? value)
    {
        if (value != null) { ActiveWorkbench = null; _assistantContextDocument = value; }
        NotifyActiveDocumentChanged();
        UpdateAssistantContext();
    }
    private void UpdateAssistantContext()
    {
        var document = ActiveDocument ?? (Documents.Contains(_assistantContextDocument!) ? _assistantContextDocument : null);
        Features.Assistant.SetEditorContext(document?.State.Path, document?.State.Content, document?.SelectedText);
    }
    public void ActivateWorkbench(string? name)
    {
        ActiveWorkbench = name;
        ActiveDocument = null;
        // Restored tabs get the same lazy initialization as a menu-opened tab.
        if (name == Workbenches.Git && !_gitToolOpened)
        {
            _gitToolOpened = true;
            _ = GuardAsync(() => Features.Git.SetWorkspaceAsync(Workspace?.Path));
        }
        else if (name == Workbenches.Packages && !_packagesToolOpened)
        {
            _packagesToolOpened = true;
            Features.Packages.SetWorkspace(Workspace);
            _ = GuardAsync(() => Features.Packages.ActivateAsync());
        }
    }
    [RelayCommand] private Task OpenSettingsAsync(string? page) => GuardAsync(async () =>
    {
        await Features.Assistant.InitializeAsync();
        if (!string.IsNullOrEmpty(page)) SettingsPage = page;
        IsSettingsOpen = true;
    });
    [RelayCommand] private void CloseSettings() => IsSettingsOpen = false;
    [RelayCommand] private Task OpenPackagesAsync() => GuardAsync(async () =>
    {
        _packagesToolOpened = true;
        ToolRequested?.Invoke(Workbenches.Packages); Features.Packages.SetWorkspace(Workspace);
        await Features.Packages.ActivateAsync();
    });
    [RelayCommand] private Task OpenSelectedProjectPackagesAsync() => OpenProjectPackagesAsync(Explorer.SelectedNode?.ProjectPath);
    private Task OpenProjectPackagesAsync(string? projectPath) => GuardAsync(async () =>
    {
        _packagesToolOpened = true;
        ToolRequested?.Invoke(Workbenches.Packages); Features.Packages.SetWorkspace(Workspace);
        if (Features.Packages.Projects.FirstOrDefault(project => project.ProjectPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase)) is { } selected)
            Features.Packages.SelectedProject = selected;
        await Features.Packages.ActivateAsync();
    });
    [RelayCommand] private Task OpenGitAsync() => GuardAsync(async () => { _gitToolOpened = true; ToolRequested?.Invoke(Workbenches.Git); await Features.Git.SetWorkspaceAsync(Workspace?.Path); });
    [RelayCommand] private Task OpenAssistantAsync() => GuardAsync(async () => { UpdateAssistantContext(); ToolRequested?.Invoke(Workbenches.Assistant); await Features.Assistant.InitializeAsync(); });
    [RelayCommand] private Task RefactorAsync(string? action) => GuardAsync(async () =>
    {
        if (ActiveDocument is not { IsReadOnly: false } document || document.State.Extension != ".cs" || action == null) { Status = "Open a C# source document to use this refactoring"; return; }
        await document.SyncAsync(_lifetime.Token); var state = document.State;
        var result = await _workspace.RefactorAsync(new(state.Path, state.CaretOffset, state.Version, action), _lifetime.Token);
        await ReviewAndApplyAsync("Review C# refactoring", result);
    });
    private async Task ApplyCSharpCodeActionAsync(EditorViewModel editor, WpfStudio.Contracts.CodeActionEntry action)
    {
        var state = editor.State;
        Status = action.Title + "…";
        var result = await _workspace.ApplyCodeActionAsync(new(state.Path, state.Version, action.Id), _lifetime.Token);
        if (await ReviewAndApplyAsync(action.Title, result)) Status = "Applied: " + action.Title;
    }
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
            case EditorAction.ToggleBreakpoint: await Breakpoints.ToggleCommand.ExecuteAsync(null); break;
            case EditorAction.BreakpointCondition: await Breakpoints.ConditionCommand.ExecuteAsync(null); break;
            case EditorAction.BreakpointHitCount: await Breakpoints.HitCountCommand.ExecuteAsync(null); break;
            case EditorAction.BreakpointLogMessage: await Breakpoints.LogMessageCommand.ExecuteAsync(null); break;
            case EditorAction.AddWatch: await Breakpoints.AddWatchCommand.ExecuteAsync(null); break;
            case EditorAction.DisableBreakpoint: await Breakpoints.DisableCommand.ExecuteAsync(null); break;
            case EditorAction.ShowBreakpoints: Breakpoints.ShowCommand.Execute(null); break;
            case EditorAction.SwitchRelated: await SwitchRelatedCommand.ExecuteAsync(null); break;
            case EditorAction.InsertProperty: await InsertPropertyCommand.ExecuteAsync(null); break;
            case EditorAction.InsertCommand: await InsertCommandCommand.ExecuteAsync(null); break;
            case EditorAction.InsertSnippet: InsertSnippet(); break;
            case EditorAction.AskColtonGpt:
                await OpenAssistantCommand.ExecuteAsync(null);
                Features.Assistant.ContextMode = document.SelectionLength > 0 ? "Selected text" : "No editor context";
                break;
        }
    }
}
