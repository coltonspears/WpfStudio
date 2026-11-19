using System.IO;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

/// <summary>
/// Keeps the project model in step with the disk. When a project file changes, or a source file is added, removed or
/// renamed, the worker loads the projects again in the background while the current model keeps answering, then
/// swaps. Without this the model stays out of date until someone chooses Reload workspace.
/// </summary>
public sealed partial class ShellViewModel
{
    private CancellationTokenSource? _projectRefreshDelay;
    private bool _projectRefreshRunning;
    private bool _projectRefreshAgain;
    // A build finished while a refresh was already reading the projects, so that refresh may have missed what the build wrote.
    private bool _projectRefreshAfterBuild;

    /// <summary>How long project files must stay quiet before the refresh starts, so a checkout or a restore is read once.</summary>
    internal TimeSpan ProjectRefreshPause { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>True when a change to project files is followed by a refresh without anyone asking for it.</summary>
    private bool RefreshesProjectModel => Editor.AutoRefreshProject && _workspace.IsConnected;

    void Features.XamlAnalysis.IProjectXamlAnalysisHost.ProjectFilesChanged() => ScheduleProjectModelRefresh();

    private void ScheduleProjectModelRefresh()
    {
        if (!Editor.AutoRefreshProject || _disposed || Workspace is null) return;
        // The files changed again while they were being loaded, so what is being loaded is already behind.
        if (_projectRefreshRunning) { _projectRefreshAgain = true; return; }
        _projectRefreshDelay?.Cancel(); _projectRefreshDelay?.Dispose();
        var pending = _projectRefreshDelay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Task.Delay(ProjectRefreshPause, pending.Token).ContinueWith(_ => _dispatcher.Post(() =>
        {
            if (pending.IsCancellationRequested || _disposed) return;
            RefreshChangedProjectModelAsync().Forget(ReportBackgroundFault);
        }), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default).Forget();
    }

    private Task RefreshChangedProjectModelAsync() => XamlAnalysis.ProjectReloadRequired
        ? RefreshProjectModelAsync("Project files changed on disk. Refreshing the project model…") : Task.CompletedTask;

    /// <param name="status">Shown while the refresh runs, or null to leave the status alone, as after a build, which reports its own result.</param>
    private async Task RefreshProjectModelAsync(string? status)
    {
        var afterBuild = status is null;
        if (_projectRefreshRunning) { if (afterBuild) _projectRefreshAfterBuild = true; return; }
        if (Workspace is not { } workspace) return;
        // Without a worker there is nothing to refresh in place. A build used to bring one back by reloading, and still does.
        if (!_workspace.IsConnected) { if (afterBuild) ReloadWhenFreeAsync().Forget(ReportBackgroundFault); return; }
        // A load or a reload holds the gate and reads the projects itself; a change seen meanwhile is looked at again once it is done.
        if (_loading || IsBusy) { if (!afterBuild) ScheduleProjectModelRefresh(); return; }
        _projectRefreshRunning = true;
        _projectRefreshAgain = false;
        var configuration = Configuration;
        var framework = Path.GetExtension(workspace.Path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? TargetFramework : null;
        if (status is not null) { Status = status; _statusShowsModelRefresh = true; }
        try
        {
            var snapshot = await _workspace.RefreshAsync(new(workspace.Path, configuration, framework), _lifetime.Token);
            // A workspace opened or reloaded meanwhile restarted the worker and supersedes this result.
            if (_loading || !ReferenceEquals(Workspace, workspace) || Configuration != configuration) return;
            var startup = StartupProject?.ProjectPath;
            Workspace = snapshot;
            ApplyWorkspaceProjects(startup);
            foreach (var issue in snapshot.Issues.Where(issue => issue.Severity != "Info")) AppendOutput(issue.Severity + ": " + issue.Message);
            foreach (var document in Documents.ToArray()) await document.SyncAsync(_lifetime.Token);
            await RefreshWpfAsync();
            XamlAnalysis.Request();
            if (_statusShowsModelRefresh) Status = "Project model refreshed";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // The loaded model stays in use. The next change to a project file tries again.
            AppendOutput("Project model refresh: " + exception.Message);
            if (_statusShowsModelRefresh) Status = "The project model could not be refreshed; see Output. Reload the workspace to try again.";
        }
        finally
        {
            _projectRefreshRunning = false;
            if (_projectRefreshAfterBuild) { _projectRefreshAfterBuild = false; RefreshProjectModelAsync(status: null).Forget(ReportBackgroundFault); }
            else if (_projectRefreshAgain) ScheduleProjectModelRefresh();
        }
    }
}
