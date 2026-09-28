using System.IO.Pipes;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionEditingTransportTests
{
    [Fact]
    public async Task AlreadyCancelledMutationIsRejectedWithoutSendingOrDisconnecting()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal("Rejected", (await session.SetPropertyAsync(Edit(), cancelled.Token)).Outcome);
        Assert.True(session.IsConnected);
        await ProveNextRequestIsSnapshotAsync(session, pipe, timeout.Token);
    }

    [Fact]
    public async Task MutationCancelledWhileWaitingForSerialGateNeverReachesTheAgent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        var reading = session.SnapshotAsync(cancellationToken: timeout.Token);
        var readRequest = Assert.IsType<InspectionMessage>(await InspectionWire.ReadAsync(pipe, timeout.Token));
        using var cancel = new CancellationTokenSource();
        var editing = session.SetPropertyAsync(Edit(), cancel.Token);
        cancel.Cancel();
        Assert.Equal("Rejected", (await editing).Outcome);
        Assert.True(session.IsConnected);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", readRequest.Id, new InspectionTree(1, [], [])), timeout.Token);
        await reading;
        await ProveNextRequestIsSnapshotAsync(session, pipe, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationOrDebuggerPauseAfterSendReportsUnknownAndClosesPipe(bool pause)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        using var cancel = new CancellationTokenSource();
        var edit = Edit();
        var editing = session.SetPropertyAsync(edit, cancel.Token);
        var request = Assert.IsType<InspectionMessage>(await InspectionWire.ReadAsync(pipe, timeout.Token));
        Assert.Equal("set-property", request.Kind);
        Assert.Equal(edit.OperationId, request.GetPayload<InspectionPropertyEdit>().OperationId);
        if (pause) session.SetDebuggerPaused(true);
        else cancel.Cancel();
        var result = await editing;
        Assert.Equal("Unknown", result.Outcome);
        Assert.Equal(edit.OperationId, result.OperationId);
        Assert.False(session.IsConnected);
        Assert.Null(await InspectionWire.ReadAsync(pipe, timeout.Token));
    }

    [Fact]
    public async Task MissingMutationResponseTimesOutAsUnknownAndRequestsCleanupThroughEof()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession(requestTimeout: TimeSpan.FromMilliseconds(250));
        using var pipe = await ConnectAsync(session, timeout.Token);
        var editing = session.SetPropertyAsync(Edit(), timeout.Token);
        Assert.Equal("set-property", (await InspectionWire.ReadAsync(pipe, timeout.Token))!.Kind);
        Assert.Equal("Unknown", (await editing).Outcome);
        Assert.False(session.IsConnected);
        Assert.Null(await InspectionWire.ReadAsync(pipe, timeout.Token));
    }

    [Fact]
    public async Task AgentUnknownKeepsConnectionForOutcomeQueryWithoutResendingMutation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        var edit = Edit();
        var editing = session.SetPropertyAsync(edit, timeout.Token);
        var request = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("set-property", request.Id,
            new InspectionPropertyEditResult(edit.OperationId, "Unknown", Error: "Callback is still running.")), timeout.Token);
        Assert.Equal("Unknown", (await editing).Outcome);
        Assert.True(session.IsConnected);
        var checking = session.GetEditStatusAsync(new(edit.OperationId), timeout.Token);
        var statusRequest = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        Assert.Equal("edit-status", statusRequest.Kind);
        Assert.Equal(edit.OperationId, statusRequest.GetPayload<InspectionEditStatusRequest>().OperationId);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("edit-status", statusRequest.Id,
            new InspectionPropertyEditResult(edit.OperationId, "Applied")), timeout.Token);
        Assert.Equal("Applied", (await checking).Outcome);
        Assert.True(session.IsConnected);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("outcome")]
    [InlineData("node")]
    [InlineData("revision")]
    public async Task UnrelatedOrMalformedMutationAcknowledgementCannotClaimSuccess(string mismatch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        var edit = Edit();
        var editing = session.SetPropertyAsync(edit, timeout.Token);
        var request = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        var element = new InspectionElement(mismatch == "revision" ? edit.Revision + 1 : edit.Revision,
            mismatch == "node" ? "other-node" : edit.NodeId, [], null, true);
        var acknowledgement = new InspectionPropertyEditResult(mismatch == "operation" ? "other-operation" : edit.OperationId,
            mismatch == "outcome" ? "ProbablyApplied" : "Applied", element);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("set-property", request.Id, acknowledgement), timeout.Token);
        var result = await editing;
        Assert.Equal("Unknown", result.Outcome);
        Assert.Equal(edit.OperationId, result.OperationId);
        Assert.False(session.IsConnected);
        Assert.Null(await InspectionWire.ReadAsync(pipe, timeout.Token));
    }

    [Fact]
    public async Task ValidationRoundtripDoesNotUseMutationKind()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        var validating = session.ValidatePropertyAsync(Edit(), timeout.Token);
        var request = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        Assert.Equal("validate-property", request.Kind);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("validate-property", request.Id,
            new InspectionPropertyValidation(false, "Invalid number.")), timeout.Token);
        var result = await validating;
        Assert.False(result.Success);
        Assert.Equal("Invalid number.", result.Error);
        Assert.True(session.IsConnected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutcomeQueryRejectsAnotherOperationOrAnUnsupportedOutcome(bool wrongIdentity)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        var operationId = Guid.NewGuid().ToString("N");
        var checking = session.GetEditStatusAsync(new(operationId), timeout.Token);
        var request = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        Assert.Equal("edit-status", request.Kind);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("edit-status", request.Id,
            new InspectionPropertyEditResult(wrongIdentity ? "different-operation" : operationId,
                wrongIdentity ? "Applied" : "MaybeApplied")), timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => checking);
    }

    private static InspectionPropertyEdit Edit() => new(Guid.NewGuid().ToString("N"), 1, "node", "property", "token", "42");

    private static async Task<NamedPipeClientStream> ConnectAsync(InspectionSession session, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", session.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(token);
            session.ExpectProcess(Environment.ProcessId);
            await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0,
                new InspectionHello(InspectionProtocol.Version, session.Token, Environment.ProcessId,
                    Environment.Version.ToString(), "editing-transport-test", ["property-edit", "tree"])), token);
            Assert.Equal("hello", (await InspectionWire.ReadAsync(pipe, token))!.Kind);
            await session.WaitForConnectionAsync(token);
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task ProveNextRequestIsSnapshotAsync(InspectionSession session, NamedPipeClientStream pipe, CancellationToken token)
    {
        var reading = session.SnapshotAsync(cancellationToken: token);
        var request = (await InspectionWire.ReadAsync(pipe, token))!;
        Assert.Equal("tree", request.Kind);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", request.Id, new InspectionTree(1, [], [])), token);
        await reading;
    }
}
