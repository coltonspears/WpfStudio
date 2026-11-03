using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class OperationGateTests
{
    [Fact]
    public void OneOperationHoldsTheGateUntilItsLeaseIsDisposed()
    {
        var changes = new List<bool>();
        var gate = new OperationGate(changes.Add);

        var first = gate.TryEnter();
        Assert.NotNull(first);
        Assert.True(gate.IsBusy);
        Assert.Null(gate.TryEnter());

        first!.Dispose();
        Assert.False(gate.IsBusy);
        using var second = gate.TryEnter();
        Assert.NotNull(second);
        Assert.Equal([true, false, true], changes);
    }

    [Fact]
    public void AnOldLeaseCannotFreeANewerHolder()
    {
        var gate = new OperationGate(_ => { });
        var first = gate.TryEnter()!;
        first.Dispose();
        using var second = gate.TryEnter();

        first.Dispose();

        Assert.True(gate.IsBusy);
    }

    [Fact]
    public void TheHoldersTokenCancelsFromEachRoute()
    {
        using var lifetime = new CancellationTokenSource();
        using var command = new CancellationTokenSource();
        var gate = new OperationGate(_ => { }, lifetime.Token);

        using (var stopButton = gate.TryEnter())
        {
            Assert.False(stopButton!.Token.IsCancellationRequested);
            gate.Cancel();
            Assert.True(stopButton.Token.IsCancellationRequested);
        }
        using (var ownCommand = gate.TryEnter(command.Token))
        {
            command.Cancel();
            Assert.True(ownCommand!.Token.IsCancellationRequested);
        }
        using (var shutdown = gate.TryEnter())
        {
            lifetime.Cancel();
            Assert.True(shutdown!.Token.IsCancellationRequested);
        }
        gate.Cancel();
    }
}

