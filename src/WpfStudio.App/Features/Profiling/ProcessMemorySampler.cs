using System.Diagnostics;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Process-level memory at one moment: private bytes (committed, including native allocations) and the
/// working set (resident pages).</summary>
public sealed record ProcessMemorySample(DateTimeOffset Time, long PrivateBytes, long WorkingSetBytes);

/// <summary>Reads process memory counters for the live timeline. Never attaches to or suspends the target.</summary>
public interface IProcessMemorySampler
{
    /// <summary>Null when the process has exited, cannot be opened, or its identity changed.</summary>
    ProcessMemorySample? Sample(int processId, long startTimeUtcTicks);
}

public sealed class ProcessMemorySampler : IProcessMemorySampler, IDisposable
{
    private readonly Lock _gate = new();
    private Process? _process;
    private long _startTicks;
    private bool _disposed;

    public ProcessMemorySample? Sample(int processId, long startTimeUtcTicks)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            try
            {
                if (_process is null || _process.Id != processId || _startTicks != startTimeUtcTicks)
                {
                    ResetCore();
                    _process = Process.GetProcessById(processId);
                    _startTicks = startTimeUtcTicks;
                    if (_process.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks) { ResetCore(); return null; }
                }
                _process.Refresh();
                if (_process.HasExited) { ResetCore(); return null; }
                return new(DateTimeOffset.Now, _process.PrivateMemorySize64, _process.WorkingSet64);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                ResetCore(); return null;
            }
        }
    }

    private void ResetCore() { _process?.Dispose(); _process = null; _startTicks = 0; }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ResetCore();
        }
    }
}
