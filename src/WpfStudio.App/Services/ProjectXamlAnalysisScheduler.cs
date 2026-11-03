namespace WpfStudio.App.Services;

/// <summary>Coalesces changes and allows one scan at a time; generations also reject uncooperative late replies.</summary>
public sealed class ProjectXamlAnalysisScheduler(Func<long, CancellationToken, Task> scan,
    Action<long, Exception> report, TimeSpan? debounce = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _singleScan = new(1, 1);
    private CancellationTokenSource? _pending;
    private long _generation, _completionGeneration;
    private int _runs;
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
        CancellationTokenSource pending;
        long generation;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _pending?.Cancel();
            pending = _pending = new CancellationTokenSource();
            generation = ++_generation;
            _runs++;
        }
        // An immediate scan runs on this thread up to its first await, which must not happen under the lock.
        var run = RunAsync(generation, pending, immediate);
        lock (_gate)
        {
            if (generation > _completionGeneration) { _completionGeneration = generation; Completion = run; }
        }
        return run;
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
            bool last;
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending)) _pending = null;
                last = --_runs == 0 && _disposed;
            }
            pending.Dispose();
            if (last) _singleScan.Dispose();
        }
    }
    public void Dispose()
    {
        bool idle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _generation++; _pending?.Cancel();
            idle = _runs == 0;
        }
        if (idle) _singleScan.Dispose(); // Otherwise the last run does it: a cancelled scan may still be releasing the semaphore.
    }
}
