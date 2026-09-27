using WpfStudio.App.Services;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed class BufferedBuildProgressTests
{
    [Fact]
    public void NoisyOutputSchedulesOneCallbackAndKeepsBoundedRecentData()
    {
        var dispatcher = new QueuedDispatcher();
        var batches = new List<IReadOnlyList<BuildOutputEvent>>();
        using var progress = new BufferedBuildProgress(dispatcher, batches.Add);
        for (int index = 0; index < 10_000; index++) progress.Report(new BuildOutputEvent($"line-{index}:" + new string('x', 400)));
        Assert.Single(dispatcher.Callbacks);
        Assert.Empty(batches);
        dispatcher.RunNext();
        var batch = Assert.Single(batches);
        Assert.Contains(batch, item => item.Text.Contains("Output truncated", StringComparison.Ordinal));
        Assert.StartsWith("line-9999:", batch[^1].Text);
        Assert.True(batch.Sum(item => item.Text.Length) < 132_000);
        Assert.True(batch.Count <= 1001);
    }

    [Fact]
    public void FlushReschedulesAndDisposalDrainsOnlyOnce()
    {
        var dispatcher = new QueuedDispatcher();
        var output = new List<BuildOutputEvent>();
        var progress = new BufferedBuildProgress(dispatcher, output.AddRange);
        progress.Report(new BuildOutputEvent("first"));
        dispatcher.RunNext();
        progress.Report(new BuildOutputEvent("second"));
        Assert.Single(dispatcher.Callbacks);
        progress.Dispose();
        progress.Report(new BuildOutputEvent("ignored"));
        dispatcher.RunNext();
        Assert.Equal(["first", "second"], output.Select(item => item.Text));
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        public Queue<Action> Callbacks { get; } = new();
        public void Post(Action action) => Callbacks.Enqueue(action);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { Post(action); return Task.CompletedTask; }
        public void RunNext() => Callbacks.Dequeue()();
    }
}
