namespace WpfStudio.App.Services;

/// <summary>
/// Lets one of a set of long operations run at a time. A command's own <c>IsRunning</c> only stops it overlapping itself;
/// these operations also exclude each other, some are not started by a command, and one Stop button has to reach whichever
/// is running. Used on the UI thread only, so a claim made after an await cannot race the check before it.
/// </summary>
public sealed class OperationGate(Action<bool> busyChanged, CancellationToken lifetime = default)
{
    private Lease? _current;

    public bool IsBusy => _current is not null;

    /// <summary>Claims the gate for an operation that <paramref name="token"/> can also cancel; null while another operation holds it.</summary>
    public Lease? TryEnter(CancellationToken token = default)
    {
        if (_current is not null) return null;
        _current = new(this, CancellationTokenSource.CreateLinkedTokenSource(token, lifetime));
        busyChanged(true);
        return _current;
    }

    /// <summary>Cancels whichever operation holds the gate.</summary>
    public void Cancel() => _current?.Cancel();

    private void Release(Lease lease)
    {
        if (!ReferenceEquals(_current, lease)) return;
        _current = null;
        busyChanged(false);
    }

    /// <summary>One operation's hold on the gate; disposing it frees the gate.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly OperationGate _gate;
        private readonly CancellationTokenSource _source;
        internal Lease(OperationGate gate, CancellationTokenSource source) { _gate = gate; _source = source; Token = source.Token; }
        public CancellationToken Token { get; }
        internal void Cancel() => _source.Cancel();
        public void Dispose() { _gate.Release(this); _source.Dispose(); }
    }
}
