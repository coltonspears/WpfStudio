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
    private Process? _process;
    private long _startTicks;

    public ProcessMemorySample? Sample(int processId, long startTimeUtcTicks)
    {
        try
        {
            if (_process is null || _process.Id != processId || _startTicks != startTimeUtcTicks)
            {
                _process?.Dispose();
                _process = Process.GetProcessById(processId);
                _startTicks = startTimeUtcTicks;
                if (_process.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks) { Reset(); return null; }
            }
            _process.Refresh();
            if (_process.HasExited) { Reset(); return null; }
            return new(DateTimeOffset.Now, _process.PrivateMemorySize64, _process.WorkingSet64);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Reset(); return null;
        }
    }

    private void Reset() { _process?.Dispose(); _process = null; _startTicks = 0; }
    public void Dispose() => Reset();
}
