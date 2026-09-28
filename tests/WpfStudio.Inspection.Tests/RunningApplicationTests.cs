using System.IO.Pipes;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class RunningApplicationTests
{
    [Theory]
    [InlineData("net8.0-windows", 8, false)]
    [InlineData("net8.0-windows", 8, true)]
    [InlineData("net9.0-windows", 9, false)]
    [InlineData("net9.0-windows", 9, true)]
    [InlineData("net10.0-windows", 10, false)]
    [InlineData("net10.0-windows", 10, true)]
    public async Task InspectRealApplicationCapturesStartupBindingsAcrossWindowsAndDispatchers(
        string framework, int runtimeMajor, bool appHost)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework, appHost);
        var existingHook = Path.Combine(AppContext.BaseDirectory, "ExistingHook", "WpfStudio.ExistingStartupHook.dll");
        var environment = InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>
        {
            ["DOTNET_STARTUP_HOOKS"] = existingHook,
            ["WPFSTUDIO_TEST_PROFILE_VALUE"] = "preserved launch profile"
        }, session);
        app.Start(environment);
        session.ExpectProcess(app.ProcessId);
        var ready = await app.WaitForReadyAsync(timeout.Token);
        InspectionAgentUnderTest.AssertLoadedAgent(ready);
        var hello = await session.WaitForConnectionAsync(timeout.Token);
        Assert.Equal(app.ProcessId, hello.ProcessId);
        Assert.Equal(runtimeMajor, ready.RuntimeMajor);
        Assert.StartsWith(runtimeMajor + ".", hello.RuntimeVersion);
        Assert.Equal("yes", ready.ExistingHookRan);
        Assert.Equal("1", ready.ExistingHookCount);
        Assert.Equal("preserved launch profile", ready.ProfileValue);
        Assert.Contains(existingHook, ready.RemainingHooks);
        Assert.DoesNotContain("WpfStudio.Inspection.StartupHook", ready.RemainingHooks);
        Assert.Null(ready.InspectionPipe);
        Assert.Null(ready.InspectionToken);
        Assert.Null(ready.InspectionOwner);

        var tree = await WaitForTreeAsync(session, tree => tree.Nodes.Any(node => node.Name == "PopupText") &&
            tree.Nodes.Any(node => node.Name == "ChildDispatcherText"), timeout.Token);
        Assert.False(tree.Truncated);
        Assert.Contains(tree.Nodes, node => node.Name == "SecondaryWindow");
        var good = Assert.Single(tree.Nodes, node => node.Name == "GoodText");
        var bad = Assert.Single(tree.Nodes, node => node.Name == "BadText");
        var child = Assert.Single(tree.Nodes, node => node.Name == "ChildDispatcherText");
        Assert.NotEqual(good.DispatcherId, child.DispatcherId);
        Assert.Equal(ready.MainThreadId, good.DispatcherId);
        Assert.Equal(ready.ChildThreadId, child.DispatcherId);
        foreach (var generated in tree.Nodes.Where(node => node.Name is "PopupText" or "SecondaryWindow" or "ChildDispatcherText"))
            Assert.Null(generated.Source);
        Assert.Contains(tree.Traces, trace => trace.Message.Contains("Misspelled", StringComparison.Ordinal));

        var goodElement = await session.InspectAsync(new(tree.Revision, good.Id), timeout.Token);
        Assert.True(goodElement.Available, goodElement.Status);
        Assert.EndsWith("RuntimeViewModel", goodElement.DataContextType);
        var text = Property(goodElement, "Text");
        Assert.Equal("Runtime data context", text.Value);
        Assert.Equal("DisplayName", text.Binding?.Path);
        Assert.Equal("Active", text.Binding?.Status);
        Assert.True(text.IsExpression);
        var badElement = await session.InspectAsync(new(tree.Revision, bad.Id), timeout.Token);
        Assert.Equal("Misspelled", Property(badElement, "Text").Binding?.Path);
        Assert.NotEqual("Active", Property(badElement, "Text").Binding?.Status);
        Assert.Equal("MissingProperty", Property(badElement, "Text").Binding?.Category);
        Assert.EndsWith("RuntimeViewModel", Property(badElement, "Text").Binding?.SourceType);
        var multi = Assert.Single(tree.Nodes, node => node.Name == "MultiBadText");
        var multiElement = await session.InspectAsync(new(tree.Revision, multi.Id), timeout.Token);
        Assert.Equal("ChildBindingError", Property(multiElement, "Text").Binding?.Category);
        var childElement = await session.InspectAsync(new(tree.Revision, child.Id), timeout.Token);
        Assert.True(childElement.Available, childElement.Status);
        Assert.Equal("Inspection fixture child dispatcher", Property(childElement, "Text").Value);
        var button = Assert.Single(tree.Nodes, node => node.Name == "StyleButton");
        var buttonElement = await session.InspectAsync(new(tree.Revision, button.Id), timeout.Token);
        Assert.Equal("Style", Property(buttonElement, "Tag").ValueSource);

        Assert.Equal("change", await app.SendAsync("change", timeout.Token));
        var changed = await session.InspectAsync(new(tree.Revision, good.Id), timeout.Token);
        Assert.Equal("Updated while running", Property(changed, "Text").Value);
        var popup = Assert.Single(tree.Nodes, node => node.Name == "PopupText");
        Assert.Equal("close-popup", await app.SendAsync("close-popup", timeout.Token));
        Assert.False((await session.InspectAsync(new(tree.Revision, popup.Id), timeout.Token)).Available);
        var newerTree = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.True(newerTree.Revision > tree.Revision);
        Assert.DoesNotContain(newerTree.Nodes, node => node.Name == "PopupText");
        Assert.False((await session.InspectAsync(new(tree.Revision, good.Id), timeout.Token)).Available);

        await session.DisconnectAsync(timeout.Token);
        Assert.False(session.IsConnected);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
        Assert.False(app.HasExited);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("missing-token")]
    [InlineData("missing-pipe")]
    public async Task UnavailableOrRejectedInspectorNeverPreventsTheApplicationFromStarting(string failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = new InspectionSession(connectionTimeout: TimeSpan.FromSeconds(2));
        await using var app = new FixtureApplication("net10.0-windows");
        var environment = new Dictionary<string, string>(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        if (failure == "wrong-token") environment[InspectionProtocol.TokenVariable] = "incorrect nonce";
        if (failure == "missing-token") environment[InspectionProtocol.TokenVariable] = "";
        if (failure == "missing-pipe") environment[InspectionProtocol.PipeVariable] = "WpfStudio.Missing." + Guid.NewGuid().ToString("N");
        app.Start(environment);
        session.ExpectProcess(app.ProcessId);
        await app.WaitForReadyAsync(timeout.Token);
        if (failure == "wrong-token")
            await Assert.ThrowsAsync<InvalidDataException>(() => session.WaitForConnectionAsync(timeout.Token));
        else
            await Assert.ThrowsAsync<TimeoutException>(() => session.WaitForConnectionAsync(timeout.Token));
        Assert.False(session.IsConnected);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
        await session.DisposeAsync();
        Assert.Equal("change", await app.SendAsync("change", timeout.Token));
        Assert.False(app.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidWelcomeOrAbruptInspectorLossLeavesRealApplicationResponsive(bool invalidWelcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var unusedSession = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        var pipeName = "WpfStudio.Inspection.Test." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string>(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), unusedSession))
        {
            [InspectionProtocol.PipeVariable] = pipeName
        };
        app.Start(environment);
        await pipe.WaitForConnectionAsync(timeout.Token);
        var hello = (await InspectionWire.ReadAsync(pipe, timeout.Token))!.GetPayload<InspectionHello>();
        Assert.Equal(app.ProcessId, hello.ProcessId);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0,
            new InspectionWelcome(invalidWelcome ? InspectionProtocol.Version + 1 : InspectionProtocol.Version, "test session")), timeout.Token);
        if (invalidWelcome)
        {
            // EOF proves the agent rejected the welcome and completed transport cleanup.
            Assert.Null(await InspectionWire.ReadAsync(pipe, timeout.Token));
        }
        else
        {
            await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", 1, new InspectionTreeRequest()), timeout.Token);
            Assert.Equal("tree", (await InspectionWire.ReadAsync(pipe, timeout.Token))!.Kind);
            pipe.Dispose(); // Simulate the IDE disappearing without a detach request.
        }
        await app.WaitForReadyAsync(timeout.Token);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
        Assert.Equal("change", await app.SendAsync("change", timeout.Token));
        Assert.False(app.HasExited);
    }

    internal static InspectionProperty Property(InspectionElement element, string name) =>
        Assert.Single(element.Properties, property => property.Name == name);

    internal static async Task<InspectionTree> WaitForTreeAsync(InspectionSession session,
        Func<InspectionTree, bool> predicate, CancellationToken token)
    {
        while (true)
        {
            var tree = await session.SnapshotAsync(cancellationToken: token);
            if (predicate(tree)) return tree;
            await Task.Delay(50, token);
        }
    }
}
