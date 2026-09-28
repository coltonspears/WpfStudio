using System.Text.Json;
using WpfStudio.Contracts;

namespace WpfStudio.Preview.Tests;

public sealed class NativePreviewNavigationTests
{
    [Fact]
    public async Task BoundaryCallsArriveInOrderBeforeReturningAndRemoveTheirExactGrant()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await Render(host);
        var attach = bridge.Request(snapshot.Surface!, 1);
        var attached = await host.Rpc.UpdateSurfaceAsync(attach, default);
        Assert.True(attached.Success, attached.Status);
        Assert.Equal(bridge.ChildFor(host.Process.Id).ToInt64(), attached.NativeHandle);
        var before = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        var expected = new[] { "next", "next", "previous", "next", "previous" };
        for (int index = 0; index < expected.Length; index++)
        {
            // Reset the command between equal values so a real DP callback runs
            // each time. These test the native site protocol, not physical keys.
            await Command(host, snapshot, "observe-" + index);
            Assert.True((await Command(host, snapshot, expected[index])).NavigationHandled);
            Assert.Equal(index + 1, bridge.NavigationCalls.Count);
            var call = bridge.NavigationCalls[index];
            Assert.Equal(expected[index] == "next" ? PreviewSurfaceNavigation.Next : PreviewSurfaceNavigation.Previous, call.Direction);
            Assert.True(index == 0 || call.Sequence > bridge.NavigationCalls[index - 1].Sequence);
            Assert.Equal(attached.NativeHandle, call.Child.ToInt64());
            Assert.Equal(nint.Zero, HostNativeTestMethods.GetProp(call.Child, PreviewNativeNavigation.PropertyPrefix + attach.NavigationToken));
        }
        var heartbeat = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        Assert.True(heartbeat.Available, heartbeat.Status);
        Assert.Equal(before.NavigationSequence, heartbeat.NavigationSequence);
        Assert.Equal(0, heartbeat.NavigationPendingSinceTick);
        var detached = await host.Rpc.UpdateSurfaceAsync(attach with { Sequence = 2, Action = PreviewSurfaceAction.Detach }, default);
        Assert.True(detached.Success, detached.Status);
    }

    [Fact]
    public async Task ExplicitRejectionAllowsLocalTraversalButUnknownReplyRevokesTheNativeSession()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await Render(host);
        var attach = bridge.Request(snapshot.Surface!, 1);
        var attached = await host.Rpc.UpdateSurfaceAsync(attach, default);
        Assert.True(attached.Success, attached.Status);
        bridge.ReplyToNavigation(PreviewNativeNavigation.Rejected);
        Assert.False((await Command(host, snapshot, "next")).NavigationHandled);
        Assert.True((await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default)).Available);
        bridge.ReplyToNavigation(97);
        Assert.True((await Command(host, snapshot, "previous")).NavigationHandled);
        var failed = await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default);
        Assert.False(failed.Available);
        Assert.Contains("unknown acknowledgement", failed.Status!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, failed.NavigationPendingSinceTick);
        Assert.Equal(nint.Zero, HostNativeTestMethods.GetProp(new nint(attached.NativeHandle), PreviewNativeNavigation.PropertyPrefix + attach.NavigationToken));
    }

    [Fact]
    public async Task AReattachedSurfaceRequiresItsNewNavigationTokenAndKeepsFocusSequenceMonotonic()
    {
        using var bridge = new HostNativeTestBridge();
        await using var host = await RawNativePreviewHost.StartAsync();
        var snapshot = await Render(host);
        var first = bridge.Request(snapshot.Surface!, 1);
        var attached = await host.Rpc.UpdateSurfaceAsync(first, default);
        Assert.True(attached.Success, attached.Status);
        var firstFocus = Assert.IsType<PreviewSurfaceFocus>(attached.Focus);
        Assert.Equal(first.Sequence, firstFocus.AttachmentSequence);
        Assert.Equal(first.BridgeToken, firstFocus.BridgeToken);
        Assert.True((await host.Rpc.UpdateSurfaceAsync(first with { Sequence = 2, Action = PreviewSurfaceAction.Detach }, default)).Success);
        Assert.Null((await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default)).Focus);
        bridge.RenewNavigationToken();
        var second = bridge.Request(snapshot.Surface!, 3);
        var reattached = await host.Rpc.UpdateSurfaceAsync(second, default);
        Assert.True(reattached.Success, reattached.Status);
        var nextFocus = Assert.IsType<PreviewSurfaceFocus>(reattached.Focus);
        Assert.Equal(second.Sequence, nextFocus.AttachmentSequence);
        Assert.True(nextFocus.Sequence > firstFocus.Sequence);
        var stale = await host.Rpc.UpdateSurfaceAsync(first with { Sequence = 4, Action = PreviewSurfaceAction.Update }, default);
        Assert.False(stale.Success);
        Assert.True((await host.Rpc.HeartbeatSurfaceAsync(new(snapshot.Surface!), default)).Available);
        Assert.True((await Command(host, snapshot, "next")).NavigationHandled);
        Assert.Equal(nint.Zero, HostNativeTestMethods.GetProp(new nint(reattached.NativeHandle), PreviewNativeNavigation.PropertyPrefix + first.NavigationToken));
        var detached = await host.Rpc.UpdateSurfaceAsync(second with { Sequence = 5, Action = PreviewSurfaceAction.Detach }, default);
        Assert.True(detached.Success, detached.Status);
    }

    private static async Task<PreviewSnapshot> Render(RawNativePreviewHost host)
    {
        var snapshot = await host.Rpc.RenderAsync(new("C:/preview/Navigation.xaml",
            "<probe:NativePreviewFixtureControl xmlns:probe='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'/>",
            1, 400, 300, typeof(NativePreviewFixtureControl).Assembly.Location, AppContext.BaseDirectory), default);
        Assert.True(snapshot.Success, snapshot.Status + "\n" + string.Join("\n", snapshot.Diagnostics.Select(item => item.Message)));
        return snapshot;
    }

    private static async Task<NativePreviewObservation> Command(RawNativePreviewHost host, PreviewSnapshot snapshot, string command)
    {
        var result = await host.Rpc.SetPropertyAsync(new(snapshot.Version, snapshot.Nodes[0].Id, "Tag", command), default);
        Assert.True(result.Success, result.Error);
        var observation = Assert.Single(result.Inspection.Properties, property => property.Name == nameof(NativePreviewFixtureControl) + ".Observation");
        return JsonSerializer.Deserialize<NativePreviewObservation>(observation.Value)!;
    }
}
