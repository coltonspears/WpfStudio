using System.Buffers.Binary;
using System.IO.Pipes;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionTransportTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("version")]
    [InlineData("pid")]
    [InlineData("expected-pid")]
    [InlineData("kind")]
    [InlineData("id")]
    public async Task UntrustedHelloCannotAuthenticateAnInspectionSession(string mismatch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        if (mismatch == "expected-pid") session.ExpectProcess(Environment.ProcessId + 1);
        using var pipe = await ConnectAsync(session, timeout.Token);
        var hello = Hello(session) with
        {
            Token = mismatch == "token" ? "other session" : session.Token,
            ProtocolVersion = mismatch == "version" ? InspectionProtocol.Version + 1 : InspectionProtocol.Version,
            ProcessId = mismatch == "pid" ? Environment.ProcessId + 1 : Environment.ProcessId
        };
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create(mismatch == "kind" ? "tree" : "hello",
            mismatch == "id" ? 1 : 0, hello), timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.WaitForConnectionAsync(timeout.Token));
        Assert.False(session.IsConnected);
        Assert.Null(session.Hello);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(InspectionProtocol.MaximumFrameBytes + 1)]
    public async Task InvalidFrameLengthDisconnectsWithoutAllocatingTheClaimedFrame(int length)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await pipe.WriteAsync(header, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.WaitForConnectionAsync(timeout.Token));
        Assert.False(session.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedOrInvalidJsonFrameRejectsHandshake(bool invalidJson)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using (var pipe = await ConnectAsync(session, timeout.Token))
        {
            await pipe.WriteAsync(invalidJson ? new byte[] { 1, 0, 0, 0, (byte)'{' } : new byte[] { 12, 0 }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }
        var error = await Record.ExceptionAsync(() => session.WaitForConnectionAsync(timeout.Token));
        if (invalidJson) Assert.IsAssignableFrom<System.Text.Json.JsonException>(error);
        else Assert.IsType<EndOfStreamException>(error);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task PausingBeforeHelloPreservesTheConnectionTimeoutBudget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession(connectionTimeout: TimeSpan.FromMilliseconds(300));
        session.SetDebuggerPaused(true);
        await Task.Delay(600, timeout.Token);
        using var pipe = await ConnectAsync(session, timeout.Token);
        await AuthenticateAsync(session, pipe, timeout.Token);
        Assert.True(session.IsConnected);
        Assert.True(session.IsDebuggerPaused);
        session.SetDebuggerPaused(false);
        Assert.True(session.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationOrDebuggerPauseDiscardsLateReplyAndKeepsConnection(bool pause)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        await AuthenticateAsync(session, pipe, timeout.Token);
        using var requestCancellation = new CancellationTokenSource();
        var pending = session.SnapshotAsync(cancellationToken: requestCancellation.Token);
        var request = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        if (pause) session.SetDebuggerPaused(true); else requestCancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.True(session.IsConnected);
        if (pause)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SnapshotAsync(cancellationToken: timeout.Token));
            session.SetDebuggerPaused(false);
        }
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", request.Id, new InspectionTree(1, [], [])), timeout.Token);
        var next = session.SnapshotAsync(cancellationToken: timeout.Token);
        var nextRequest = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", nextRequest.Id, new InspectionTree(2, [], [])), timeout.Token);
        Assert.Equal(2, (await next).Revision);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task UnansweredRequestTimesOutOnlyTheInspectionConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession(requestTimeout: TimeSpan.FromMilliseconds(100));
        using var pipe = await ConnectAsync(session, timeout.Token);
        await AuthenticateAsync(session, pipe, timeout.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => session.SnapshotAsync(cancellationToken: timeout.Token));
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task UnexpectedResponseKindClosesConnectionInsteadOfDeserializingWrongPayload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, timeout.Token);
        await AuthenticateAsync(session, pipe, timeout.Token);
        var pending = session.SnapshotAsync(cancellationToken: timeout.Token);
        var request = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("inspect", request.Id, new InspectionTree(1, [], [])), timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        // The pending request and disconnect are completed by the same reader continuation.
        while (session.IsConnected) await Task.Delay(10, timeout.Token);
    }

    private static InspectionHello Hello(InspectionSession session) =>
        new(InspectionProtocol.Version, session.Token, Environment.ProcessId, Environment.Version.ToString(), "test", ["tree", "inspect"]);

    private static async Task<NamedPipeClientStream> ConnectAsync(InspectionSession session, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", session.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(token); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task AuthenticateAsync(InspectionSession session, NamedPipeClientStream pipe, CancellationToken token)
    {
        session.ExpectProcess(Environment.ProcessId);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0, Hello(session)), token);
        var welcome = (await InspectionWire.ReadAsync(pipe, token))!;
        Assert.Equal("hello", welcome.Kind);
        Assert.Equal(session.SessionId, welcome.GetPayload<InspectionWelcome>().SessionId);
        await session.WaitForConnectionAsync(token);
    }
}
