using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Runtimes;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Features.WpfExplorer;
using WpfStudio.App.Features.XamlAnalysis;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;

namespace WpfStudio.App.ViewModels;

internal interface IWorkspaceLifecycleHost
{
    WorkspaceSnapshot? Workspace { get; set; }
    WorkspaceProject? StartupProject { get; set; }
    string Configuration { get; set; }
    string? TargetFramework { get; set; }
    string? LaunchProfile { get; set; }
    string Status { get; set; }
    bool IsInitialized { get; }
    bool IsDisposed { get; }
    bool IsPreviewOpen { get; }
    bool IsGitToolOpened { get; }
    string[] AllFiles { get; set; }
    CancellationToken Lifetime { get; }
    WorkspaceClient WorkspaceClient { get; }
    ObservableCollection<WorkspaceProject> Projects { get; }
    ObservableCollection<string> RecentWorkspaces { get; }
    ObservableCollection<string> Configurations { get; }
    ObservableCollection<string> TargetFrameworks { get; }
    ObservableCollection<string> LaunchProfiles { get; }
    BulkObservableCollection<NavigationResult> SearchResults { get; }
    string SearchResultsTitle { get; set; }
    ObservableCollection<EditorViewModel> Documents { get; }
    SolutionExplorerViewModel Explorer { get; }
    WpfExplorerViewModel WpfExplorer { get; }
    DebuggerViewModel Debugger { get; }
    TerminalViewModel Terminal { get; }
    StudioFeatures Features { get; }
    TaskListViewModel TaskList { get; }
    FileGroupsViewModel FileGroups { get; }
    RuntimeCheckViewModel Runtimes { get; }
    ProjectXamlAnalysisCoordinator XamlAnalysis { get; }
    IAsyncRelayCommand ReloadWorkspaceCommand { get; }
    Task<bool> CloseAllDocumentsForWorkspaceAsync();
    void ClearHistory();
    void AppendOutput(string line);
    void ShowWorkspaceStatus(WorkspaceSnapshot snapshot, bool refreshingModel);
    void RefreshEditorDiagnostics();
    Task RefreshWpfAsync();
    Task SaveSettingsAsync();
    void CheckRuntimes(string path);
    Task RefreshSavedProjectModelAsync(string path, string configuration);
    void ReportBackgroundFault(Exception fault);
}

/// <summary>
/// Owns the workspace <see cref="OperationGate"/> and orchestrates workspace Load, Close, and Reload
/// transitions so long-running shell operations cannot interleave with teardown or context changes.
/// </summary>
internal sealed class WorkspaceLifecycleCoordinator
{
    private readonly IWorkspaceLifecycleHost _host;
    private readonly OperationGate _operations;

    public WorkspaceLifecycleCoordinator(IWorkspaceLifecycleHost host, Action<bool> busyChanged)
    {
        _host = host;
        _operations = new(busyChanged, host.Lifetime);
    }

    public bool IsBusy => _operations.IsBusy;
    public bool IsLoading { get; private set; }
    public bool IsContextReloadPending { get; private set; }
    public bool IsUpdatingChoices { get; private set; }

    public OperationGate.Lease? TryEnter(CancellationToken token = default) => _operations.TryEnter(token);

    public void Cancel() => _operations.Cancel();

    /// <summary>Frees the gate, then runs the reload that was requested while it was held.</summary>
    public void EndOperation(OperationGate.Lease lease)
    {
        lease.Dispose();
        if (IsContextReloadPending) ReloadWhenFreeAsync().Forget(_host.ReportBackgroundFault);
    }

    /// <summary>Marks launch/configuration choice collections as updating inside a guaranteed try/finally scope.</summary>
    public ChoicesUpdateScope BeginUpdatingChoices()
    {
        IsUpdatingChoices = true;
        return new(this);
    }

    public void ScheduleContextReload()
    {
        if (!_host.IsInitialized || _host.Workspace == null || IsUpdatingChoices) return;
        ReloadWhenFreeAsync().Forget(_host.ReportBackgroundFault);
    }

    /// <summary>Reloads now, or when the operation holding the gate ends.</summary>
    public Task ReloadWhenFreeAsync()
    {
        if (_host.Workspace == null || _host.IsDisposed) return Task.CompletedTask;
        IsContextReloadPending = true;
        return ExecuteWhenAvailableAsync(_host.ReloadWorkspaceCommand);
    }

    /// <summary>For internal callers: ExecuteAsync skips CanExecute, and on a running cancellable command it cancels that execution.</summary>
    public static Task ExecuteWhenAvailableAsync(IAsyncRelayCommand command) =>
        command.CanExecute(null) ? command.ExecuteAsync(null) : Task.CompletedTask;

