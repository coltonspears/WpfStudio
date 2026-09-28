using System.Diagnostics;

namespace WpfStudio.Runtime.Design;

/// <summary>Owns the native handle returned by one launch, never a process found by numeric PID.</summary>
internal sealed class PreviewProcessLease : IDisposable
{
    private readonly object _ownership = new();
    private readonly Process _process;
    private bool _revoked;
    private bool _disposed;
    private string? _reason;

    public PreviewProcessLease(Process process, string sessionId)
    {
        // Accessing Handle retains the launch's original OS object even after its PID is reusable.
        _ = process.Handle;
        _process = process;
        ProcessId = process.Id;
        SessionId = sessionId;
    }

    public int ProcessId { get; }
    public string SessionId { get; }
    public string? Reason { get { lock (_ownership) return _reason; } }
    public bool IsAlive
    {
        get
        {
            lock (_ownership)
                return !_disposed && !_revoked && !_process.HasExited;
        }
    }
    public bool HasExited { get { lock (_ownership) return _disposed || _process.HasExited; } }

    public void Terminate(string reason)
    {
        lock (_ownership)
        {
            if (_disposed) return;
            _revoked = true;
            _reason ??= reason;
            // Keep the lock through Kill and WaitForExit. A concurrent Dispose may not
            // close the handle after revocation but before the watchdog actually kills.
            if (!_process.HasExited) _process.Kill();
            if (!_process.WaitForExit(5000))
                throw new TimeoutException("The owned preview process has not exited. Its native parent must remain alive.");
        }
    }

    public void Dispose()
    {
        lock (_ownership)
        {
            if (_disposed) return;
            Terminate("Preview process disposed.");
            _process.Dispose();
            _disposed = true;
        }
    }
}
