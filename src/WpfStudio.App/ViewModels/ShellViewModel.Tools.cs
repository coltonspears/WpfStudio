using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.App.Features.Workbench;

namespace WpfStudio.App.ViewModels;

/// <summary>A pane the docking layout keeps for the shell's lifetime; its view is the DataTemplate for the model's type.</summary>
public sealed record RetainedPane(string ContentId, string Title, object Model);

public sealed partial class ShellViewModel
{
    public IReadOnlyList<RetainedPane> RetainedPanes =>
    [
        new(Workbenches.Packages, "NuGet packages", Features.Packages),
        new(Workbenches.Git, "Git changes", Features.Git),
        new(Workbenches.DevOps, "DevOps", Features.DevOps),
        new(Workbenches.Profiler, "Memory profiler", MemoryProfiler),
        new(Workbenches.ApiClient, "API client", Features.ApiClient),
        new(Workbenches.Designer, "XAML Designer", Designer),
        new(Workbenches.LiveInspection, "Live XAML", LiveInspection),
    ];
    private bool _gitToolOpened;
    private bool _packagesToolOpened;
    private EditorViewModel? _assistantContextDocument;
    public StudioFeatures Features { get; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    partial void OnIsSettingsOpenChanged(bool value) => UpdateDesignerOverlay();
    partial void OnActiveDocumentChanged(EditorViewModel? value)
    {
        value?.RefreshCountsIfBehind();
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
        else if (name == Workbenches.DevOps)
            _ = GuardAsync(ActivateDevOpsAsync);
        else if (name == Workbenches.Packages && !_packagesToolOpened)
        {
            _packagesToolOpened = true;
            Features.Packages.SetWorkspace(Workspace);
            _ = GuardAsync(() => Features.Packages.ActivateAsync());
        }
        // Loads collections, environments and the tabs left open last time; later calls return at once.
        else if (name == Workbenches.ApiClient) _ = GuardAsync(Features.ApiClient.InitializeAsync);
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
    /// <summary>The model generator on its own, for a JSON sample or schema that did not come from a request.</summary>
    [RelayCommand] private Task OpenModelGeneratorAsync() => GuardAsync(async () => { ToolRequested?.Invoke(Workbenches.ApiClient); await Features.ApiClient.InitializeAsync(); await Features.ApiClient.OpenModelGeneratorAsync(); });
    [RelayCommand] private Task OpenApiClientAsync() => GuardAsync(async () => { ToolRequested?.Invoke(Workbenches.ApiClient); await Features.ApiClient.InitializeAsync(); });
    [RelayCommand] private Task OpenGitAsync() => GuardAsync(async () => { _gitToolOpened = true; ToolRequested?.Invoke(Workbenches.Git); await Features.Git.SetWorkspaceAsync(Workspace?.Path); });
    /// <summary>Opens the DevOps workbench: pull requests, work items and pipelines of the repository the Git pane follows.</summary>
    [RelayCommand] private Task OpenDevOpsAsync() => GuardAsync(async () => { ToolRequested?.Invoke(Workbenches.DevOps); await ActivateDevOpsAsync(); });
    /// <summary>Opens a DevOps tab (0 pull requests, 1 work items, 2 pipelines, 3 access) and the workbench with it.</summary>
    [RelayCommand] private Task OpenDevOpsTabAsync(string? tab) => GuardAsync(async () =>
    {
        if (int.TryParse(tab, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)) Features.DevOps.SelectedTab = index;
        ToolRequested?.Invoke(Workbenches.DevOps);
        await ActivateDevOpsAsync();
    });
    /// <summary>The DevOps workbench follows the Git pane's repository, so the repository is loaded first if nothing opened it yet.</summary>
    private async Task ActivateDevOpsAsync()
    {
        if (!_gitToolOpened)
        {
            _gitToolOpened = true;
            await Features.Git.SetWorkspaceAsync(Workspace?.Path);
        }
        await Features.DevOps.ActivateAsync();
    }
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
            case EditorAction.BreakpointSettings: Breakpoints.EditSettingsCommand.Execute(null); break;
            case EditorAction.BreakpointCondition: Breakpoints.ConditionCommand.Execute(null); break;
            case EditorAction.BreakpointHitCount: Breakpoints.HitCountCommand.Execute(null); break;
            case EditorAction.BreakpointLogMessage: Breakpoints.LogMessageCommand.Execute(null); break;
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
