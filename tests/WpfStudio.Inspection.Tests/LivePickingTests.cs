using System.IO.Pipes;
using System.Text.Json;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class LivePickingTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task PickingAndHighlightingUseActualTargetsAndRestoreInputAcrossDispatchersAndPopup(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        await session.WaitForConnectionAsync(timeout.Token);
        var initial = await session.SnapshotAsync(cancellationToken: timeout.Token);
        var original = await InputStateAsync(app, timeout.Token);

        await session.SnapshotAsync(cancellationToken: timeout.Token);
        var staleHighlight = await session.HighlightAsync(new(initial.Revision, Node(initial, "PrimaryPickButton").Id), timeout.Token);
        Assert.False(staleHighlight.Applied);
        AssertAllAdorners(original, await InputStateAsync(app, timeout.Token));

        foreach (var (commandTarget, elementName) in Targets)
        {
            var tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
            var node = Node(tree, elementName);
            var before = await InputStateAsync(app, timeout.Token);
            var highlight = await session.HighlightAsync(new(tree.Revision, node.Id), timeout.Token);
            Assert.True(highlight.Applied, highlight.Status);
            var highlighted = await InputStateAsync(app, timeout.Token);
            Assert.Equal(before[elementName].Adorners + 1, highlighted[elementName].Adorners);
            Assert.True((await session.HighlightAsync(new(tree.Revision, null), timeout.Token)).Applied);
            AssertAllAdorners(original, await InputStateAsync(app, timeout.Token));

            var armed = await session.SetPickingAsync(new(true), timeout.Token);
            Assert.True(armed.IsActive, armed.Status);
            await app.SendAsync("input-" + commandTarget + "-down", timeout.Token);
            await app.SendAsync("input-" + commandTarget + "-up", timeout.Token);
            var pickedTree = await session.SnapshotAsync(cancellationToken: timeout.Token);
            Assert.NotNull(pickedTree.Pick);
            Assert.False(pickedTree.Pick.IsActive);
            Assert.True(pickedTree.Pick.Sequence > armed.Sequence);
            Assert.Equal(node.Id, pickedTree.Pick.NodeId);
            Assert.Contains(pickedTree.Nodes, value => value.Id == pickedTree.Pick.NodeId);
            Assert.DoesNotContain(pickedTree.Nodes, value => value.Type.Contains("InspectionAdorner", StringComparison.Ordinal));
            var picked = await InputStateAsync(app, timeout.Token);
            Assert.Equal(before[elementName].PointerDowns, picked[elementName].PointerDowns);
            Assert.Equal(before[elementName].PointerUps, picked[elementName].PointerUps);
            Assert.Equal(before[elementName].Clicks, picked[elementName].Clicks);

            await session.SetPickingAsync(new(false), timeout.Token);
            await app.SendAsync("input-" + commandTarget + "-down", timeout.Token);
            await app.SendAsync("input-" + commandTarget + "-up", timeout.Token);
            var normal = await InputStateAsync(app, timeout.Token);
            Assert.Equal(before[elementName].PointerDowns + 1, normal[elementName].PointerDowns);
            Assert.Equal(before[elementName].PointerUps + 1, normal[elementName].PointerUps);
            AssertAllAdorners(original, normal);
        }

        var beforeEscape = await InputStateAsync(app, timeout.Token);
        await session.SetPickingAsync(new(true), timeout.Token);
        await app.SendAsync("input-main-move", timeout.Token);
        Assert.Equal(original["PrimaryPickButton"].Adorners + 1, (await InputStateAsync(app, timeout.Token))["PrimaryPickButton"].Adorners);
        await app.SendAsync("input-child-escape", timeout.Token);
        var cancelled = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.False(cancelled.Pick!.IsActive);
        Assert.Null(cancelled.Pick.NodeId);
        var afterEscape = await WaitForNoAdornersAsync(app, original, timeout.Token);
        Assert.Equal(beforeEscape["ChildPickButton"].Keys, afterEscape["ChildPickButton"].Keys);
        await app.SendAsync("input-child-down", timeout.Token);
        await app.SendAsync("input-child-up", timeout.Token);
        Assert.Equal(beforeEscape["ChildPickButton"].PointerDowns + 1, (await InputStateAsync(app, timeout.Token))["ChildPickButton"].PointerDowns);

        await session.SetPickingAsync(new(true), timeout.Token);
        await app.SendAsync("input-popup-move", timeout.Token);
        Assert.Equal(original["PopupPickButton"].Adorners + 1, (await InputStateAsync(app, timeout.Token))["PopupPickButton"].Adorners);
        var beforeDisconnect = await InputStateAsync(app, timeout.Token);
        await session.DisconnectAsync(timeout.Token);
        await WaitForNoAdornersAsync(app, original, timeout.Token);
        foreach (var (commandTarget, elementName) in Targets)
        {
            await app.SendAsync("input-" + commandTarget + "-down", timeout.Token);
            await app.SendAsync("input-" + commandTarget + "-up", timeout.Token);
            var afterDisconnect = await InputStateAsync(app, timeout.Token);
            Assert.Equal(beforeDisconnect[elementName].PointerDowns + 1, afterDisconnect[elementName].PointerDowns);
            Assert.Equal(beforeDisconnect[elementName].PointerUps + 1, afterDisconnect[elementName].PointerUps);
        }
        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task InterruptedPickPointerSequenceDoesNotSwallowTheNextApplicationMouseUp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        await session.WaitForConnectionAsync(timeout.Token);
        await session.SnapshotAsync(cancellationToken: timeout.Token);
        var before = await InputStateAsync(app, timeout.Token);
        await session.SetPickingAsync(new(true), timeout.Token);
        await app.SendAsync("input-main-down", timeout.Token);
        Assert.False((await session.SnapshotAsync(cancellationToken: timeout.Token)).Pick!.IsActive);
        // The first sequence is deliberately interrupted before its mouse-up.
        await app.SendAsync("input-main-down", timeout.Token);
        await app.SendAsync("input-main-up", timeout.Token);
        var after = await InputStateAsync(app, timeout.Token);
        Assert.Equal(before["PrimaryPickButton"].PointerDowns + 1, after["PrimaryPickButton"].PointerDowns);
        Assert.Equal(before["PrimaryPickButton"].PointerUps + 1, after["PrimaryPickButton"].PointerUps);
    }

    [Fact]
    public async Task AbruptInspectorEofRemovesLivePickerHandlersAndAdornersFromEveryDispatcher()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var configuration = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        var pipeName = "WpfStudio.Inspection.PickerEof." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string>(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), configuration))
        {
            [InspectionProtocol.PipeVariable] = pipeName
        };
        app.Start(environment);
        await pipe.WaitForConnectionAsync(timeout.Token);
        var hello = (await InspectionWire.ReadAsync(pipe, timeout.Token))!.GetPayload<InspectionHello>();
        Assert.Equal(app.ProcessId, hello.ProcessId);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0,
            new InspectionWelcome(InspectionProtocol.Version, "abrupt picker EOF")), timeout.Token);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        var before = await InputStateAsync(app, timeout.Token);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("pick", 1, new InspectionPickRequest(true)), timeout.Token);
        var response = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        Assert.Null(response.Error);
        Assert.True(response.GetPayload<InspectionPickState>().IsActive);
        await app.SendAsync("input-popup-move", timeout.Token);
        Assert.Equal(before["PopupPickButton"].Adorners + 1, (await InputStateAsync(app, timeout.Token))["PopupPickButton"].Adorners);
        pipe.Dispose();
        await WaitForNoAdornersAsync(app, before, timeout.Token);
        foreach (var (commandTarget, elementName) in Targets)
        {
            await app.SendAsync("input-" + commandTarget + "-down", timeout.Token);
            await app.SendAsync("input-" + commandTarget + "-up", timeout.Token);
            var after = await InputStateAsync(app, timeout.Token);
            Assert.Equal(before[elementName].PointerDowns + 1, after[elementName].PointerDowns);
            Assert.Equal(before[elementName].PointerUps + 1, after[elementName].PointerUps);
        }
        Assert.False(app.HasExited);
    }

    private static readonly (string Command, string Name)[] Targets =
        [("main", "PrimaryPickButton"), ("child", "ChildPickButton"), ("popup", "PopupPickButton")];

    private static InspectionNode Node(InspectionTree tree, string name) => Assert.Single(tree.Nodes, node => node.Name == name);

    private static async Task<Dictionary<string, InputState>> InputStateAsync(FixtureApplication app, CancellationToken token)
    {
        await app.SendAsync("input-state", token);
        var json = await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "input-state.json"), token);
        return JsonSerializer.Deserialize<InputState[]>(json)!.ToDictionary(value => value.Name, StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, InputState>> WaitForNoAdornersAsync(FixtureApplication app,
        Dictionary<string, InputState> original, CancellationToken token)
    {
        while (true)
        {
            var state = await InputStateAsync(app, token);
            if (state.All(pair => pair.Value.Adorners == original[pair.Key].Adorners)) return state;
            await Task.Delay(20, token);
        }
    }

    private static void AssertAllAdorners(Dictionary<string, InputState> expected, Dictionary<string, InputState> actual)
    {
        foreach (var pair in expected) Assert.Equal(pair.Value.Adorners, actual[pair.Key].Adorners);
    }

    private sealed record InputState(string Name, int PointerDowns, int PointerUps, int Clicks, int Keys, int Adorners);
}
