using WpfStudio.Hosting;

namespace WpfStudio.Workspace.Tests;

public sealed class WorkerProcessHostDisposalTests
{
    [Fact]
    public async Task ConcurrentDisposeCallsAreAtomicAndIdempotent()
    {
        var host = new WorkerProcessHost(new WorkerProcessOptions(
            Path.Combine(Path.GetTempPath(), "nonexistent-worker.dll"),
            "wpfstudio-dispose-test-" + Guid.NewGuid().ToString("N"),
            TimeSpan.FromSeconds(5)));

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                gate.Wait();
                host.Dispose();
            }))
            .ToArray();

        gate.Set();
        await Task.WhenAll(tasks);

        Assert.True(host.HasExited);
        Assert.Throws<ObjectDisposedException>(() => host.Start());
    }
}
