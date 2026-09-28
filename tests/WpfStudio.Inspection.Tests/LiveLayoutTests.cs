using System.IO.Pipes;
using System.Text.Json;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveLayoutTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task LiveLayoutPolygonsMatchActualAdornerGeometryAcrossDispatchersAndPopup(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        var hello = await session.WaitForConnectionAsync(timeout.Token);
        Assert.Contains("layout", hello.Capabilities);
        Assert.Contains("layout-overlay", hello.Capabilities);
        await app.SendAsync("layout-configure", timeout.Token);
        await Task.Delay(75, timeout.Token); // Allow the application's own layout/render pass.

        foreach (var (command, name) in new[] { ("main", "PrimaryPickButton"), ("child", "ChildPickButton"), ("popup", "PopupPickButton") })
        {
            var tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
            var node = Assert.Single(tree.Nodes, node => node.Name == name);
            var initialInspection = await session.InspectAsync(new(tree.Revision, node.Id), timeout.Token);
            Assert.Contains(initialInspection.Layout!.Overlays, overlay => overlay.Kind == "margin");
            await app.SendAsync("layout-rotate-" + command, timeout.Token);
            await Task.Delay(75, timeout.Token);
            tree = await session.SnapshotAsync(cancellationToken: timeout.Token);
            node = Assert.Single(tree.Nodes, node => node.Name == name);
            var inspection = await session.InspectAsync(new(tree.Revision, node.Id), timeout.Token);
            var layout = Assert.IsType<LayoutSnapshot>(inspection.Layout);
            Assert.True(layout.Available, layout.Status);
            Assert.Contains(layout.Overlays, overlay => overlay.Kind == "slot");
            Assert.DoesNotContain(layout.Overlays, overlay => overlay.Kind == "margin");
            Assert.Contains(layout.Facts, fact => fact.Name == "Margin" && fact.Value == "10, 12, 14, 16");
            Assert.Contains(layout.Overlays, overlay => overlay.Kind == "clip-bounds");
            var render = Assert.Single(layout.Overlays, overlay => overlay.Kind == "render");
            Assert.True(Math.Abs(render.Points[0].Y - render.Points[1].Y) > 1);
            Assert.Equal(node.Bounds!.X, render.Points.Min(point => point.X), 4);
            Assert.Equal(node.Bounds.Y, render.Points.Min(point => point.Y), 4);

            var result = await session.HighlightAsync(new(tree.Revision, node.Id, ShowLayout: true), timeout.Token);
            Assert.True(result.Applied, result.Status);
            var drawn = await WaitForAdornerAsync(app, name, layout.Overlays.Count, timeout.Token);
            Assert.False(drawn.IsHitTestVisible);
            Assert.NotEqual(name, drawn.AdornedName); // Root-side surface can draw slots outside the target.
            Assert.Equal(layout.Overlays.Count, drawn.Polygons.Count);
            for (int index = 0; index < layout.Overlays.Count; index++)
            {
                var expected = layout.Overlays[index];
                var actual = drawn.Polygons[index];
                Assert.Equal(Color(expected.Kind), actual.Color);
                Assert.True(actual.Points.Count >= expected.Points.Count);
                for (int point = 0; point < expected.Points.Count; point++)
                {
                    Assert.InRange(Math.Abs(expected.Points[point].X - actual.Points[point].X), 0, .01);
                    Assert.InRange(Math.Abs(expected.Points[point].Y - actual.Points[point].Y), 0, .01);
                }
            }

            // Default request switches back to the original single blue outline.
            Assert.True((await session.HighlightAsync(new(tree.Revision, node.Id), timeout.Token)).Applied);
            var normal = await WaitForAdornerAsync(app, name, 1, timeout.Token);
            Assert.Equal(name, normal.AdornedName);
            Assert.Equal("#FF0078D7", Assert.Single(normal.Polygons).Color);
            Assert.False(normal.IsHitTestVisible);
            await session.HighlightAsync(new(tree.Revision, null), timeout.Token);
            await WaitForNoAdornersAsync(app, timeout.Token);
        }

        var beforeRemoval = await session.SnapshotAsync(cancellationToken: timeout.Token);
        var removed = Assert.Single(beforeRemoval.Nodes, node => node.Name == "PrimaryPickButton");
        Assert.True((await session.HighlightAsync(new(beforeRemoval.Revision, removed.Id, true), timeout.Token)).Applied);
        await WaitForAdornerAsync(app, removed.Name!, 1, timeout.Token);
        await app.SendAsync("layout-remove-main", timeout.Token);
        var unavailable = await session.InspectAsync(new(beforeRemoval.Revision, removed.Id), timeout.Token);
        Assert.False(unavailable.Available);
        Assert.Null(unavailable.Layout);
        Assert.False((await session.HighlightAsync(new(beforeRemoval.Revision, removed.Id, true), timeout.Token)).Applied);
        var refreshed = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.DoesNotContain(refreshed.Nodes, node => node.Id == removed.Id || node.Type.Contains("InspectionAdorner", StringComparison.Ordinal));
        await WaitForNoAdornersAsync(app, timeout.Token);

        var child = Assert.Single(refreshed.Nodes, node => node.Name == "ChildPickButton");
        Assert.True((await session.HighlightAsync(new(refreshed.Revision, child.Id, true), timeout.Token)).Applied);
        await WaitForAdornerAsync(app, child.Name!, 1, timeout.Token);
        await session.SetPickingAsync(new(true), timeout.Token);
        await WaitForNoAdornersAsync(app, timeout.Token);
        await app.SendAsync("input-child-escape", timeout.Token);
        Assert.False((await session.SnapshotAsync(cancellationToken: timeout.Token)).Pick!.IsActive);
        refreshed = await session.SnapshotAsync(cancellationToken: timeout.Token);
        Assert.True((await session.HighlightAsync(new(refreshed.Revision, child.Id, true), timeout.Token)).Applied);
        await WaitForAdornerAsync(app, child.Name!, 1, timeout.Token);
        await session.DisconnectAsync(timeout.Token);
        await WaitForNoAdornersAsync(app, timeout.Token);
        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task AbruptEofRemovesLayoutAdornerAndDoesNotStopApplication()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var configuration = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        string pipeName = "WpfStudio.Inspection.LayoutEof." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string>(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), configuration))
        { [InspectionProtocol.PipeVariable] = pipeName };
        app.Start(environment);
        await pipe.WaitForConnectionAsync(timeout.Token);
        var hello = (await InspectionWire.ReadAsync(pipe, timeout.Token))!.GetPayload<InspectionHello>();
        Assert.Equal(app.ProcessId, hello.ProcessId);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0,
            new InspectionWelcome(InspectionProtocol.Version, "layout EOF")), timeout.Token);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        await app.SendAsync("layout-configure", timeout.Token);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", 1, new InspectionTreeRequest()), timeout.Token);
        var tree = (await InspectionWire.ReadAsync(pipe, timeout.Token))!.GetPayload<InspectionTree>();
        var node = Assert.Single(tree.Nodes, node => node.Name == "PopupPickButton");
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("highlight", 2, new InspectionHighlightRequest(tree.Revision, node.Id, true)), timeout.Token);
        Assert.True((await InspectionWire.ReadAsync(pipe, timeout.Token))!.GetPayload<InspectionHighlightResult>().Applied);
        await WaitForAdornerAsync(app, node.Name!, 1, timeout.Token);
        pipe.Dispose();
        await WaitForNoAdornersAsync(app, timeout.Token);
        await app.SendAsync("input-popup-down", timeout.Token);
        await app.SendAsync("input-popup-up", timeout.Token);
        Assert.False(app.HasExited);
    }

    private static string Color(string kind) => kind switch
    {
        "slot" => "#FF38BDF8", "render" => "#FF4ADE80", "margin" => "#FFFB923C", "clip-bounds" => "#FFC084FC",
        _ => throw new InvalidOperationException("Unexpected overlay kind " + kind)
    };

    private static async Task<TargetState[]> StateAsync(FixtureApplication app, CancellationToken token)
    {
        await app.SendAsync("layout-state", token);
        return JsonSerializer.Deserialize<TargetState[]>(await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "layout-state.json"), token))!;
    }

    private static async Task<AdornmentState> WaitForAdornerAsync(FixtureApplication app, string name, int minimumPolygons, CancellationToken token)
    {
        while (true)
        {
            var target = Assert.Single(await StateAsync(app, token), target => target.Name == name);
            if (target.Adorners.Count == 1 && target.Adorners[0].Polygons.Count >= minimumPolygons) return target.Adorners[0];
            await Task.Delay(25, token);
        }
    }

    private static async Task WaitForNoAdornersAsync(FixtureApplication app, CancellationToken token)
    {
        while ((await StateAsync(app, token)).Any(target => target.Adorners.Count != 0)) await Task.Delay(25, token);
    }

    private sealed record PointState(double X, double Y);
    private sealed record PolygonState(string Color, IReadOnlyList<PointState> Points);
    private sealed record AdornmentState(string? AdornedName, bool IsHitTestVisible, IReadOnlyList<PolygonState> Polygons);
    private sealed record TargetState(string Name, IReadOnlyList<AdornmentState> Adorners);
}
