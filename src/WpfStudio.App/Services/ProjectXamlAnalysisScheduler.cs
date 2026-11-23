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
    /// <summary><see cref="Completion"/> and the generation it was scheduled for, read together.</summary>
    public (Task Completion, long Generation) Latest { get { lock (_gate) return (Completion, _completionGeneration); } }
    public bool IsCurrent(long generation) { lock (_gate) return !_disposed && generation == _generation; }
    public long Invalidate()
    {
        CancellationTokenSource? superseded;
        long generation;
        lock (_gate)
        {
            superseded = _pending;
            generation = ++_generation;
        }
        Cancel(superseded);
        return generation;
    }
    public Task Schedule(bool immediate = false)
    {
        CancellationTokenSource pending;
        CancellationTokenSource? superseded;
        long generation;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            superseded = _pending;
            pending = _pending = new CancellationTokenSource();
            generation = ++_generation;
            _runs++;
        }
        Cancel(superseded);
        // An immediate scan runs on this thread up to its first await, which must not happen under the lock.
        var run = RunAsync(generation, pending, immediate);
        lock (_gate)
        {
            if (generation > _completionGeneration) { _completionGeneration = generation; Completion = run; }
        }
        return run;
    }
    // Cancelling runs the superseded scan's continuations on this thread, so it happens once the generation has moved on, or
    // they would take the old one as current, and outside the lock, as an immediate scan they start must. The scan may have
    // ended and disposed its source meanwhile.
    private static void Cancel(CancellationTokenSource? superseded)
    {
        try { superseded?.Cancel(); }
        catch (ObjectDisposedException) { }
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
