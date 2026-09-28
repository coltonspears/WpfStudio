using System.IO.Pipes;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionAppearanceTransportTests
{
    private static AppearanceRequest Selection => new(7, "node", "Foreground", "System.Windows.Controls.Control", "PresentationFramework", "observed-property");

    [Fact]
    public async Task AppearanceUsesItsOwnReadRequestAndPreservesObservationIdentity()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, true, timeout.Token);
        var selected = Selection;
        var pending = session.GetAppearanceAsync(selected, timeout.Token);
        var message = Assert.IsType<InspectionMessage>(await InspectionWire.ReadAsync(pipe, timeout.Token));
        Assert.Equal("appearance", message.Kind);
        Assert.Equal(selected, message.GetPayload<AppearanceRequest>());
        var snapshot = new AppearanceSnapshot(true, [new("Value source", "Style")], [], [], [], ["Observed declarations"], true);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("appearance", message.Id, new AppearanceResponse(selected, snapshot)), timeout.Token);
        var response = await pending;
        Assert.Equal(selected, response.Request);
        Assert.Equal("Style", Assert.Single(response.Snapshot.Facts).Value);
        Assert.True(response.Snapshot.Truncated);
        Assert.True(session.IsConnected);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("node")]
    [InlineData("property")]
    [InlineData("owner")]
    [InlineData("assembly")]
    [InlineData("identity")]
    [InlineData("missing snapshot")]
    public async Task UnrelatedOrMalformedAppearanceCannotBePresentedAsSelectedProperty(string mismatch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, true, timeout.Token);
        var selected = Selection;
        var pending = session.GetAppearanceAsync(selected, timeout.Token);
        var message = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        var echoed = mismatch switch
        {
            "revision" => selected with { Revision = selected.Revision + 1 },
            "node" => selected with { NodeId = "other-node" },
            "property" => selected with { Property = "Background" },
            "owner" => selected with { OwnerType = "Other.Owner" },
            "assembly" => selected with { OwnerAssembly = "OtherAssembly" },
            "identity" => selected with { PropertyId = "other-property" },
            _ => selected
        };
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("appearance", message.Id,
            new AppearanceResponse(echoed, mismatch == "missing snapshot" ? null! : AppearanceSnapshot.Unavailable("No declaration"))), timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        Assert.True(session.IsConnected); // Rejected read data does not mutate or terminate the target.
    }

    [Fact]
    public async Task OlderAgentDoesNotReceiveUnsupportedAppearanceRequest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, false, timeout.Token);
        var response = await session.GetAppearanceAsync(Selection, timeout.Token);
        Assert.False(response.Snapshot.Available);
        Assert.Equal(Selection, response.Request);
        Assert.Contains("does not provide", response.Snapshot.Status);
        await ProveNextRequestIsTreeAsync(session, pipe, timeout.Token);
    }

    [Fact]
    public async Task CancelledAppearanceDoesNotReachAgent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, true, timeout.Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetAppearanceAsync(Selection, cancelled.Token));
        await ProveNextRequestIsTreeAsync(session, pipe, timeout.Token);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(InspectionSession session, bool appearance, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", session.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(token);
            session.ExpectProcess(Environment.ProcessId);
            await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0,
                new InspectionHello(InspectionProtocol.Version, session.Token, Environment.ProcessId,
                    Environment.Version.ToString(), "appearance-transport-test", appearance ? ["tree", "appearance"] : ["tree"])), token);
            Assert.Equal("hello", (await InspectionWire.ReadAsync(pipe, token))!.Kind);
            await session.WaitForConnectionAsync(token);
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task ProveNextRequestIsTreeAsync(InspectionSession session, NamedPipeClientStream pipe, CancellationToken token)
    {
        var pending = session.SnapshotAsync(cancellationToken: token);
        var message = (await InspectionWire.ReadAsync(pipe, token))!;
        Assert.Equal("tree", message.Kind);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", message.Id, new InspectionTree(8, [], [])), token);
        await pending;
    }
}
