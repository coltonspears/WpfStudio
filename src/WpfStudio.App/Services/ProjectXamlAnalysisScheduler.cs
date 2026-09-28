namespace WpfStudio.App.Services;

/// <summary>Coalesces changes and allows one scan at a time; generations also reject uncooperative late replies.</summary>
public sealed class ProjectXamlAnalysisScheduler(Func<long, CancellationToken, Task> scan,
    Action<long, Exception> report, TimeSpan? debounce = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _singleScan = new(1, 1);
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool IsCurrent(long generation) { lock (_gate) return !_disposed && generation == _generation; }
    public long Invalidate()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            return ++_generation;
        }
    }
    public Task Schedule(bool immediate = false)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _pending?.Cancel();
            var pending = _pending = new CancellationTokenSource();
            var generation = ++_generation;
            return Completion = RunAsync(generation, pending, immediate);
        }
    }
    private async Task RunAsync(long generation, CancellationTokenSource pending, bool immediate)
    {
        var token = pending.Token;
        try
        {
            if (!immediate) await Task.Delay(debounce ?? TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            await _singleScan.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsCurrent(generation)) await scan(generation, token).ConfigureAwait(false);
            }
            finally { _singleScan.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (IsCurrent(generation)) report(generation, exception); }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_pending, pending)) _pending = null; }
            pending.Dispose();
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _generation++; _pending?.Cancel(); }
    }
}
