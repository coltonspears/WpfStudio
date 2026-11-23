using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Inspection;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Inspection;
using WpfStudio.Workspace;

namespace WpfStudio.App.ViewModels;

internal interface IBuildAndLaunchHost
{
    WorkspaceSnapshot? Workspace { get; }
    WorkspaceProject? StartupProject { get; }
    string Configuration { get; }
    string? TargetFramework { get; set; }
    string? LaunchProfile { get; set; }
    string Status { get; set; }
    string Output { get; set; }
    ObservableCollection<WorkspaceProject> Projects { get; }
    ObservableCollection<string> TargetFrameworks { get; }
    ObservableCollection<string> LaunchProfiles { get; }
    BuildService Build { get; }
    IUiDispatcher Dispatcher { get; }
    IUserDialogService Dialogs { get; }
    SolutionExplorerViewModel Explorer { get; }
    DebuggerViewModel Debugger { get; }
    InspectionViewModel LiveInspection { get; }
    InspectionSourceVerifier InspectionSources { get; }
    IAsyncRelayCommand RunCommand { get; }
    Task GuardAsync(Func<Task> action);
    void RequestTool(string name);
    void AppendOutput(string line);
    void RefreshEditorDiagnostics();
    Task RefreshProjectModelAfterBuildAsync();
    void ReportBackgroundFault(Exception fault);
}