    public async Task LoadWorkspaceAsync(string path)
    {
        // The language worker starts in the solution's folder and resolves the path again there, so a relative command-line argument
        // would name a different file in the worker, and would be saved as a relative recent entry.
        path = Path.GetFullPath(path);
        if (IsBusy) { _host.Status = "Wait for the current workspace operation to finish"; return; }
        if (_host.IsPreviewOpen) { _host.Status = "Finish or cancel the change preview before switching workspaces"; return; }
        if (!await _host.CloseAllDocumentsForWorkspaceAsync()) return;
        if (_host.Debugger.IsActive) await _host.Debugger.StopCommand.ExecuteAsync(null);
        if (_host.IsGitToolOpened) await _host.Features.Git.SetWorkspaceAsync(null);
        // The awaits above let another operation start.
        if (_operations.TryEnter() is not { } lease) { _host.Status = "Wait for the current workspace operation to finish"; return; }
        IsLoading = true;
        var token = lease.Token;
        var loaded = false;
        try
        {
            _host.Status = "Loading " + Path.GetFileName(path) + "…";
            _host.ClearHistory();
            _host.Explorer.Clear();
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            _host.Terminal.WorkingDirectory = directory;
            _host.TargetFramework = null;
            // Start the worker first: listing files overlaps its process start and SDK resolution, and the tree appears before language services finish.
            // The project model saved by the last session answers at once; MSBuild refreshes it in the background below.
            var loading = _host.WorkspaceClient.LoadAsync(new(path, _host.Configuration, UseCache: true), token);
            try { _host.AllFiles = await Task.Run(() => EnumerateSourceFiles(directory).ToArray(), token); }
            catch (IOException ex) { _host.AllFiles = []; _host.AppendOutput("File listing: " + ex.Message); }
            _host.Explorer.ShowFiles(directory, _host.AllFiles);
            _host.Status = "Loading language services for " + Path.GetFileName(path) + "…";
            _host.Workspace = await loading; loaded = true;
            RefreshConfigurations();
            _host.Projects.Clear(); foreach (var project in _host.Workspace.Projects) _host.Projects.Add(project);
            _host.StartupProject = _host.Projects.FirstOrDefault(p => p.IsExecutable) ?? _host.Projects.FirstOrDefault();
            _host.AllFiles = _host.AllFiles.Concat(_host.Workspace.Projects.SelectMany(p => p.Files).Where(f => !f.IsGenerated).Select(f => f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _host.Explorer.Rebuild(keepState: false);
            if (_host.Explorer.Nodes.Count == 0 && !_host.Explorer.IsFiltered) _host.Explorer.ShowFiles(directory, _host.AllFiles);
            foreach (var issue in _host.Workspace.Issues) _host.AppendOutput(issue.Severity + ": " + issue.Message);
            _host.RecentWorkspaces.Remove(path); _host.RecentWorkspaces.Insert(0, path); while (_host.RecentWorkspaces.Count > 10) _host.RecentWorkspaces.RemoveAt(10);
            // The language service is up: say so now rather than after the steps below, which the status used to wait for.
            _host.ShowWorkspaceStatus(_host.Workspace, refreshingModel: _host.Workspace.Cached);
            // Independent of one another, so they run together instead of one after another.
            await Task.WhenAll(_host.Debugger.SetWorkspaceAsync(path), _host.IsGitToolOpened ? _host.Features.Git.SetWorkspaceAsync(path) : Task.CompletedTask,
                _host.RefreshWpfAsync(), _host.TaskList.LoadUserTasksAsync(), _host.FileGroups.LoadAsync());
            _host.TaskList.RefreshCommand.ExecuteAsync(null).Forget(_host.ReportBackgroundFault);
            await _host.SaveSettingsAsync();
        }
        catch when (!loaded)
        {
            // The previous workspace's tabs are closed and its worker is stopped by now, so it must not stay the target of Build, Run and the title.
            _host.Projects.Clear(); _host.StartupProject = null; _host.Workspace = null;
            throw;
        }
        finally
        {
            IsLoading = false;
            lease.Dispose();
            _host.XamlAnalysis.Request();
            if (IsContextReloadPending) ReloadWhenFreeAsync().Forget(_host.ReportBackgroundFault);
            _host.CheckRuntimes(path);
        }
        if (_host.Workspace is { Cached: true } cached && !IsContextReloadPending)
            _host.RefreshSavedProjectModelAsync(cached.Path, _host.Configuration).Forget(_host.ReportBackgroundFault);
    }

    public async Task<bool> CloseWorkspaceAsync()
    {
        var onlyLooseFiles = _host.Workspace == null && _host.Explorer.Nodes.Count == 0;
        if (onlyLooseFiles && _host.Documents.Count == 0) { _host.Status = "No solution is open"; return true; }
        if (IsBusy) { _host.Status = "Wait for the current workspace operation to finish"; return false; }
        if (_host.IsPreviewOpen) { _host.Status = "Finish or cancel the change preview before closing the solution"; return false; }
        if (!await _host.CloseAllDocumentsForWorkspaceAsync()) return false;
        if (_host.Debugger.IsActive) await _host.Debugger.StopCommand.ExecuteAsync(null);
        if (_host.IsGitToolOpened) await _host.Features.Git.SetWorkspaceAsync(null);
        // The awaits above let another operation start.
        if (_operations.TryEnter() is not { } lease) { _host.Status = "Wait for the current workspace operation to finish"; return false; }
        try
        {
            var name = _host.Workspace == null ? "" : Path.GetFileName(_host.Workspace.Path);
            _host.ClearHistory();
            _host.AllFiles = [];
            IsContextReloadPending = false;
            _host.Explorer.Clear(); _host.Projects.Clear(); _host.SearchResults.Clear(); _host.SearchResultsTitle = ""; _host.WpfExplorer.Items.Clear();
            using (BeginUpdatingChoices())
            {
                _host.TargetFrameworks.Clear(); _host.LaunchProfiles.Clear(); _host.TargetFramework = null; _host.LaunchProfile = null;
            }
            _host.StartupProject = null;
            _host.Workspace = null;
            await _host.Runtimes.CheckAsync(null, []);
            _host.RefreshEditorDiagnostics();
            await _host.TaskList.LoadUserTasksAsync(); await _host.FileGroups.LoadAsync(); await _host.TaskList.RefreshCommand.ExecuteAsync(null);
            try { await _host.WorkspaceClient.UnloadAsync(_host.Lifetime); } catch (Exception ex) { _host.AppendOutput("Language worker shutdown: " + ex.Message); }
            await _host.SaveSettingsAsync();
            _host.Status = onlyLooseFiles ? "Closed all tabs" : name.Length == 0 ? "Solution closed" : $"Closed {name}";
            return true;
        }
        finally { lease.Dispose(); }
    }

    public async Task ReloadWorkspaceAsync(CancellationToken cancel)
    {
        if (_host.Workspace == null) return;
        IsContextReloadPending = true;
        // The operation holding the gate reloads when it ends.
        if (_operations.TryEnter(cancel) is not { } lease) return;
        try { await ReloadProjectModelAsync(lease.Token); }
        finally { lease.Dispose(); }
    }

    /// <summary>The caller holds the gate. Only the load can be cancelled: the steps after it are short and leave the editors consistent with the new model.</summary>
    private async Task ReloadProjectModelAsync(CancellationToken token)
    {
        IsLoading = true;
        try
        {
            do
            {
                IsContextReloadPending = false;
                var requestedConfiguration = _host.Configuration;
                var requestedFramework = _host.TargetFramework;
                _host.Status = "Updating project context…";
                var startupPath = _host.StartupProject?.ProjectPath;
                _host.Workspace = await _host.WorkspaceClient.LoadAsync(
                    new(_host.Workspace!.Path, requestedConfiguration,
                        Path.GetExtension(_host.Workspace.Path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? requestedFramework : null),
                    token);
                ApplyWorkspaceProjects(startupPath);
                foreach (var doc in _host.Documents) await doc.SyncAsync(_host.Lifetime);
                await _host.RefreshWpfAsync(); _host.Status = "Workspace refreshed";
                if (requestedConfiguration != _host.Configuration || requestedFramework != _host.TargetFramework) IsContextReloadPending = true;
            } while (IsContextReloadPending && !token.IsCancellationRequested);
        }
        // A load stops the old worker before starting the new one, so nothing keeps serving the previous model.
        catch (OperationCanceledException) when (!_host.Lifetime.IsCancellationRequested) { _host.Status = "Reload cancelled. Language services are off until you reload the workspace."; }
        finally { IsLoading = false; _host.XamlAnalysis.Request(); }
    }

    public void ApplyWorkspaceProjects(string? startupPath)
    {
        if (_host.Workspace is null) return;
        RefreshConfigurations();
        _host.Projects.Clear(); foreach (var project in _host.Workspace.Projects) _host.Projects.Add(project);
        _host.StartupProject = _host.Projects.FirstOrDefault(p => p.ProjectPath == startupPath) ?? _host.Projects.FirstOrDefault(p => p.IsExecutable) ?? _host.Projects.FirstOrDefault();
        _host.AllFiles = _host.AllFiles.Concat(_host.Workspace.Projects.SelectMany(p => p.Files).Where(f => !f.IsGenerated).Select(f => f.Path)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _host.Explorer.Rebuild(keepState: true);
    }

    public void RefreshConfigurations()
    {
        var current = _host.Configuration;
        var configurations = (_host.Workspace?.Configurations ?? ["Debug", "Release"]).Append(current).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using (BeginUpdatingChoices())
        {
            _host.Configurations.Clear(); foreach (var configuration in configurations) _host.Configurations.Add(configuration);
            _host.Configuration = current;
        }
    }

    public static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            string[] files; string[] directories;
            try { files = Directory.GetFiles(directory); directories = Directory.GetDirectories(directory); } catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in directories)
                if (Path.GetFileName(child) is not ("bin" or "obj" or ".git" or ".vs" or "node_modules" or "artifacts") && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
        }
    }

    internal readonly struct ChoicesUpdateScope(WorkspaceLifecycleCoordinator owner) : IDisposable
    {
        public void Dispose() => owner.IsUpdatingChoices = false;
    }
}
