using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

/// <summary>netcoredbg is optional here, so a scripted adapter on a pipe decides when each request is answered.</summary>
public sealed class DebuggerWatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OverlappingRefreshesOfOneFrameLeaveOneRowPerExpressionAndKeepTheNewest(bool olderRequestFails) => OnUiThreadAsync(async (model, adapter) =>
    {
        await model.AddWatchAsync("a");
        await model.AddWatchAsync("b");
        model.IsStopped = true;
        model.SelectedFrame = new DebugFrame(7, "Main", null, 0);
        var older = await adapter.NextHeldEvaluationAsync();
        var adding = model.AddWatchAsync("c");
        var newer = await adapter.NextHeldEvaluationAsync();
        await adapter.AnswerAsync(older, olderRequestFails);
        await adapter.AnswerAsync(newer);
        await adapter.AnswerEverythingAsync();
        await adding;
        await Task.Delay(150);
        Assert.Equal(["a", "b", "c"], model.Watches.Select(watch => watch.Name));
        Assert.Equal(["a = 1", "b = 1", "c = 1"], model.Watches.Select(watch => watch.Name + " = " + watch.Value));
    });

    [Fact]
    public Task ClearingWatchesSupersedesARefreshAwaitingTheAdapter() => OnUiThreadAsync(async (model, adapter) =>
    {
        await model.AddWatchAsync("a");
        await model.AddWatchAsync("b");
        model.IsStopped = true;
        model.SelectedFrame = new DebugFrame(7, "Main", null, 0);
        var inFlight = await adapter.NextHeldEvaluationAsync();
        await model.ClearWatchesCommand.ExecuteAsync(null);
        await adapter.AnswerAsync(inFlight);
        await adapter.AnswerEverythingAsync();
        await Task.Delay(150);
        Assert.Empty(model.Watches);
    });

    private static Task OnUiThreadAsync(Func<DebuggerViewModel, ScriptedAdapter, Task> test)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var session = new DebugSession();
                    await using var adapter = await ScriptedAdapter.AttachAsync(session);
                    await using var model = new DebuggerViewModel(session, new TestDispatcher(dispatcher), new NoDebugAdapter());
                    await test(model, adapter);
                    result.TrySetResult();
                }
                catch (Exception exception) { result.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Debugger watch regression" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class TestDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public void Post(Action action) => dispatcher.BeginInvoke(action);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }

    /// <summary>Holds "evaluate" requests until the test answers them, and answers every other request at once.</summary>
    private sealed class ScriptedAdapter : IAsyncDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly Channel<JsonElement> held = Channel.CreateUnbounded<JsonElement>();
        private readonly SemaphoreSlim writer = new(1, 1);
        private readonly Task loop;
        private readonly object gate = new();
        private bool holding = true;
        private ScriptedAdapter(NamedPipeServerStream pipe) { this.pipe = pipe; loop = Task.Run(RunAsync); }

        public static async Task<ScriptedAdapter> AttachAsync(DebugSession session)
        {
            var name = "WpfStudio.DapTest." + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            var transport = new DapTransport(client, client);
            transport.Start();
            typeof(DebugSession).GetField("transport", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, transport);
            return new ScriptedAdapter(server);
        }

        public async Task<JsonElement> NextHeldEvaluationAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await held.Reader.ReadAsync(deadline.Token);
        }

        public async Task AnswerEverythingAsync()
        {
            lock (gate) holding = false;
            while (held.Reader.TryRead(out var request)) await AnswerAsync(request);
        }

        public Task AnswerAsync(JsonElement request, bool fail = false) => WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "response", request_seq = request.GetProperty("seq").GetInt32(), success = !fail, message = "evaluation failed",
            body = new { result = "1", type = "int", variablesReference = 0, scopes = Array.Empty<object>() }
        }));

        private async Task WriteAsync(byte[] payload)
        {
            await writer.WaitAsync();
            try { await DapTransport.WriteFrameAsync(pipe, payload); }
            finally { writer.Release(); }
        }

        private async Task RunAsync()
        {
            try
            {
                while (await DapTransport.ReadFrameAsync(pipe) is { } request)
                {
                    bool hold;
                    lock (gate)
                    {
                        hold = holding && request.GetProperty("command").GetString() == "evaluate";
                        if (hold) held.Writer.TryWrite(request);
                    }
                    if (!hold) await AnswerAsync(request);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await pipe.DisposeAsync();
            await loop;
        }
    }
}