/// <summary>
/// Orchestrates build/run/test/debug/inspection launches, launch-choice discovery, and build diagnostic aggregation.
/// </summary>
internal sealed class BuildAndLaunchCoordinator(
    IBuildAndLaunchHost host,
    WorkspaceLifecycleCoordinator lifecycle,
    DocumentSessionManager documents)
{
    private readonly List<WorkspaceDiagnostic> _buildDiagnostics = [];
    private int _launchChoicesGeneration;

    public IReadOnlyList<WorkspaceDiagnostic> BuildDiagnostics => _buildDiagnostics;

    public void ClearBuildDiagnostics() => _buildDiagnostics.Clear();

    public async Task RefreshLaunchChoicesAsync()
    {
        if (host.StartupProject == null) return;
        var path = host.StartupProject.ProjectPath;
        var configuration = host.Configuration;
        var generation = ++_launchChoicesGeneration;
        var targets = await host.Build.DiscoverLaunchTargetsAsync(path, configuration: configuration);
        if (generation != _launchChoicesGeneration || host.StartupProject?.ProjectPath != path || host.Configuration != configuration) return;
        var previousFramework = host.TargetFramework; var previousProfile = host.LaunchProfile;
        using (lifecycle.BeginUpdatingChoices())
        {
            host.TargetFrameworks.Clear(); host.LaunchProfiles.Clear();
            var target = targets.FirstOrDefault();
            if (target != null)
            {
                foreach (var framework in target.TargetFrameworks) host.TargetFrameworks.Add(framework);
                foreach (var profile in target.Profiles) host.LaunchProfiles.Add(profile.Name);
            }
            host.TargetFramework = previousFramework != null && host.TargetFrameworks.Contains(previousFramework) ? previousFramework : host.TargetFrameworks.FirstOrDefault() ?? host.StartupProject.TargetFramework;
            host.LaunchProfile = previousProfile != null && host.LaunchProfiles.Contains(previousProfile) ? previousProfile : host.LaunchProfiles.FirstOrDefault();
        }
    }

    /// <summary>Builds the solution or project chosen in Solution Explorer instead of the whole workspace.</summary>
    public Task RunSelectedBuildAsync(BuildOperation operation, CancellationToken cancel)
    {
        if (host.Explorer.SelectedNode is { CanBuild: true } node) return RunBuildAsync(operation, cancel, node.Path);
        host.Status = "Select a solution or project in Solution Explorer";
        return Task.CompletedTask;
    }

    /// <summary>The opened solution, for a project built from it.</summary>
    public string? SolutionPathFor(string path) => host.Workspace is { } workspace && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        && !workspace.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? workspace.Path : null;

    public Task RunBuildAsync(BuildOperation operation, CancellationToken cancel, string? target = null) => host.GuardAsync(async () =>
    {
        if (host.Workspace == null || lifecycle.TryEnter(cancel) is not { } lease) return;
        host.RequestTool(Workbenches.Output); host.Output = "";
        var refreshModel = false;
        try
        {
            if (!await documents.SaveAllDocumentsAsync()) return;
            var path = target ?? (operation == BuildOperation.Run ? host.StartupProject?.ProjectPath ?? host.Workspace.Path : host.Workspace.Path);
            var label = target == null ? operation.ToString() : $"{operation} {Path.GetFileNameWithoutExtension(path)}";
            host.Status = label + "…";
            // The selected framework belongs to the startup project; another project builds each of its frameworks.
            var framework = Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                && (target == null || path.Equals(host.StartupProject?.ProjectPath, StringComparison.OrdinalIgnoreCase)) ? host.TargetFramework : null;
            using var progress = CreateBuildProgress();
            var result = await host.Build.RunAsync(new(path, operation, host.Configuration, framework, host.LaunchProfile, SolutionPath: SolutionPathFor(path)), progress, lease.Token);
            progress.Flush();
            host.Status = result.Cancelled ? "Cancelled" : result.ExitCode == 0 ? label + " succeeded" : label + $" failed (exit {result.ExitCode})";
            refreshModel = result.ExitCode == 0 && operation is BuildOperation.Build or BuildOperation.Rebuild or BuildOperation.Restore;
        }
        finally { lifecycle.EndOperation(lease); }
        // Restore and build write the assets and generated sources the project model reads. The model refreshes in place, as for
        // an edited project file: the language service keeps answering, the gate is free at once, and the build's result stays shown.
        // Reloading inside the gate restarted the worker and kept the whole shell busy for a full MSBuild load after every build.
        if (refreshModel) host.RefreshProjectModelAfterBuildAsync().Forget(host.ReportBackgroundFault);
    });

    public Task DebugAsync(CancellationToken cancel) => host.GuardAsync(async () =>
    {
        if (host.Debugger.IsStopped) { await host.Debugger.ContinueCommand.ExecuteAsync(null); return; }
        if (host.Debugger.IsActive) { host.Status = "The debugger is already running"; return; }
        if (lifecycle.IsBusy || host.StartupProject == null) return;
        if (ProjectDiscovery.TargetsNetFramework(host.StartupProject, host.TargetFramework))
        {
            // netcoredbg debugs .NET (Core) processes only; the .NET Framework runtime needs Visual Studio's debugger.
            if (await host.Dialogs.ConfirmAsync("Run without debugging", $"{host.StartupProject.Name} targets .NET Framework. The bundled debugger supports .NET 5 and later only. Run it without debugging instead?"))
                await WorkspaceLifecycleCoordinator.ExecuteWhenAvailableAsync(host.RunCommand);
            else host.Status = "Debugging .NET Framework applications isn't supported. Use Run without debugging (Ctrl+F5).";
            return;
        }
        if (!await EnsureDebuggerAsync()) return;
        if (host.StartupProject == null) return;
        // The install prompt keeps the dispatcher running, so a build may have started behind it.
        if (lifecycle.TryEnter(cancel) is not { } lease) { host.Status = "Wait for the current workspace operation to finish"; return; }
        host.RequestTool(Workbenches.Debugger);
        try
        {
            if (!await documents.SaveAllDocumentsAsync()) return;
            if (host.StartupProject is not { } project) return;
            var configuration = host.Configuration;
            var framework = host.TargetFramework;
            var profile = host.LaunchProfile;
            host.Status = "Building for debug…";
            using var progress = CreateBuildProgress();
            var result = await host.Build.RunAsync(new(project.ProjectPath, BuildOperation.Build, configuration, framework, SolutionPath: SolutionPathFor(project.ProjectPath)), progress, lease.Token);
            progress.Flush();
            if (result.ExitCode != 0 || result.Cancelled) { host.Status = "Debug launch cancelled: build did not succeed"; return; }
            var launch = await host.Build.ResolveLaunchAsync(project.ProjectPath, configuration, framework, profile, lease.Token);
            await host.Debugger.LaunchAsync(new DebugLaunchConfiguration(launch.Program, launch.WorkingDirectory, launch.Arguments, launch.Environment), lease.Token);
            host.Status = "Debugging " + project.Name;
        }
        finally { lifecycle.EndOperation(lease); }
    });

    /// <summary>Offers to install the debug adapter when it is missing; false when debugging cannot start.</summary>
    public async Task<bool> EnsureDebuggerAsync()
    {
        if (!host.Debugger.IsAdapterMissing) return true;
        host.RequestTool(Workbenches.Debugger);
        host.Status = "Installing the debugger…";
        var ready = await host.Debugger.EnsureAdapterAsync(() => host.Dialogs.ConfirmAsync("Install the debugger",
            $"Debugging needs {host.Debugger.AdapterDescription}, which isn't installed. Download it now (about 5 MB from GitHub), verify its checksum and install it in your WpfStudio profile?"));
        host.Status = ready ? "Debugger installed" : "Debugging needs the debugger. Choose Install debugger in the Debugger pane.";
        return ready;
    }

    public Task LaunchWithInspectionAsync(bool debug, CancellationToken cancel) => host.GuardAsync(async () =>
    {
        if (lifecycle.IsBusy) return;
        if (host.StartupProject is not { } project) { host.Status = "Select a startup project before launching with inspection."; return; }
        if (debug && host.Debugger.IsActive) { host.Status = "Stop the current debugger before launching another application."; return; }
        if (debug && !await EnsureDebuggerAsync()) return;
        var framework = host.TargetFramework ?? project.TargetFramework;
        if (framework == null || !new[] { "net8.0", "net9.0", "net10.0" }.Any(prefix => framework == prefix || framework.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)))
        { host.Status = "Running-app inspection currently requires an untrimmed .NET 8, 9 or 10 WPF project."; return; }
        string hook = InspectionLaunch.FindStartupHook();
        // The install prompt above keeps the dispatcher running, so a build may have started behind it.
        if (lifecycle.TryEnter(cancel) is not { } operation) { host.Status = "Wait for the current workspace operation to finish"; return; }
        InspectionSession? session = null, attached = null;
        try
        {
            if (!await documents.SaveAllDocumentsAsync()) return;
            var configuration = host.Configuration; var profile = host.LaunchProfile;
            var sourceWorkspace = host.Workspace;
            host.RequestTool(Workbenches.Output); host.Status = "Building with XAML source information for inspection…";
            using var progress = CreateBuildProgress();
            BuildResult result;
            IReadOnlyList<XamlBuildSource> buildSources;
            // WPF reads its checksum after parsing XAML. Hold ordinary source
            // files read-only for the rebuild so those two reads see identical
            // bytes; a post-build hash alone would not prove that relationship.
            using (var lease = await XamlBuildSourceLease.AcquireAsync((host.LiveInspection.VerifySourceOnBuild ? host.Projects.SelectMany(p => p.Files)
                .Where(f => !f.IsGenerated && f.Kind is "Page" or "ApplicationDefinition")
                .Select(f => f.Path) : []), operation.Token))
            {
                foreach (var issue in lease.Issues) host.AppendOutput("XAML source navigation: " + issue);
                result = await host.Build.RunAsync(new(project.ProjectPath, BuildOperation.Rebuild, configuration,
                    framework, XamlDebuggingInformation: true, SolutionPath: SolutionPathFor(project.ProjectPath)), progress, operation.Token);
                buildSources = lease.Sources;
            }
            progress.Flush();
            if (result.Cancelled || result.ExitCode != 0)
            {
                host.Status = "Inspection launch cancelled: build did not succeed." + (host.LiveInspection.VerifySourceOnBuild
                    ? " If a custom build target rewrites XAML, turn off ‘Verify source on build’ in Live XAML and retry." : "");
                return;
            }
            var launch = await host.Build.ResolveLaunchAsync(project.ProjectPath, configuration, framework, profile, operation.Token);
            await host.LiveInspection.DisconnectCommand.ExecuteAsync(null);
            host.InspectionSources.Capture(buildSources, sourceWorkspace);
            session = new InspectionSession();
            var environment = InspectionLaunch.CreateEnvironment(launch.Environment, session, hook);
            host.RequestTool(Workbenches.LiveInspection);
            var connection = host.LiveInspection.AttachAsync(attached = session, debug, operation.Token);
            // AttachAsync owns disposal from here: it disposes a session it does not adopt, and
            // Disconnect one it did. The launch environment applies to the target only, never the build or adapter.
            session = null;
            if (debug)
            {
                await host.Debugger.LaunchAsync(new DebugLaunchConfiguration(launch.Program, launch.WorkingDirectory,
                    launch.Arguments, environment), operation.Token);
                host.LiveInspection.SetDebuggerState(host.Debugger.IsStopped);
            }
            else
            {
                var start = InspectionLaunch.CreateStartInfo(launch, environment);
                // The debugger owns output capture for debug launches. A standalone
                // application must not block on redirected streams nobody drains.
                start.RedirectStandardOutput = false; start.RedirectStandardError = false;
                using var target = Process.Start(start)
                    ?? throw new InvalidOperationException("The target application could not be launched.");
                attached.ExpectProcess(target.Id);
            }
            await connection;
            host.Status = host.LiveInspection.IsConnected ? "Inspecting " + project.Name : host.LiveInspection.Status;
        }
        catch
        {
            if (attached != null) await host.LiveInspection.DisconnectCommand.ExecuteAsync(null);
            throw;
        }
        finally
        {
            if (session != null) await session.DisposeAsync();
            lifecycle.EndOperation(operation);
        }
    });

    public BufferedBuildProgress CreateBuildProgress()
    {
        _buildDiagnostics.Clear(); host.RefreshEditorDiagnostics();
        return new(host.Dispatcher, batch =>
        {
            host.AppendOutput(string.Join(Environment.NewLine, batch.Select(e => e.Text)));
            foreach (var entry in batch.Where(e => e.Diagnostic != null))
            {
                if (_buildDiagnostics.Count >= 2000) _buildDiagnostics.RemoveAt(0);
                _buildDiagnostics.Add(entry.Diagnostic!);
            }
            host.RefreshEditorDiagnostics();
        });
    }
}
