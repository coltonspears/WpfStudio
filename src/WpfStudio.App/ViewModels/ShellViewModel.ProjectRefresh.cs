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

    private async Task RefreshChangedProjectModelAsync()
    {
        if (_projectRefreshRunning || !XamlAnalysis.ProjectReloadRequired || Workspace is not { } workspace || !_workspace.IsConnected) return;
        // A build, a load or a reload holds the gate. It reloads the model itself when it succeeds; otherwise look again once it is done.
        if (_loading || IsBusy) { ScheduleProjectModelRefresh(); return; }
        _projectRefreshRunning = true;
        _projectRefreshAgain = false;
        var configuration = Configuration;
        var framework = Path.GetExtension(workspace.Path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? TargetFramework : null;
        Status = "Project files changed on disk. Refreshing the project model…";
        _statusShowsModelRefresh = true;
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
            if (_projectRefreshAgain) ScheduleProjectModelRefresh();
        }
    }
}