public sealed class WorkspaceOperationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);
    private const string WaitMessage = "Wait for the current workspace operation to finish";

    private static (IAsyncRelayCommand Command, ICommand Cancel) Operation(ShellViewModel shell, string name) => name switch
    {
        "Build" => (shell.BuildCommand, shell.BuildCancelCommand),
        "Restore" => (shell.RestoreCommand, shell.RestoreCancelCommand),
        "Rebuild" => (shell.RebuildCommand, shell.RebuildCancelCommand),
        "Clean" => (shell.CleanCommand, shell.CleanCancelCommand),
        "Run" => (shell.RunCommand, shell.RunCancelCommand),
        "Test" => (shell.TestCommand, shell.TestCancelCommand),
        "BuildSelected" => (shell.BuildSelectedCommand, shell.BuildSelectedCancelCommand),
        "RebuildSelected" => (shell.RebuildSelectedCommand, shell.RebuildSelectedCancelCommand),
        "CleanSelected" => (shell.CleanSelectedCommand, shell.CleanSelectedCancelCommand),
        "RestoreSelected" => (shell.RestoreSelectedCommand, shell.RestoreSelectedCancelCommand),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static IAsyncRelayCommand[] WorkspaceOperations(ShellViewModel shell) =>
    [
        shell.BuildCommand, shell.RestoreCommand, shell.RebuildCommand, shell.CleanCommand, shell.RunCommand, shell.TestCommand,
        shell.BuildSelectedCommand, shell.RebuildSelectedCommand, shell.CleanSelectedCommand, shell.RestoreSelectedCommand,
        shell.DebugCommand, shell.RunWithInspectionCommand, shell.DebugWithInspectionCommand, shell.ReloadWorkspaceCommand
    ];

    /// <summary>Every MSBuild run of this project waits far longer than any test, so only cancellation can end it.</summary>
    private static Task<string> CreateHangingProjectAsync(ShellTestContext test) => test.CreateFileAsync("Hang.csproj", """
        <Project Sdk="Microsoft.NET.Sdk" InitialTargets="Hang">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <Target Name="Hang">
            <Touch Files="$(MSBuildProjectDirectory)\started.marker" AlwaysCreate="true" />
            <Exec Command="ping -n 300 127.0.0.1 &gt; nul" />
          </Target>
        </Project>
        """);

    private static async Task UntilMsBuildStartedAsync(ShellTestContext test)
    {
        var marker = Path.Combine(test.Root, "started.marker");
        using var patience = new CancellationTokenSource(Patience);
        while (!File.Exists(marker)) await Task.Delay(50, patience.Token);
    }

    /// <summary>Starts a build that holds the gate at the save prompt, with no process, until the returned source is completed.</summary>
    private static async Task<(Task Build, TaskCompletionSource Release)> HoldTheGateAsync(ShellTestContext test)
    {
        var path = await test.CreateFileAsync("Edited.cs", "original");
        await test.Shell.OpenDocumentAsync(path);
        test.Shell.ActiveDocument!.State.Content = "edited";
        await File.WriteAllTextAsync(path, "changed on disk");
        test.Shell.Workspace = new(Path.Combine(test.Root, "App.csproj"), "Test", [], []);
        var release = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        test.Dialogs.WhileConfirming = _ => { reached.TrySetResult(); return release.Task; };
        var build = test.Shell.BuildCommand.ExecuteAsync(null);
        await reached.Task.WaitAsync(Patience);
        return (build, release);
    }

    [Theory]
    [InlineData("Build")]
    [InlineData("Restore")]
    [InlineData("Rebuild")]
    [InlineData("Clean")]
    [InlineData("Run")]
    [InlineData("Test")]
    [InlineData("BuildSelected")]
    [InlineData("RebuildSelected")]
    [InlineData("CleanSelected")]
    [InlineData("RestoreSelected")]
    public async Task CancellingABuildOperationEndsItsProcessQuietly(string name)
    {
        await using var test = new ShellTestContext();
        var project = await CreateHangingProjectAsync(test);
        test.Shell.Workspace = new(project, "Test", [], []);
        if (name.EndsWith("Selected", StringComparison.Ordinal)) test.Shell.Explorer.SelectedNode = new ExplorerNode("Hang", project, ExplorerNodeKind.Project);
        var (command, cancel) = Operation(test.Shell, name);

        var running = command.ExecuteAsync(null);
        await UntilMsBuildStartedAsync(test);
        Assert.True(test.Shell.IsBusy);
        Assert.True(cancel.CanExecute(null));
        cancel.Execute(null);
        await running.WaitAsync(Patience);

        Assert.Equal("Cancelled", test.Shell.Status);
        Assert.False(test.Shell.IsBusy);
        // A cancelled or faulted task would reach the unhandled-fault reporter through the command's async void Execute.
        Assert.True(command.ExecutionTask!.IsCompletedSuccessfully);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task TheToolbarStopCancelsWhicheverOperationIsRunning()
    {
        await using var test = new ShellTestContext();
        var project = await CreateHangingProjectAsync(test);
        test.Shell.Workspace = new(project, "Test", [], []);
        Assert.False(test.Shell.CancelOperationCommand.CanExecute(null));

        var running = test.Shell.RebuildCommand.ExecuteAsync(null);
        await UntilMsBuildStartedAsync(test);
        Assert.True(test.Shell.CancelOperationCommand.CanExecute(null));
        test.Shell.CancelOperationCommand.Execute(null);
        await running.WaitAsync(Patience);

        Assert.Equal("Cancelled", test.Shell.Status);
        Assert.False(test.Shell.CancelOperationCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancellingADebugLaunchEndsItsBuild()
    {
        var adapter = Path.Combine(Path.GetTempPath(), "WpfStudio-adapter-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(adapter, "");
        try
        {
            await using var test = new ShellTestContext(configure: services => services.AddSingleton<IDebugAdapterProvider>(new FakeAdapter(adapter)));
            var project = await CreateHangingProjectAsync(test);
            var startup = new WorkspaceProject("id", "Hang", project, "net10.0", null, true, []);
            test.Shell.Workspace = new(project, "Test", [startup], []);
            test.Shell.StartupProject = startup;

            var running = test.Shell.DebugCommand.ExecuteAsync(null);
            await UntilMsBuildStartedAsync(test);
            test.Shell.DebugCancelCommand.Execute(null);
            await running.WaitAsync(Patience);

            Assert.False(test.Shell.IsBusy);
            Assert.False(test.Shell.Debugger.IsActive);
            Assert.True(test.Shell.DebugCommand.ExecutionTask!.IsCompletedSuccessfully);
        }
        finally { File.Delete(adapter); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingAnInspectionLaunchEndsItsBuild(bool debug)
    {
        // The launch stops at its build, so these only have to exist. This test project does not copy the real ones.
        var hooks = Path.Combine(AppContext.BaseDirectory, "Inspection");
        var ownsHooks = !Directory.Exists(hooks);
        var adapter = Path.Combine(Path.GetTempPath(), "WpfStudio-adapter-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(adapter, "");
        try
        {
            Directory.CreateDirectory(hooks);
            foreach (var file in new[] { "WpfStudio.Inspection.StartupHook.dll", "WpfStudio.Inspection.Agent.dll", "WpfStudio.Inspection.Protocol.dll", "WpfStudio.Wpf.PropertyEditing.dll", "WpfStudio.Wpf.Diagnostics.dll" })
                await File.WriteAllTextAsync(Path.Combine(hooks, file), "");
            await using var test = new ShellTestContext(configure: services => services.AddSingleton<IDebugAdapterProvider>(new FakeAdapter(adapter)));
            var project = await CreateHangingProjectAsync(test);
            var startup = new WorkspaceProject("id", "Hang", project, "net10.0", null, true, []);
            test.Shell.Workspace = new(project, "Test", [startup], []);
            test.Shell.StartupProject = startup;
            var (command, cancel) = debug
                ? ((IAsyncRelayCommand)test.Shell.DebugWithInspectionCommand, (ICommand)test.Shell.DebugWithInspectionCancelCommand)
                : (test.Shell.RunWithInspectionCommand, test.Shell.RunWithInspectionCancelCommand);

            var running = command.ExecuteAsync(null);
            await UntilMsBuildStartedAsync(test);
            Assert.True(test.Shell.IsBusy);
            cancel.Execute(null);
            await running.WaitAsync(Patience);

            Assert.False(test.Shell.IsBusy);
            Assert.Contains("Inspection launch cancelled", test.Shell.Status);
            Assert.True(command.ExecutionTask!.IsCompletedSuccessfully);
        }
        finally
        {
            File.Delete(adapter);
            if (ownsHooks) Directory.Delete(hooks, recursive: true);
        }
    }

    [Fact]
    public async Task CancellingAReloadStopsTheLoadAndSaysWhatIsLeft()
    {
        await using var test = new ShellTestContext();
        var project = await CreateHangingProjectAsync(test);
        test.Shell.Workspace = new(project, "Test", [], []);

        var reload = test.Shell.ReloadWorkspaceCommand.ExecuteAsync(null);
        Assert.True(test.Shell.IsBusy);
        test.Shell.ReloadWorkspaceCancelCommand.Execute(null);
        await reload.WaitAsync(Patience);

        Assert.Contains("Reload cancelled", test.Shell.Status);
        Assert.False(test.Shell.IsBusy);
        Assert.False(test.Workspace.IsConnected);
        Assert.True(reload.IsCompletedSuccessfully);
        Assert.True(test.Shell.ReloadWorkspaceCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheToolbarStopCancelsAWorkspaceLoadThatNoCommandStarted()
    {
        await using var test = new ShellTestContext();
        var project = await CreateHangingProjectAsync(test);

        var opening = test.Shell.OpenRecentCommand.ExecuteAsync(project);
        Assert.True(test.Shell.IsBusy);
        test.Shell.CancelOperationCommand.Execute(null);
        await opening.WaitAsync(Patience);

        Assert.Equal("Cancelled", test.Shell.Status);
        Assert.False(test.Shell.IsBusy);
        Assert.True(opening.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhileOneOperationRunsEveryOtherIsRefused()
    {
        await using var test = new ShellTestContext(configure: services => services.AddSingleton(new WorkspaceClient(Path.Combine(Path.GetTempPath(), "no-such-host.dll"))));
        var (build, release) = await HoldTheGateAsync(test);

        Assert.True(test.Shell.IsBusy);
        Assert.True(test.Shell.CancelOperationCommand.CanExecute(null));
        foreach (var command in WorkspaceOperations(test.Shell)) Assert.False(command.CanExecute(null));

        // A build clears Output when it starts. ExecuteAsync skips CanExecute, so these reach the gate itself.
        test.Shell.AppendOutput("the first build's output");
        await test.Shell.RunCommand.ExecuteAsync(null);
        await test.Shell.TestCommand.ExecuteAsync(null);
        await test.Shell.CleanCommand.ExecuteAsync(null);
        Assert.Contains("the first build's output", test.Shell.Output);
        await test.Shell.LoadWorkspaceAsync(Path.Combine(test.Root, "App.csproj"));
        Assert.Equal(WaitMessage, test.Shell.Status);
        Assert.False(build.IsCompleted);

        release.SetResult();
        await build.WaitAsync(Patience);
        Assert.False(test.Shell.IsBusy);
        foreach (var command in WorkspaceOperations(test.Shell)) Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ABoundButtonIsToldTheCommandIsAvailableAgain()
    {
        await using var test = new ShellTestContext();
        var (build, release) = await HoldTheGateAsync(test);
        var seen = new List<bool>();
        test.Shell.BuildCommand.CanExecuteChanged += (_, _) => seen.Add(test.Shell.BuildCommand.CanExecute(null));

        release.SetResult();
        await build.WaitAsync(Patience);

        // The gate frees just before the command's task ends; the last notification must come after both.
        using var patience = new CancellationTokenSource(Patience);
        while (seen.Count == 0 || !seen[^1]) await Task.Delay(10, patience.Token);
    }

    [Fact]
    public async Task AReloadRequestedDuringAnOperationRunsWhenItEnds()
    {
        await using var test = new ShellTestContext(configure: services => services.AddSingleton(new WorkspaceClient(Path.Combine(Path.GetTempPath(), "no-such-host.dll"))));
        var (build, release) = await HoldTheGateAsync(test);
        var statuses = new List<string>();
        test.Shell.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ShellViewModel.Status)) statuses.Add(test.Shell.Status); };

        await test.Shell.ReloadWorkspaceCommand.ExecuteAsync(null);
        Assert.DoesNotContain("Updating project context…", statuses);
        Assert.False(build.IsCompleted);

        release.SetResult();
        await build.WaitAsync(Patience);
        await test.Shell.ReloadWorkspaceCommand.ExecutionTask!.WaitAsync(Patience);

        Assert.Contains("Updating project context…", statuses);
        Assert.False(test.Shell.IsBusy);
    }

    [Fact]
    public async Task ALaunchWaitingOnTheInstallPromptDoesNotTakeOverARunningOperation()
    {
        // B12: the prompt keeps the dispatcher running, so a build can start behind it.
        var adapter = Path.Combine(Path.GetTempPath(), "WpfStudio-adapter-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            await using var test = new ShellTestContext(configure: services => services.AddSingleton<IDebugAdapterProvider>(new FakeAdapter(adapter)));
            var path = await test.CreateFileAsync("Edited.cs", "original");
            await test.Shell.OpenDocumentAsync(path);
            test.Shell.ActiveDocument!.State.Content = "edited";
            await File.WriteAllTextAsync(path, "changed on disk");
            var startup = new WorkspaceProject("id", "App", Path.Combine(test.Root, "App.csproj"), "net10.0", null, true, []);
            test.Shell.Workspace = new(startup.ProjectPath, "Test", [startup], []);
            test.Shell.StartupProject = startup;
            var statuses = new List<string>();
            test.Shell.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ShellViewModel.Status)) statuses.Add(test.Shell.Status); };

            TaskCompletionSource installAnswered = new(), saveAnswered = new(), installAsked = new(), saveAsked = new();
            test.Dialogs.WhileConfirming = title =>
            {
                if (title == "Install the debugger") { installAsked.TrySetResult(); return installAnswered.Task; }
                saveAsked.TrySetResult(); return saveAnswered.Task;
            };
            var debug = test.Shell.DebugCommand.ExecuteAsync(null);
            await installAsked.Task.WaitAsync(Patience);
            Assert.False(test.Shell.IsBusy);

            var build = test.Shell.BuildCommand.ExecuteAsync(null);
            await saveAsked.Task.WaitAsync(Patience);
            Assert.True(test.Shell.IsBusy);

            test.Dialogs.Confirmations.Enqueue(true);
            installAnswered.SetResult();
            await debug.WaitAsync(Patience);

            Assert.Contains(WaitMessage, statuses);
            Assert.DoesNotContain("Building for debug…", statuses);
            Assert.False(build.IsCompleted);
            Assert.True(test.Shell.IsBusy);

            saveAnswered.SetResult();
            await build.WaitAsync(Patience);
            Assert.False(test.Shell.IsBusy);
        }
        finally { File.Delete(adapter); }
    }

    [Fact]
    public async Task AFaultStaysInTheStatusBarAndFreesTheGate()
    {
        await using var test = new ShellTestContext();
        test.Shell.Workspace = new(Path.Combine(test.Root, "Missing.csproj"), "Test", [], []);

        await test.Shell.BuildCommand.ExecuteAsync(null);

        Assert.Equal("Build target does not exist.", test.Shell.Status);
        Assert.False(test.Shell.IsBusy);
        Assert.True(test.Shell.BuildCommand.ExecutionTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ShuttingDownCancelsTheRunningOperation()
    {
        await using var test = new ShellTestContext();
        var project = await CreateHangingProjectAsync(test);
        test.Shell.Workspace = new(project, "Test", [], []);

        var running = test.Shell.BuildCommand.ExecuteAsync(null);
        await UntilMsBuildStartedAsync(test);
        Assert.True(await test.Shell.TryCloseAsync());
        await running.WaitAsync(Patience);

        Assert.False(test.Shell.IsBusy);
    }

    private sealed class FakeAdapter(string path) : IDebugAdapterProvider
    {
        public string Description => "the test adapter";
        public string? Locate() => path;
        public async Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(path, "", cancellationToken);
            return path;
        }
    }
}
