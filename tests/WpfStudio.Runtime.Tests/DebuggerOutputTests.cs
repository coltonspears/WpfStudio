using System.Collections.Concurrent;
using System.Reflection;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

public sealed class DebuggerOutputTests
{
    [Fact]
    public async Task OutputFloodQueuesOneBoundedBatchAndClearDiscardsPendingText()
    {
        var dispatcher = new QueuedDispatcher();
        await using var viewModel = new DebuggerViewModel(new DebugSession(), dispatcher);
        var append = typeof(DebuggerViewModel).GetMethod("AppendOutput", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<string>>(viewModel);
        for (var i = 0; i < 20_000; i++) append(new string('x', 255) + "\n");
        append("LAST OUTPUT");
        await Task.Delay(150);
        Assert.Single(dispatcher.Pending);
        dispatcher.Drain();
        Assert.InRange(viewModel.Output.Length, 1, 250_000);
        Assert.EndsWith("LAST OUTPUT", viewModel.Output);
        append("This should be discarded");
        viewModel.ClearOutputCommand.Execute(null);
        await Task.Delay(150); dispatcher.Drain();
        Assert.Empty(viewModel.Output);
    }
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        public ConcurrentQueue<Action> Pending { get; } = new();
        public void Post(Action action) => Pending.Enqueue(action);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { Post(action); return Task.CompletedTask; }
        public void Drain() { while (Pending.TryDequeue(out var action)) action(); }
    }
}
