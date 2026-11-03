using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using WpfStudio.Hosting;

namespace WpfStudio.Workspace.Tests;

public sealed class WorkerProcessHostTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("WpfStudio-WorkerHost-").FullName;
    private static string NewPipeName() => "wpfstudio-test-" + Guid.NewGuid().ToString("N");

    /// <summary>A batch file stands in for a worker; it receives the same arguments and is launched the same way.</summary>
    private string Worker(string body)
    {
        var path = Path.Combine(_directory, "worker-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return path;
    }
    private string StalledWorker() => Worker("ping -n 120 127.0.0.1 > nul");

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task AWorkerThatExitsReportsItsCodeAndStderrWithoutWaitingOutItsBudget()
    {
        using var worker = new WorkerProcessHost(new(Worker("for /L %%i in (1,1,30) do echo line %%i 1>&2\r\nexit /b 7"), NewPipeName(), TimeSpan.FromMinutes(2)));
        worker.Start();
        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<WorkerConnectionException>(() => worker.ConnectAsync(CancellationToken.None));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), clock.Elapsed.ToString());
        Assert.Equal(7, failure.ExitCode);
        Assert.Contains("exited with code 7", failure.Message);
        var lines = failure.ErrorTail.Split(Environment.NewLine, StringSplitOptions.TrimEntries);
        Assert.Equal(20, lines.Length);
        Assert.Equal("line 11", lines[0]);
        Assert.Equal("line 30", lines[^1]);
    }

    [Fact]
    public async Task TheStderrTailIsBoundedInCharactersAsWellAsLines()
    {
        var wide = new string('x', 900);
        using var worker = new WorkerProcessHost(new(Worker($"for /L %%i in (1,1,12) do echo {wide}%%i 1>&2\r\nexit /b 1"), NewPipeName(), TimeSpan.FromMinutes(2)));
        worker.Start();
        await Assert.ThrowsAsync<WorkerConnectionException>(() => worker.ConnectAsync(CancellationToken.None));
        Assert.InRange(worker.ErrorTail.Length, 900, 4000 + 4 * Environment.NewLine.Length);
        Assert.Contains("x12", worker.ErrorTail);
    }

    [Fact]
    public async Task AWorkerThatNeverListensRunsOutItsBudgetAndDisposingEndsIt()
    {
        var worker = new WorkerProcessHost(new(StalledWorker(), NewPipeName(), TimeSpan.FromMilliseconds(300)) { KillProcessTree = true });
        worker.Start();
        using var process = Process.GetProcessById(worker.ProcessId);
        var failure = await Assert.ThrowsAsync<WorkerConnectionException>(() => worker.ConnectAsync(CancellationToken.None));
        Assert.Null(failure.ExitCode);
        Assert.Contains("did not connect", failure.Message);
        Assert.False(worker.HasExited);
        worker.Dispose();
        Assert.True(worker.HasExited);
        Assert.True(process.WaitForExit(30_000));
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAFailedWorker()
    {
        using var worker = new WorkerProcessHost(new(StalledWorker(), NewPipeName(), TimeSpan.FromMinutes(2)) { KillProcessTree = true });
        worker.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ConnectAsync(cancellation.Token));
        Assert.False(worker.HasExited);
    }

    [Fact]
    public async Task APipeServedByAnotherProcessIsRefused()
    {
        var pipeName = NewPipeName();
        // The test process takes the name before the worker can, as an impostor would.
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = server.WaitForConnectionAsync();
        using var worker = new WorkerProcessHost(new(StalledWorker(), pipeName, TimeSpan.FromSeconds(30)) { KillProcessTree = true });
        worker.Start();
        await Assert.ThrowsAsync<InvalidDataException>(() => worker.ConnectAsync(CancellationToken.None));
        await accepted;
    }

    [Fact]
    public async Task AWorkerBehindALauncherConnectsWhenTheCheckIsOff()
    {
        var pipeName = NewPipeName();
        // The test process serves the pipe as the real host would behind a shim that is still running.
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = server.WaitForConnectionAsync();
        using var worker = new WorkerProcessHost(new(StalledWorker(), pipeName, TimeSpan.FromSeconds(30)) { KillProcessTree = true, VerifyServerProcess = false });
        worker.Start();
        await worker.ConnectAsync(CancellationToken.None);
        await accepted;
        Assert.True(worker.Pipe.IsConnected);
    }

    [Fact]
    public async Task ARealWorkerConnectsAndStops()
    {
        var project = Path.Combine(_directory, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var configuration = typeof(WorkerProcessHostTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var host = WorkerHostLocator.Find("WpfStudio.WorkspaceHost", "WorkspaceHost", "net10.0", [configuration], [".dll"], []);
        var worker = new WorkerProcessHost(new(host, NewPipeName(), TimeSpan.FromSeconds(30))
        {
            WorkingDirectory = _directory, Arguments = ["--workspace", project], KillProcessTree = true
        });
        worker.Start();
        using var process = Process.GetProcessById(worker.ProcessId);
        await worker.ConnectAsync(CancellationToken.None);
        Assert.True(worker.Pipe.IsConnected);
        Assert.True(NamedPipeIdentity.TryGetServerProcessId(worker.Pipe, out var server));
        Assert.Equal(worker.ProcessId, server);
        Assert.True(worker.StartTimeUtc <= DateTime.UtcNow);
        await worker.StopAsync(TimeSpan.FromSeconds(2));
        Assert.True(worker.HasExited);
        Assert.True(process.WaitForExit(30_000));
    }

    [Fact]
    public async Task ExitIsReportedToSubscribers()
    {
        using var worker = new WorkerProcessHost(new(Worker("exit /b 0"), NewPipeName(), TimeSpan.FromSeconds(30)));
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Exited += exited.SetResult;
        worker.Start();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(worker.HasExited);
    }

    [Fact]
    public void AWorkerThatCannotStartThrowsAndHoldsNothing()
    {
        using var worker = new WorkerProcessHost(new(Path.Combine(_directory, "missing.exe"), NewPipeName(), TimeSpan.FromSeconds(1)));
        Assert.Throws<Win32Exception>(worker.Start);
        Assert.True(worker.HasExited);
    }

    [Fact]
    public void ManagedAssembliesRunThroughTheDotnetHostAndExecutablesDirectly()
    {
        var managed = ProcessLaunch.Create(@"C:\tools\Worker.dll", @"C:\work", ["--pipe", "name"]);
        Assert.Equal("dotnet", managed.FileName);
        Assert.Equal([@"C:\tools\Worker.dll", "--pipe", "name"], managed.ArgumentList);
        Assert.Equal(@"C:\work", managed.WorkingDirectory);
        Assert.False(managed.UseShellExecute);
        Assert.True(managed.CreateNoWindow);
        Assert.True(managed.RedirectStandardOutput);
        Assert.True(managed.RedirectStandardError);
        Assert.Equal(@"C:\x86\dotnet.exe", ProcessLaunch.Create(@"C:\tools\Worker.DLL", @"C:\work", [], @"C:\x86\dotnet.exe").FileName);
        var native = ProcessLaunch.Create(@"C:\tools\Worker.exe", @"C:\work", ["--pipe", "name"], @"C:\x86\dotnet.exe");
        Assert.Equal(@"C:\tools\Worker.exe", native.FileName);
        Assert.Equal(["--pipe", "name"], native.ArgumentList);
    }

    [Fact]
    public async Task PipeIdentitiesComeFromTheOperatingSystem()
    {
        var pipeName = NewPipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5_000);
        await accepted;
        Assert.True(NamedPipeIdentity.TryGetServerProcessId(client, out var serverProcess));
        Assert.True(NamedPipeIdentity.TryGetClientProcessId(server, out var clientProcess));
        Assert.Equal(Environment.ProcessId, serverProcess);
        Assert.Equal(Environment.ProcessId, clientProcess);
    }

    [Fact]
    public void TheLocatorPrefersThePackagedWorkerThenASourceCheckoutAndSkipsPartialCopies()
    {
        var application = Directory.CreateDirectory(Path.Combine(_directory, "checkout", "tests", "bin")).FullName;
        string Find(params string[] companions) => WorkerHostLocator.Find("Demo.Host", "Host", "net10.0", ["Release", "Debug"], [".exe", ".dll"], companions, application);
        string Touch(params string[] parts)
        {
            var path = Path.Combine([_directory, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            return path;
        }

        Assert.Equal(Path.Combine(application, "Host", "Demo.Host.exe"), Find());

        var debug = Touch("checkout", "src", "Demo.Host", "bin", "Debug", "net10.0", "Demo.Host.dll");
        Assert.Equal(debug, Find());
        var release = Touch("checkout", "src", "Demo.Host", "bin", "Release", "net10.0", "Demo.Host.dll");
        Assert.Equal(release, Find());

        var partial = Touch("checkout", "tests", "bin", "Host", "Demo.Host.exe");
        Assert.Equal(partial, Find());
        Assert.Equal(Path.Combine(application, "Host", "Demo.Host.exe"), Find("Companion.dll"));
        var companion = Touch("checkout", "src", "Demo.Host", "bin", "Debug", "net10.0", "Companion.dll");
        Assert.Equal(debug, Find(Path.GetFileName(companion)));
        Touch("checkout", "tests", "bin", "Host", "Companion.dll");
        Assert.Equal(partial, Find("Companion.dll"));
    }

    [Fact]
    public async Task WorkspaceClientReportsAWorkerThatExitsWithItsStderr()
    {
        var project = Path.Combine(_directory, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await using var client = new WorkspaceClient(Worker("echo the runtime is missing 1>&2\r\nexit /b 3"));
        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoadAsync(new(project)));
        Assert.Contains("failed to connect", failure.Message);
        Assert.Contains("the runtime is missing", failure.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(25), clock.Elapsed.ToString());
        Assert.False(client.IsConnected);
        Assert.Null(client.WorkerProcessId);
    }
}
