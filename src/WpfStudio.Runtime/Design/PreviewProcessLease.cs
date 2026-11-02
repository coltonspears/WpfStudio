using System.ComponentModel;
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

    /// <summary>Revokes the lease and ends the process. Never throws: callers run this inside their own cleanup, which must finish.</summary>
    public void Terminate(string reason)
    {
        lock (_ownership)
        {
            if (_disposed) return;
            TerminateCore(reason);
        }
    }

    // Called with _ownership held. Keep the lock through Kill and WaitForExit: a concurrent Dispose may not close the
    // handle after revocation but before the watchdog actually kills.
    private void TerminateCore(string reason)
    {
        _revoked = true;
        _reason ??= reason;
        try { if (!_process.HasExited) _process.Kill(); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { /* Already gone, or exiting on its own. */ }
        // A process that ignores Kill for five seconds is left to the OS. Throwing here would abandon the caller's
        // cleanup half way through, which costs more than a stray process the OS reaps later.
        _process.WaitForExit(5000);
    }

    public void Dispose()
    {
        lock (_ownership)
        {
            if (_disposed) return;
            try { TerminateCore("Preview process disposed."); }
            finally { _disposed = true; _process.Dispose(); }
        }
    }
}
