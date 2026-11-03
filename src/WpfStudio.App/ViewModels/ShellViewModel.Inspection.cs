using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Inspection;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    public InspectionViewModel LiveInspection { get; }
    [RelayCommand] private Task RunWithInspectionAsync() => LaunchWithInspectionAsync(false);
    [RelayCommand] private Task DebugWithInspectionAsync() => LaunchWithInspectionAsync(true);
    private Task LaunchWithInspectionAsync(bool debug) => GuardAsync(async () =>
    {
        if (IsBusy || _loading) return;
        if (StartupProject is not { } project) { Status = "Select a startup project before launching with inspection."; return; }
        if (debug && Debugger.IsActive) { Status = "Stop the current debugger before launching another application."; return; }
        if (debug && !await EnsureDebuggerAsync()) return;
        // The install prompt above keeps the dispatcher running; a build started meanwhile owns the operation token.
        if (IsBusy || _loading) { Status = "Wait for the current workspace operation to finish"; return; }
        var framework = TargetFramework ?? project.TargetFramework;
        if (framework == null || !new[] { "net8.0", "net9.0", "net10.0" }.Any(prefix => framework == prefix || framework.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)))
        { Status = "Running-app inspection currently requires an untrimmed .NET 8, 9 or 10 WPF project."; return; }
        string hook = InspectionLaunch.FindStartupHook();
        IsBusy = true; _operation = new();
        InspectionSession? session = null, attached = null;
        try
        {
            if (!await SaveAllDocumentsAsync()) return;
            var configuration = Configuration; var profile = LaunchProfile;
            var sourceWorkspace = Workspace;
            ToolRequested?.Invoke(Workbenches.Output); Status = "Building with XAML source information for inspection…";
            using var progress = CreateBuildProgress();
            BuildResult result;
            IReadOnlyList<XamlBuildSource> buildSources;
            // WPF reads its checksum after parsing XAML. Hold ordinary source
            // files read-only for the rebuild so those two reads see identical
            // bytes; a post-build hash alone would not prove that relationship.
            using (var lease = await XamlBuildSourceLease.AcquireAsync((LiveInspection.VerifySourceOnBuild ? Projects.SelectMany(p => p.Files)
                .Where(f => !f.IsGenerated && f.Kind is "Page" or "ApplicationDefinition")
                .Select(f => f.Path) : []), _operation.Token))
            {
                foreach (var issue in lease.Issues) AppendOutput("XAML source navigation: " + issue);
                result = await _build.RunAsync(new(project.ProjectPath, BuildOperation.Rebuild, configuration,
                    framework, XamlDebuggingInformation: true, SolutionPath: SolutionPathFor(project.ProjectPath)), progress, _operation.Token);
                buildSources = lease.Sources;
            }
            progress.Flush();
            if (result.Cancelled || result.ExitCode != 0)
            {
                Status = "Inspection launch cancelled: build did not succeed." + (LiveInspection.VerifySourceOnBuild
                    ? " If a custom build target rewrites XAML, turn off ‘Verify source on build’ in Live XAML and retry." : "");
                return;
            }
            var launch = await _build.ResolveLaunchAsync(project.ProjectPath, configuration, framework, profile, _operation.Token);
            await LiveInspection.DisconnectCommand.ExecuteAsync(null);
            InspectionSources.Capture(buildSources, sourceWorkspace);
            session = new InspectionSession();
            var environment = InspectionLaunch.CreateEnvironment(launch.Environment, session, hook);
            ToolRequested?.Invoke(Workbenches.LiveInspection);
            var connection = LiveInspection.AttachAsync(attached = session, debug, _operation.Token);
            // AttachAsync owns disposal from here: it disposes a session it does not adopt, and
            // Disconnect one it did. The launch environment applies to the target only, never the build or adapter.
            session = null;
            if (debug)
            {
                await Debugger.LaunchAsync(new DebugLaunchConfiguration(launch.Program, launch.WorkingDirectory,
                    launch.Arguments, environment), _operation.Token);
                LiveInspection.SetDebuggerState(Debugger.IsStopped);
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
            Status = LiveInspection.IsConnected ? "Inspecting " + project.Name : LiveInspection.Status;
        }
        catch
        {
            if (attached != null) await LiveInspection.DisconnectCommand.ExecuteAsync(null);
            throw;
        }
        finally
        {
            if (session != null) await session.DisposeAsync();
            _operation.Dispose(); _operation = null; IsBusy = false;
            if (_contextReloadPending) _ = ReloadWorkspaceAsync();
        }
    });
}
