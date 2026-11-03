using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

public sealed class DebuggerOutputTests
{
    [Fact]
    public async Task OutputFloodQueuesOneBoundedBatchAndClearDiscardsPendingText()
    {
        var dispatcher = new QueuedDispatcher();
        var shown = "";
        var output = new OutputBuffer(dispatcher, text => shown = text);
        for (var i = 0; i < 20_000; i++) output.Append(new string('x', 255) + "\n");
        output.Append("LAST OUTPUT");
        await Task.Delay(150);
        Assert.Single(dispatcher.Pending);
        dispatcher.Drain();
        Assert.InRange(shown.Length, 1, 250_000);
        Assert.EndsWith("LAST OUTPUT", shown);
        output.Append("This should be discarded");
        output.Clear();
        await Task.Delay(150); dispatcher.Drain();
        Assert.Empty(shown);
    }

    [Fact]
    public async Task ShownTextIsTrimmedFromTheFront()
    {
        var dispatcher = new QueuedDispatcher();
        var shown = "";
        var output = new OutputBuffer(dispatcher, text => shown = text);
        for (var batch = 0; batch < 3; batch++)
        {
            output.Append(new string('x', 100_000));
            await Task.Delay(150); dispatcher.Drain();
        }
        Assert.Equal(200_000, shown.Length);
        output.Append("END");
        await Task.Delay(150); dispatcher.Drain();
        Assert.Equal(200_003, shown.Length);
        Assert.EndsWith("END", shown);
    }

    [Fact]
    public async Task AValueLargerThanTheLimitKeepsItsTail()
    {
        var dispatcher = new QueuedDispatcher();
        var shown = "";
        var output = new OutputBuffer(dispatcher, text => shown = text);
        output.Append(new string('x', 300_000) + "END");
        await Task.Delay(150); dispatcher.Drain();
        Assert.Equal(250_000, shown.Length);
        Assert.EndsWith("END", shown);
    }

    [Fact]
    public async Task NothingIsShownOnceClosed()
    {
        var dispatcher = new QueuedDispatcher();
        var published = 0;
        var output = new OutputBuffer(dispatcher, _ => published++);
        output.Append("queued before closing");
        await Task.Delay(150);
        output.Close();
        output.Append("after closing");
        dispatcher.Drain();
        await Task.Delay(150); dispatcher.Drain();
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task AnAdapterEventFromAnyThreadAnnouncesItsStateOnTheDispatcher()
    {
        var dispatcher = new QueuedDispatcher();
        var messenger = new WeakReferenceMessenger();
        await using var viewModel = new DebuggerViewModel(new DebugSession(), dispatcher, new NoDebugAdapter(), messenger);
        var received = new List<(bool IsStopped, int Thread)>();
        messenger.Register<DebuggerStateMessage>(this, (_, message) => received.Add((message.IsStopped, Environment.CurrentManagedThreadId)));
        viewModel.IsStopped = true;
        received.Clear();

        var onEvent = typeof(DebuggerViewModel).GetMethod("OnDebugEvent", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<DebugEvent>>(viewModel);
        await Task.Run(() => onEvent(new DebugEvent("continued", JsonDocument.Parse("{}").RootElement)));
        Assert.Empty(received);

        dispatcher.Drain();
        Assert.Equal([(false, Environment.CurrentManagedThreadId)], received);
    }

    [Fact]
    public async Task TheChosenBreakpointIsAnnouncedAsASourceLine()
    {
        var messenger = new WeakReferenceMessenger();
        await using var viewModel = new DebuggerViewModel(new DebugSession(), new QueuedDispatcher(), new NoDebugAdapter(), messenger);
        DebuggerSourceMessage? received = null;
        messenger.Register<DebuggerSourceMessage>(this, (_, message) => received = message);
        viewModel.SelectedBreakpoint = new BreakpointViewModel { Path = "App.cs", Line = 12 };

        viewModel.NavigateBreakpointCommand.Execute(null);

        Assert.Equal(new DebuggerSourceMessage("App.cs", 12), received);
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        public ConcurrentQueue<Action> Pending { get; } = new();
        public void Post(Action action) => Pending.Enqueue(action);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { Post(action); return Task.CompletedTask; }
        public void Drain() { while (Pending.TryDequeue(out var action)) action(); }
    }
}
