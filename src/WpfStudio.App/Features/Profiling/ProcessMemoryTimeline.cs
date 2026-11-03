using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>The selected process's memory over time, sampled once a second while the profiler is on screen.</summary>
public sealed partial class ProcessMemoryTimeline(IProcessMemorySampler sampler, IMemorySessionHost host, Action samplesChanged) : ObservableObject
{
    private const int MaxSamples = 3600;
    private readonly List<ProcessMemorySample> _samples = [];
    private CancellationTokenSource? _monitor;
    public IReadOnlyList<string> Windows { get; } = ["1 min", "5 min", "15 min", "1 hour"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial ProfileProcess? Process { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLiveSamples), nameof(Title))] public partial IReadOnlyList<ProcessMemorySample> LiveSamples { get; private set; } = [];
    [ObservableProperty] public partial string LiveMemoryText { get; private set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowSeconds))] public partial string Window { get; set; } = "5 min";
    /// <summary>Set by the pane while it is visible; sampling stops when nobody is looking.</summary>
    [ObservableProperty] public partial bool IsMonitorActive { get; set; }
    [ObservableProperty] public partial bool MonitorMemory { get; set; } = true;
    public double WindowSeconds => Window switch { "1 min" => 60, "15 min" => 900, "1 hour" => 3600, _ => 300 };
    public bool HasLiveSamples => LiveSamples.Count > 0;
    public string Title => HasLiveSamples ? $"Process memory · {Process?.Name} (PID {Process?.Id})" : "Managed heap across snapshots";

    partial void OnIsMonitorActiveChanged(bool value) => UpdateMonitor();
    partial void OnMonitorMemoryChanged(bool value) => UpdateMonitor();

    [RelayCommand]
    private void SetWindow(string? window) { if (window is not null && Windows.Contains(window)) Window = window; }

    /// <summary>Starts over for another process; its samples say nothing about this one.</summary>
    internal void Watch(ProfileProcess? process)
    {
        Process = process;
        _samples.Clear(); LiveSamples = []; LiveMemoryText = "";
        samplesChanged(); UpdateMonitor();
    }

    private void UpdateMonitor()
    {
        var run = IsMonitorActive && MonitorMemory && Process is not null && !host.IsDisposed;
        if (!run) { Stop(); return; }
        if (_monitor is not null) return;
        _monitor = CancellationTokenSource.CreateLinkedTokenSource(host.Lifetime);
        MonitorAsync(_monitor.Token).Forget(host.ReportFault);
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do SampleMemory(); while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!host.IsDisposed) LiveMemoryText = "Memory sampling stopped: " + ex.Message; }
        finally
        {
            // A loop that ended for any reason must not leave its source registered, or monitoring could never restart.
            if (_monitor is { } monitor && monitor.Token == token) { monitor.Dispose(); _monitor = null; }
        }
    }

    /// <summary>Takes one process-memory sample for the timeline. Public so tests can drive the timeline without a timer.</summary>
    public void SampleMemory()
    {
        if (host.IsDisposed || Process is not { } process) return;
        var sample = sampler.Sample(process.Id, process.StartTimeUtcTicks);
        if (sample is null) { LiveMemoryText = "Process has exited"; return; }
        var first = _samples.Count == 0;
        _samples.Add(sample);
        if (_samples.Count > MaxSamples) _samples.RemoveRange(0, _samples.Count - MaxSamples);
        LiveSamples = _samples.ToArray();
        LiveMemoryText = $"Private {MemorySize.Format(sample.PrivateBytes)} · working set {MemorySize.Format(sample.WorkingSetBytes)}";
        if (first) samplesChanged();
    }

    internal void Stop() { _monitor?.Cancel(); _monitor?.Dispose(); _monitor = null; }
}
