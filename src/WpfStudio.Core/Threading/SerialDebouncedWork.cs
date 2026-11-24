namespace WpfStudio.Core.Threading;

/// <summary>
/// Serializes debounced asynchronous work so that each new request cancels and disposes any previous
/// pending work, links to a parent lifetime token, and becomes a no-op once disposed.
/// </summary>
public sealed class SerialDebouncedWork : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public SerialDebouncedWork(CancellationToken lifetime = default) => _lifetime = lifetime;

    public bool IsDisposed
    {
        get
        {
            lock (_gate) return _disposed;
        }
    }

    /// <summary>Cancels and disposes any currently scheduled or in-flight work.</summary>
    public void Cancel()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _pending;
            _pending = null;
        }
        CancelAndDispose(previous);
    }

    /// <summary>
    /// Cancels any previous work, waits <paramref name="delay"/>, and invokes <paramref name="action"/>
    /// with a token linked to the parent lifetime and <paramref name="cancellationToken"/>.
    /// Returns immediately if already disposed or if the parent lifetime is cancelled.
    /// </summary>
    public Task ScheduleAsync(TimeSpan delay, Action<CancellationToken> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(delay, token =>
        {
            action(token);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>
    /// Cancels any previous work, waits <paramref name="delay"/>, and executes <paramref name="work"/>
    /// with a token linked to the parent lifetime and <paramref name="cancellationToken"/>.
    /// Returns immediately if already disposed or if the parent lifetime is cancelled.
    /// </summary>
    public Task RunAsync(TimeSpan delay, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryBegin(cancellationToken, out var token)) return Task.CompletedTask;
        return ExecuteAsync(token, delay, work);
    }

    /// <summary>
    /// Cancels any previous work, then schedules <paramref name="work"/> on the thread pool after <paramref name="delay"/>.
    /// Returns immediately if already disposed or if the parent lifetime is cancelled.
    /// </summary>
    public Task ScheduleBackgroundAsync(TimeSpan delay, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryBegin(cancellationToken, out var token)) return Task.CompletedTask;
        return Task.Run(() => ExecuteAsync(token, delay, work), CancellationToken.None);
    }

    private bool TryBegin(CancellationToken externalToken, out CancellationToken token)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource? next = null;
        lock (_gate)
        {
            previous = _pending;
            if (_disposed || _lifetime.IsCancellationRequested || externalToken.IsCancellationRequested)
            {
                _pending = null;
                token = default;
            }
            else
            {
                next = externalToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime, externalToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
                _pending = next;
                token = next.Token;
            }
        }
        CancelAndDispose(previous);
        return next is not null;
    }

    private async Task ExecuteAsync(CancellationToken token, TimeSpan delay, Func<CancellationToken, Task> work)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            await work(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Expected when superseded, cancelled, or disposed.
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            // Expected if disposed concurrently with cancellation.
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        source.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            previous = _pending;
            _pending = null;
        }
        CancelAndDispose(previous);
    }
}
