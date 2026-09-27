using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using WpfStudio.Runtime.Debugging;
using Xunit.Abstractions;

namespace WpfStudio.Runtime.Tests;

public sealed class DebuggerIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AttachExceptionDetailsAndDetachLeaveTheDebuggeeRunning(int stopMode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-DebugTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string source = """
using System;
internal class Program
{
    static void Main()
    {
        Console.WriteLine("READY");
        Console.ReadLine();
        int answer = 123;
        Console.WriteLine(answer); // BREAK
        try { throw new InvalidOperationException("fixture exception"); }
        catch (InvalidOperationException) { Console.WriteLine("CAUGHT"); }
        Console.ReadLine();
    }
}
""";
        var sourcePath = Path.Combine(directory, "Program.cs");
        await File.WriteAllTextAsync(sourcePath, source);
        await File.WriteAllTextAsync(Path.Combine(directory, "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><DebugType>portable</DebugType><Optimize>false</Optimize></PropertyGroup></Project>");
        using (var build = Process.Start(new ProcessStartInfo("dotnet", "build --nologo --verbosity quiet") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!)
        {
            var text = build.StandardOutput.ReadToEndAsync(); var error = build.StandardError.ReadToEndAsync();
            await build.WaitForExitAsync(); Assert.True(build.ExitCode == 0, await text + await error);
        }
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(directory, "bin", "Debug", "net10.0", "Fixture.dll"));
        using var debuggee = Process.Start(start)!;
        await using var session = new DebugSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal("READY", await debuggee.StandardOutput.ReadLineAsync(timeout.Token));
            var events = Channel.CreateUnbounded<DebugEvent>(); session.EventReceived += value => events.Writer.TryWrite(value);
            var breakpointLine = Array.FindIndex(source.Split('\n'), line => line.Contains("// BREAK")) + 1;
            await session.AttachAsync(debuggee.Id, [new SourceBreakpoint(sourcePath, breakpointLine)], breakOnThrown: true, timeout.Token);
            await debuggee.StandardInput.WriteLineAsync(); await debuggee.StandardInput.FlushAsync(timeout.Token);
            var breakpoint = await NextEventAsync(events.Reader, "stopped", timeout.Token);
            Assert.Equal("breakpoint", breakpoint.Body.GetProperty("reason").GetString());
            var threadId = breakpoint.Body.GetProperty("threadId").GetInt32();
            await session.ContinueAsync(threadId, timeout.Token);
            var exception = await NextEventAsync(events.Reader, "stopped", timeout.Token);
            Assert.Equal("exception", exception.Body.GetProperty("reason").GetString());
            var details = await session.RequestAsync("exceptionInfo", new { threadId }, timeout.Token);
            Assert.Contains("InvalidOperationException", details.GetProperty("exceptionId").GetString());
            if (stopMode == 0) await session.StopAsync(terminate: false, timeout.Token);
            else if (stopMode == 1) await session.StopAsync(cancellationToken: timeout.Token);
            else await session.DisposeAsync();
            Assert.False(debuggee.HasExited);
            await debuggee.StandardInput.WriteLineAsync(); await debuggee.StandardInput.FlushAsync(timeout.Token);
            await debuggee.WaitForExitAsync(timeout.Token);
        }
        finally { if (!debuggee.HasExited) debuggee.Kill(entireProcessTree: true); }
    }

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task LaunchRealWpfDispatcherHitBreakpointInspectLocalAndStep(string framework)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-DebugTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "Program.cs");
        await File.WriteAllTextAsync(Path.Combine(directory, "Fixture.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{framework}</TargetFramework><OutputType>Exe</OutputType><UseWPF>true</UseWPF><DebugType>portable</DebugType><Optimize>false</Optimize></PropertyGroup></Project>");
        await File.WriteAllTextAsync(sourcePath, """
using System;
using System.Windows.Threading;
internal class Program
{
    [STAThread]
    static void Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(new Action(Work));
        Dispatcher.Run();
    }
    static void Work()
    {
        int answer = 42;
        Console.WriteLine(answer);
        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
    }
}
""");
        using var build = Process.Start(new ProcessStartInfo("dotnet", "build --nologo --verbosity quiet") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        var buildOutput = build.StandardOutput.ReadToEndAsync(); var buildError = build.StandardError.ReadToEndAsync();
        await build.WaitForExitAsync();
        Assert.True(build.ExitCode == 0, await buildOutput + await buildError);
        await using var session = new DebugSession();
        var events = Channel.CreateUnbounded<DebugEvent>();
        session.EventReceived += value => events.Writer.TryWrite(value);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var program = Path.Combine(directory, "bin", "Debug", framework, "Fixture.dll");
        using var testHost = Process.GetCurrentProcess();
        testHost.Refresh();
        var testHostWorkingSetBeforeLaunch = testHost.WorkingSet64;
        await session.LaunchAsync(new DebugLaunchConfiguration(program, directory), [new SourceBreakpoint(sourcePath, 15)], cancellationToken: deadline.Token);
        var stop = await NextEventAsync(events.Reader, "stopped", deadline.Token);
        var thread = stop.Body.GetProperty("threadId").GetInt32();
        var stack = await session.RequestAsync("stackTrace", new { threadId = thread }, deadline.Token);
        var frame = stack.GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
        var value = await session.RequestAsync("evaluate", new { expression = "answer", frameId = frame, context = "watch" }, deadline.Token);
        Assert.Equal("42", value.GetProperty("result").GetString());
        if (framework == "net10.0-windows")
            await RecordDebuggerMeasurementAsync(session, testHost, testHostWorkingSetBeforeLaunch);
        await session.StepAsync("next", thread, deadline.Token);
        await NextEventAsync(events.Reader, "stopped", deadline.Token);
        await session.StopAsync(cancellationToken: deadline.Token);
        Assert.False(session.IsActive);
    }

    private async Task RecordDebuggerMeasurementAsync(DebugSession session, Process testHost, long beforeLaunch)
    {
        // Informational sampling only: test-host growth includes unrelated runner activity,
        // while the adapter's working set belongs to this exact active debugging session.
        try
        {
            if (session.AdapterProcessId is not { } adapterId) return;
            using var adapter = Process.GetProcessById(adapterId);
            adapter.Refresh();
            testHost.Refresh();
            var testHostAtBreakpoint = testHost.WorkingSet64;
            var measurement = new
            {
                SampledAtUtc = DateTimeOffset.UtcNow,
                Scenario = "net10.0-windows WPF dispatcher fixture paused at source breakpoint after evaluating a local",
                AdapterProcessId = adapterId,
                AdapterWorkingSetBytes = adapter.WorkingSet64,
                TestHostWorkingSetBeforeLaunchBytes = beforeLaunch,
                TestHostWorkingSetAtBreakpointBytes = testHostAtBreakpoint,
                TestHostWorkingSetDeltaBytes = testHostAtBreakpoint - beforeLaunch,
                Limitations = "Single informational sample; adapter working set excludes debuggee and IDE UI. Test-host delta includes test runner, JIT, GC, and other concurrent test activity and is not an isolated IDE debugger feature cost."
            };
            var json = JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(json);
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "WpfStudio.sln"))) root = root.Parent;
            if (root is null) return;
            var artifactDirectory = Path.Combine(root.FullName, "artifacts", "performance");
            Directory.CreateDirectory(artifactDirectory);
            await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "debugger-feature.json"), json);
        }
        catch (Exception exception)
        {
            output.WriteLine("Optional debugger performance sample unavailable: " + exception.Message);
        }
    }

    private static async Task<DebugEvent> NextEventAsync(ChannelReader<DebugEvent> reader, string name, CancellationToken cancellationToken)
    {
        await foreach (var value in reader.ReadAllAsync(cancellationToken)) if (value.Name == name) return value;
        throw new InvalidOperationException("Debugger connection ended without " + name);
    }
}
