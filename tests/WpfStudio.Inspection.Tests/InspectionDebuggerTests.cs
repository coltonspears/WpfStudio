using System.IO.Pipes;
using System.Reflection;
using System.Threading.Channels;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionDebuggerTests
{
    [Theory]
    [InlineData("inherit")]
    [InlineData("clear")]
    [InlineData("replace")]
    public async Task DapLaunchHonorsEffectiveStartupHooksExactlyOnce(string profileMode)
    {
        const string variable = "DOTNET_STARTUP_HOOKS";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var inspection = new InspectionSession();
            await using var debugger = new DebugSession();
            await using var app = new FixtureApplication("net10.0-windows");
            var existingHook = Path.Combine(AppContext.BaseDirectory, "ExistingHook", "WpfStudio.ExistingStartupHook.dll");
            Environment.SetEnvironmentVariable(variable, profileMode == "replace"
                ? Path.Combine(AppContext.BaseDirectory, "MustNotLoadInheritedHook.dll") : existingHook);
            var profile = new Dictionary<string, string>();
            if (profileMode == "clear") profile[variable] = "";
            if (profileMode == "replace") profile[variable] = existingHook;
            var environment = InspectionAgentUnderTest.CreateEnvironment(profile, inspection);
            await debugger.LaunchAsync(new DebugLaunchConfiguration(app.Program, Path.GetDirectoryName(app.Program)!,
                [app.DirectoryPath], environment), [], cancellationToken: timeout.Token);
            var ready = await app.WaitForReadyAsync(timeout.Token);
            InspectionAgentUnderTest.AssertLoadedAgent(ready);
            inspection.ExpectProcess(ready.ProcessId);
            await inspection.WaitForConnectionAsync(timeout.Token);
            Assert.Equal(profileMode == "clear" ? null : "1", ready.ExistingHookCount);
            Assert.Equal(profileMode == "clear" ? null : "yes", ready.ExistingHookRan);
            Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
            await inspection.DisconnectAsync(timeout.Token);
            await debugger.StopAsync(cancellationToken: timeout.Token);
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public async Task DebuggerPauseLongerThanHandshakeBudgetDoesNotDetachAgentOnResume()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var configuration = new InspectionSession();
        await using var debugger = new DebugSession();
        await using var app = new FixtureApplication("net10.0-windows");
        var pipeName = "WpfStudio.Inspection.PausedHandshake." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string>(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), configuration))
        {
            [InspectionProtocol.PipeVariable] = pipeName
        };
        var sourcePath = typeof(InspectionDebuggerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "InspectionFixtureSourcePath").Value!;
        var source = await File.ReadAllLinesAsync(sourcePath, timeout.Token);
        var breakpointLine = Array.FindIndex(source, line => line.Contains("// INSPECTION_BREAKPOINT", StringComparison.Ordinal)) + 1;
        var events = Channel.CreateUnbounded<DebugEvent>();
        debugger.EventReceived += value => events.Writer.TryWrite(value);
        await debugger.LaunchAsync(new DebugLaunchConfiguration(app.Program, Path.GetDirectoryName(app.Program)!,
            [app.DirectoryPath], environment), [new SourceBreakpoint(sourcePath, breakpointLine)], cancellationToken: timeout.Token);
        await pipe.WaitForConnectionAsync(timeout.Token);
        var hello = (await InspectionWire.ReadAsync(pipe, timeout.Token))!.GetPayload<InspectionHello>();
        var ready = await app.WaitForReadyAsync(timeout.Token);
        InspectionAgentUnderTest.AssertLoadedAgent(ready);
        Assert.Equal(ready.ProcessId, hello.ProcessId);
        var requestId = await app.SubmitAsync("break", timeout.Token);
        var stopped = await NextEventAsync(events.Reader, "stopped", timeout.Token);
        Assert.Equal("breakpoint", stopped.Body.GetProperty("reason").GetString());
        // The server deliberately withholds its welcome while all managed threads are
        // stopped for longer than the agent's ten-second startup handshake budget.
        await Task.Delay(TimeSpan.FromSeconds(11.5), timeout.Token);
        await debugger.ContinueAsync(stopped.Body.GetProperty("threadId").GetInt32(), timeout.Token);
        await app.WaitForAcknowledgementAsync(requestId, timeout.Token);
        // Let overdue timers run: an ordinary CancelAfter deadline would now close
        // the pipe before this welcome, despite the debuggee having been suspended.
        await Task.Delay(350, timeout.Token);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0,
            new InspectionWelcome(InspectionProtocol.Version, "resumed handshake")), timeout.Token);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", 1, new InspectionTreeRequest()), timeout.Token);
        var response = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        Assert.Equal("tree", response.Kind);
        Assert.Contains(response.GetPayload<InspectionTree>().Nodes, node => node.Name == "GoodText");
        pipe.Dispose();
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
        await debugger.StopAsync(cancellationToken: timeout.Token);
    }

    [Fact]
    public async Task DapLaunchCanInspectThenBreakAndResumeWithoutInjectingTheDebuggerAdapter()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var inspection = new InspectionSession();
        await using var debugger = new DebugSession();
        await using var app = new FixtureApplication("net10.0-windows");
        var environment = InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), inspection);
        var sourcePath = typeof(InspectionDebuggerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "InspectionFixtureSourcePath").Value!;
        var source = await File.ReadAllLinesAsync(sourcePath, timeout.Token);
        var breakpointLine = Array.FindIndex(source, line => line.Contains("// INSPECTION_BREAKPOINT", StringComparison.Ordinal)) + 1;
        Assert.True(breakpointLine > 0);
        var events = Channel.CreateUnbounded<DebugEvent>();
        debugger.EventReceived += value => events.Writer.TryWrite(value);
        await debugger.LaunchAsync(new DebugLaunchConfiguration(app.Program, Path.GetDirectoryName(app.Program)!,
            [app.DirectoryPath], environment), [new SourceBreakpoint(sourcePath, breakpointLine)], cancellationToken: timeout.Token);
        var ready = await app.WaitForReadyAsync(timeout.Token);
        InspectionAgentUnderTest.AssertLoadedAgent(ready);
        inspection.ExpectProcess(ready.ProcessId);
        var hello = await inspection.WaitForConnectionAsync(timeout.Token);
        Assert.Equal(ready.ProcessId, hello.ProcessId);
        Assert.NotEqual(debugger.AdapterProcessId, hello.ProcessId);
        var tree = await RunningApplicationTests.WaitForTreeAsync(inspection,
            tree => tree.Nodes.Any(node => node.Name == "GoodText"), timeout.Token);
        Assert.Contains(tree.Traces, trace => trace.Message.Contains("Misspelled", StringComparison.Ordinal));

        var requestId = await app.SubmitAsync("break", timeout.Token);
        var stopped = await NextEventAsync(events.Reader, "stopped", timeout.Token);
        Assert.Equal("breakpoint", stopped.Body.GetProperty("reason").GetString());
        var threadId = stopped.Body.GetProperty("threadId").GetInt32();
        inspection.SetDebuggerPaused(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspection.SnapshotAsync(cancellationToken: timeout.Token));
        Assert.True(inspection.IsConnected);
        var stack = await debugger.RequestAsync("stackTrace", new { threadId }, timeout.Token);
        var frameId = stack.GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
        var value = await debugger.RequestAsync("evaluate", new { expression = "answer", frameId, context = "watch" }, timeout.Token);
        Assert.Equal("42", value.GetProperty("result").GetString());
        await debugger.ContinueAsync(threadId, timeout.Token);
        inspection.SetDebuggerPaused(false);
        Assert.Equal("break", await app.WaitForAcknowledgementAsync(requestId, timeout.Token));
        Assert.NotEmpty((await inspection.SnapshotAsync(cancellationToken: timeout.Token)).Nodes);

        await inspection.DisconnectAsync(timeout.Token);
        Assert.True(debugger.IsActive);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
        Assert.False(debugger.CanDetach);
        await Assert.ThrowsAsync<NotSupportedException>(() => debugger.StopAsync(terminate: false, timeout.Token));
        Assert.True(debugger.IsActive);
        Assert.False(app.HasExited);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
        await debugger.StopAsync(terminate: true, timeout.Token);
    }

    private static async Task<DebugEvent> NextEventAsync(ChannelReader<DebugEvent> reader, string name, CancellationToken token)
    {
        await foreach (var value in reader.ReadAllAsync(token)) if (value.Name == name) return value;
        throw new InvalidOperationException("Debugger disconnected before " + name);
    }
}
