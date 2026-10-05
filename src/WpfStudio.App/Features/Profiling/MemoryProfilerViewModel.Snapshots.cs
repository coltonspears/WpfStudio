using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Profiling.Visuals;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Snapshot history and comparison: every capture is kept as a summary, the newest one is compared with the
/// previous capture of the same process (or a baseline you pick), and types that grow in every snapshot are flagged.
/// A live process-memory timeline shows when each snapshot was taken.</summary>
public sealed partial class MemoryProfilerViewModel
{
    public const int SnapshotsView = 4;
    private const int MaxSnapshots = 30, MaxSamples = 3600;
    private readonly List<ProcessMemorySample> _samples = [];
    private CancellationTokenSource? _monitor;
    private int _snapshotNumber;
    private bool _baselinePinned;
    private string? _pendingSourceKey, _pendingSourceName;
    public ObservableCollection<SnapshotCard> Snapshots { get; } = [];
    public IReadOnlyList<string> TimelineWindows { get; } = ["1 min", "5 min", "15 min", "1 hour"];
    public IReadOnlyList<string> ComparisonFilters { get; } = ["Changed", "Growing", "New", "All"];
    [ObservableProperty] public partial SnapshotCard? CurrentSnapshot { get; set; }
    [ObservableProperty] public partial SnapshotCard? BaselineSnapshot { get; set; }
    [ObservableProperty] public partial bool AutoCompare { get; set; } = true;
    [ObservableProperty] public partial IReadOnlyList<ComparisonRow> ComparisonRows { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<KpiTile> ComparisonTiles { get; set; } = [];
    [ObservableProperty] public partial string ComparisonFilter { get; set; } = "Changed";
    [ObservableProperty] public partial string ComparisonSearch { get; set; } = "";
    [ObservableProperty] public partial bool ComparisonOnlyMyCode { get; set; }
    [ObservableProperty] public partial string ComparisonHeadline { get; set; } = "";
    [ObservableProperty] public partial string ComparisonCountText { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<LeakStep> LeakSteps { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ProcessMemorySample> LiveSamples { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<TimelineMarker> TimelineMarkers { get; set; } = [];
    [ObservableProperty] public partial string LiveMemoryText { get; set; } = "";
    [ObservableProperty] public partial string TimelineWindow { get; set; } = "5 min";
    /// <summary>Set by the pane while it is visible; sampling stops when nobody is looking.</summary>
    [ObservableProperty] public partial bool IsMonitorActive { get; set; }
    [ObservableProperty] public partial bool MonitorMemory { get; set; } = true;
    public double TimelineSeconds => TimelineWindow switch { "1 min" => 60, "15 min" => 900, "1 hour" => 3600, _ => 300 };
    public bool HasComparison => BaselineSnapshot is not null && CurrentSnapshot is not null && !ReferenceEquals(BaselineSnapshot, CurrentSnapshot);
    public bool HasSnapshotHistory => Snapshots.Count > 1;
    public bool IsLiveSource => SelectedProcess is not null;
    public bool HasLiveSamples => LiveSamples.Count > 0;
    public string SnapshotsTabText => Snapshots.Count > 1 ? $"Snapshots ({Snapshots.Count})" : "Snapshots";
    public string TimelineTitle => HasLiveSamples ? $"Process memory · {SelectedProcess?.Name} (PID {SelectedProcess?.Id})" : "Managed heap across snapshots";
    /// <summary>Same-source snapshots up to the current one, oldest first; the series behind trends and steady growth.</summary>
    private IReadOnlyList<SnapshotCard> Series
    {
        get
        {
            if (CurrentSnapshot is not { } current) return [];
            var index = Snapshots.IndexOf(current);
            return Snapshots.Take(index + 1).Where(s => s.SourceKey == current.SourceKey).TakeLast(8).ToArray();
        }
    }

    partial void OnComparisonFilterChanged(string value) => RefreshComparison();
    partial void OnComparisonSearchChanged(string value) => RefreshComparison();
    partial void OnComparisonOnlyMyCodeChanged(bool value) => RefreshComparison();
    partial void OnTimelineWindowChanged(string value) => OnPropertyChanged(nameof(TimelineSeconds));
    partial void OnIsMonitorActiveChanged(bool value) => UpdateMonitor();
    partial void OnMonitorMemoryChanged(bool value) => UpdateMonitor();
    partial void OnLiveSamplesChanged(IReadOnlyList<ProcessMemorySample> value) { OnPropertyChanged(nameof(HasLiveSamples)); OnPropertyChanged(nameof(TimelineTitle)); }
    partial void OnSelectedProcessChanged(ProfileProcess? value)
    {
        _samples.Clear(); LiveSamples = []; LiveMemoryText = "";
        OnPropertyChanged(nameof(IsLiveSource)); OnPropertyChanged(nameof(TimelineTitle));
        RefreshTimelineMarkers(); UpdateMonitor();
    }

    private static string ProcessKey(ProfileProcess process) => $"pid:{process.Id}:{process.StartTimeUtcTicks}";

    /// <summary>Records where the next capture comes from, so snapshots of the same process line up.</summary>
    private void PrepareSnapshotSource(HeapCaptureRequest request)
    {
        if (request.DumpPath is string path) { _pendingSourceKey = "dump:" + System.IO.Path.GetFullPath(path).ToUpperInvariant(); _pendingSourceName = System.IO.Path.GetFileName(path); }
        else
        {
            var process = Processes.FirstOrDefault(p => p.Id == request.ProcessId) ?? SelectedProcess;
            _pendingSourceKey = $"pid:{request.ProcessId}:{request.ProcessStartTimeUtcTicks}";
            _pendingSourceName = process is null ? $"PID {request.ProcessId}" : $"{process.Name} (PID {process.Id})";
        }
    }

    /// <summary>Adds the new capture to the history and chooses what it is compared with.</summary>
    private void RecordSnapshot(HeapSummary summary)
    {
        var card = new SnapshotCard(++_snapshotNumber, summary, _pendingSourceKey ?? summary.Source, _pendingSourceName ?? summary.Source);
        Snapshots.Add(card);
        while (Snapshots.Count > MaxSnapshots)
        {
            var oldest = Snapshots.FirstOrDefault(s => !ReferenceEquals(s, BaselineSnapshot)) ?? Snapshots[0];
            Snapshots.Remove(oldest);
        }
        CurrentSnapshot = card;
        if (_baselinePinned && BaselineSnapshot is { } pinned && Snapshots.Contains(pinned)) ApplyComparison(pinned, pinned: true);
        else if (AutoCompare && Snapshots.Take(Snapshots.Count - 1).LastOrDefault(s => s.SourceKey == card.SourceKey) is { } earlier) ApplyComparison(earlier, pinned: false);
        else ApplyComparison(null, pinned: false);
    }

    private void ApplyComparison(SnapshotCard? baseline, bool pinned)
    {
        _baselinePinned = pinned && baseline is not null;
        BaselineSnapshot = baseline;
        _baseline = baseline?.Summary;
        RefreshTypes(); UpdateComparison(); RefreshOverview(); RefreshComparison(); RefreshSnapshotCards();
        OnPropertyChanged(nameof(HasBaseline)); OnPropertyChanged(nameof(Baseline)); OnPropertyChanged(nameof(BaselineDescription));
        OnPropertyChanged(nameof(HasComparison));
        ClearBaselineCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Compares the current snapshot with an earlier one and keeps that choice for later captures.</summary>
    [RelayCommand]
    private void CompareWith(SnapshotCard? card)
    {
        if (card is null || !Snapshots.Contains(card)) return;
        if (ReferenceEquals(card, CurrentSnapshot)) { Status = "That is the snapshot you are viewing. Choose an earlier snapshot to compare with."; return; }
        ApplyComparison(card, pinned: true);
        if (SelectedView != SnapshotsView && SelectedView != OverviewView) SelectedView = SnapshotsView;
        Status = $"Comparing #{CurrentSnapshot?.Number} with #{card.Number}. Later captures keep comparing with #{card.Number} until you clear it.";
    }

    [RelayCommand]
    private void RemoveSnapshot(SnapshotCard? card)
    {
        if (card is null || ReferenceEquals(card, CurrentSnapshot)) return;
        Snapshots.Remove(card);
        if (ReferenceEquals(card, BaselineSnapshot)) ApplyComparison(null, pinned: false);
        else { RefreshSnapshotCards(); RefreshComparison(); RefreshOverview(); }
    }

    [RelayCommand]
    private void SetTimelineWindow(string? window) { if (window is not null && TimelineWindows.Contains(window)) TimelineWindow = window; }
    [RelayCommand]
    private void SetComparisonFilter(string? filter) { if (filter is not null && ComparisonFilters.Contains(filter)) ComparisonFilter = filter; }

    private void RefreshSnapshotCards()
    {
        var max = Snapshots.Count == 0 ? 1 : Math.Max(1, Snapshots.Max(s => s.Summary.ManagedBytes));
        SnapshotCard? previous = null;
        foreach (var card in Snapshots)
        {
            var before = previous is not null && previous.SourceKey == card.SourceKey ? previous : null;
            card.Update(ReferenceEquals(card, CurrentSnapshot), ReferenceEquals(card, BaselineSnapshot), (double)card.Summary.ManagedBytes / max, before);
            previous = card;
        }
        OnPropertyChanged(nameof(HasSnapshotHistory)); OnPropertyChanged(nameof(SnapshotsTabText));
        RefreshTimelineMarkers(); RefreshLeakSteps();
    }

    private void RefreshTimelineMarkers()
    {
        // Live mode shows snapshots of the watched process; otherwise the current source's series.
        var key = SelectedProcess is { } process && _samples.Count > 0 ? ProcessKey(process) : CurrentSnapshot?.SourceKey;
        TimelineMarkers = key is null ? [] : Snapshots.Where(s => s.SourceKey == key).Select(s => new TimelineMarker(s.Summary.CapturedAt, s.Title + (s.IsBaseline ? " baseline" : ""),
            s.Summary.ManagedBytes, $"Managed heap {s.SizeText} · {s.ObjectsText}" + (s.FindingCount > 0 ? $" · {s.FindingCount} findings" : ""), s.IsCurrent, s.IsBaseline, s)).ToArray();
    }

    private void RefreshLeakSteps()
    {
        var count = Series.Count;
        LeakSteps =
        [
            new("1", "Capture a baseline", "Before the action you suspect.", count >= 1, count == 0),
            new("2", "Repeat the action", "Open and close the window, run the import…", count >= 2, count == 1),
            new("3", "Capture again", "Growth since the baseline appears below.", count >= 2, count == 1),
            new("4", "Confirm", "Repeat once more: steady growth is the leak signal.", count >= 3, count == 2),
        ];
    }

    /// <summary>Per-type comparison of the current snapshot with the baseline, with each type's trend across the series.</summary>
    private void RefreshComparison()
    {
        if (_disposed) return;
        if (!HasComparison || Summary is not { } current || _baseline is not { } baseline)
        {
            ComparisonRows = []; ComparisonTiles = []; ComparisonHeadline = ""; ComparisonCountText = "";
            OnPropertyChanged(nameof(HasComparison));
            return;
        }
        var series = Series;
        var steady = SteadyGrowth(series);
        var before = baseline.Types.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var now = current.Types.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var seriesTypes = series.Select(s => s.Summary.Types.ToDictionary(t => t.Key, t => (double)t.Bytes, StringComparer.Ordinal)).ToArray();
        var all = now.Keys.Concat(before.Keys).Distinct(StringComparer.Ordinal).Select(key =>
        {
            before.TryGetValue(key, out var b); now.TryGetValue(key, out var n);
            var type = n ?? b!;
            var trend = seriesTypes.Length > 1 ? seriesTypes.Select(s => s.GetValueOrDefault(key)).ToArray() : [];
            return new ComparisonRow(key, type.Name, type.Module, b?.Count ?? 0, n?.Count ?? 0, b?.Bytes ?? 0, n?.Bytes ?? 0, b is null, n is null, steady.Contains(key), trend);
        }).ToArray();
        var grew = all.Count(r => r.BytesDelta > 0 && r.CountDelta > 0);
        var added = all.Count(r => r.IsNew);
        var gone = all.Count(r => r.IsGone);
        IEnumerable<ComparisonRow> rows = ComparisonFilter switch
        {
            "Growing" => all.Where(r => r.BytesDelta > 0 || r.CountDelta > 0).OrderByDescending(r => r.BytesDelta),
            "New" => all.Where(r => r.IsNew).OrderByDescending(r => r.Bytes),
            "All" => all.OrderByDescending(r => Math.Abs(r.BytesDelta)).ThenByDescending(r => r.Bytes),
            _ => all.Where(r => r.BytesDelta != 0 || r.CountDelta != 0).OrderByDescending(r => Math.Abs(r.BytesDelta))
        };
        if (ComparisonOnlyMyCode) rows = rows.Where(r => !MemoryLabels.IsFrameworkModule(r.Module));
        if (ComparisonSearch.Trim() is { Length: > 0 } search) rows = rows.Where(r => r.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        // Steady growers first: they are the strongest leak candidates.
        var list = rows.OrderByDescending(r => r.GrowsEveryTime && r.BytesDelta > 0)
            .ThenByDescending(r => r.GrowsEveryTime && r.BytesDelta > 0 && !MemoryLabels.IsFrameworkModule(r.Module)).Take(500).ToArray();
        var maxDelta = list.Length == 0 ? 1 : Math.Max(1, list.Max(r => Math.Abs(r.BytesDelta)));
        ComparisonRows = list.Select(r => r with { DeltaRatio = (double)r.BytesDelta / maxDelta }).ToArray();
        ComparisonCountText = list.Length == 0 ? "No matching types" : $"{list.Length:N0} type{(list.Length == 1 ? "" : "s")}" + (list.Length == 500 ? " (first 500)" : "");

        var gap = current.CapturedAt - baseline.CapturedAt;
        ComparisonHeadline = $"#{CurrentSnapshot!.Number} compared with #{BaselineSnapshot!.Number}" + (gap > TimeSpan.Zero ? $", {Elapsed(gap)} later" : "") +
            (CurrentSnapshot.SourceKey != BaselineSnapshot.SourceKey ? " · different sources" : "") +
            $" · {grew:N0} type{(grew == 1 ? "" : "s")} grew" + (added > 0 ? $" · {added:N0} new" : "") + (gone > 0 ? $" · {gone:N0} gone" : "") +
            (steady.Count > 0 ? $" · {steady.Count:N0} grew in every one of the last {series.Count} snapshots" : "");
        string Signed(long v) => v == 0 ? "no change" : MemorySize.Signed(v);
        string SignedCount(long v) => v == 0 ? "no change" : v.ToString("+#,0;−#,0", CultureInfo.CurrentCulture);
        ComparisonTiles =
        [
            new("Managed heap", Signed(current.ManagedBytes - baseline.ManagedBytes), $"{MemorySize.Format(baseline.ManagedBytes)} → {MemorySize.Format(current.ManagedBytes)}", "", Tone(current.ManagedBytes - baseline.ManagedBytes), "Change in the bytes of all managed objects."),
            new("Kept alive", Signed(current.ReachableBytes - baseline.ReachableBytes), $"{MemorySize.Format(baseline.ReachableBytes)} → {MemorySize.Format(current.ReachableBytes)}", "", Tone(current.ReachableBytes - baseline.ReachableBytes), "Change in reachable bytes. Garbage that has not been collected yet is excluded, so this is the number that matters for leaks."),
            new("Objects", SignedCount(current.ObjectCount - baseline.ObjectCount), $"{baseline.ObjectCount:N0} → {current.ObjectCount:N0}", "", Tone(current.ObjectCount - baseline.ObjectCount), "Change in the number of managed objects."),
            new("Steady growth", steady.Count.ToString("N0", CultureInfo.CurrentCulture), series.Count >= 3 ? $"types grew in all {series.Count} snapshots" : "needs 3 snapshots of one process", "", steady.Count > 0 ? "Danger" : "Neutral", "Types whose instance count rose in every snapshot of this process. Capture after each repetition of a workload to use it."),
        ];
        OnPropertyChanged(nameof(HasComparison));

        static string Tone(long delta) => delta > 0 ? "Danger" : delta < 0 ? "Success" : "Neutral";
    }

    private static string Elapsed(TimeSpan gap) => gap.TotalSeconds < 90 ? $"{gap.TotalSeconds:N0} s" : gap.TotalMinutes < 90 ? $"{gap.TotalMinutes:N0} min" : $"{gap.TotalHours:N1} h";

    /// <summary>Types whose count rose between every consecutive pair of at least three same-source snapshots.</summary>
    private static HashSet<string> SteadyGrowth(IReadOnlyList<SnapshotCard> series)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (series.Count < 3) return result;
        var counts = series.Select(s => s.Summary.Types.ToDictionary(t => t.Key, t => t.Count, StringComparer.Ordinal)).ToArray();
        foreach (var key in counts[^1].Keys)
        {
            var growing = true;
            for (var i = 1; i < counts.Length && growing; i++) growing = counts[i].GetValueOrDefault(key) > counts[i - 1].GetValueOrDefault(key);
            if (growing) result.Add(key);
        }
        return result;
    }

    /// <summary>Client-side finding for the Overview: types that grew in every snapshot of this process.</summary>
    private MemoryInsight? SteadyGrowthInsight()
    {
        var series = Series;
        var steady = SteadyGrowth(series);
        if (steady.Count == 0 || Summary is not { } current) return null;
        var first = series[0].Summary.Types.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var rows = current.Types.Where(t => steady.Contains(t.Key)).Select(t =>
        {
            first.TryGetValue(t.Key, out var start);
            return (Type: t, Count: t.Count - (start?.Count ?? 0), Bytes: t.Bytes - (start?.Bytes ?? 0));
        }).OrderByDescending(r => !MemoryLabels.IsFrameworkModule(r.Type.Module)).ThenByDescending(r => r.Bytes).ToArray();
        var bytes = rows.Sum(r => Math.Max(0, r.Bytes));
        var items = rows.Take(20).Select(r => new MemoryInsightItem(MemoryLabels.ShortType(r.Type.Name),
            $"+{r.Count:N0} objects over {series.Count} snapshots · now {r.Type.Count:N0}", r.Count, r.Bytes, r.Type.Key)).ToArray();
        return new("steady-growth", "Leak", rows.Any(r => !MemoryLabels.IsFrameworkModule(r.Type.Module)) ? "High" : "Medium",
            $"{steady.Count:N0} type{(steady.Count == 1 ? "" : "s")} grew in every snapshot",
            $"Across the last {series.Count} snapshots of this process, these types gained instances every time ({MemorySize.Format(bytes)} in total). Memory that only ever grows while a workload repeats is the classic leak pattern.",
            "Open the type and read its retention paths: the owner that keeps accumulating instances (a static list, an event, a cache) is usually the bug. Group the instances by retention to see which path grows.",
            steady.Count, bytes, items);
    }

    // ------------------------------------------------------------------ live process memory

    private void UpdateMonitor()
    {
        var run = IsMonitorActive && MonitorMemory && SelectedProcess is not null && !_disposed;
        if (!run) { _monitor?.Cancel(); _monitor?.Dispose(); _monitor = null; return; }
        if (_monitor is not null) return;
        _monitor = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = MonitorAsync(_monitor.Token);
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do SampleMemory(); while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Takes one process-memory sample for the timeline. Public so tests can drive the timeline without a timer.</summary>
    public void SampleMemory()
    {
        if (_disposed || SelectedProcess is not { } process) return;
        var sample = _sampler.Sample(process.Id, process.StartTimeUtcTicks);
        if (sample is null) { LiveMemoryText = "Process has exited"; return; }
        var first = _samples.Count == 0;
        _samples.Add(sample);
        if (_samples.Count > MaxSamples) _samples.RemoveRange(0, _samples.Count - MaxSamples);
        LiveSamples = _samples.ToArray();
        LiveMemoryText = $"Private {MemorySize.Format(sample.PrivateBytes)} · working set {MemorySize.Format(sample.WorkingSetBytes)}";
        if (first) RefreshTimelineMarkers();
    }

    private void StopMonitor() { _monitor?.Cancel(); _monitor?.Dispose(); _monitor = null; }
}

/// <summary>One captured snapshot in the history strip.</summary>
public sealed partial class SnapshotCard(int number, HeapSummary summary, string sourceKey, string sourceName) : ObservableObject
{
    public int Number => number;
    public HeapSummary Summary => summary;
    public string SourceKey => sourceKey;
    public string SourceName => sourceName;
    public string Title => $"#{number}";
    public string TimeText => summary.CapturedAt.LocalDateTime.ToString("T", CultureInfo.CurrentCulture);
    public string SizeText => MemorySize.Format(summary.ManagedBytes);
    public string ObjectsText => $"{summary.ObjectCount:N0} objects";
    public int FindingCount => summary.Insights?.Count ?? 0;
    public string FindingText => FindingCount == 0 ? "" : FindingCount == 1 ? "1 finding" : $"{FindingCount} findings";
    public string ToolTipText => $"Snapshot #{number} · {sourceName}\nCaptured {summary.CapturedAt.LocalDateTime:G}\nManaged heap {SizeText} · {ObjectsText}\nKept alive {MemorySize.Format(summary.ReachableBytes)}";
    [ObservableProperty] public partial bool IsCurrent { get; private set; }
    [ObservableProperty] public partial bool IsBaseline { get; private set; }
    [ObservableProperty] public partial double SizeRatio { get; private set; }
    [ObservableProperty] public partial string DeltaText { get; private set; } = "";
    [ObservableProperty] public partial bool IsGrowth { get; private set; }
    public bool CanCompare => !IsCurrent;

    internal void Update(bool current, bool baseline, double ratio, SnapshotCard? previous)
    {
        IsCurrent = current; IsBaseline = baseline; SizeRatio = ratio;
        var delta = previous is null ? 0 : summary.ManagedBytes - previous.Summary.ManagedBytes;
        DeltaText = previous is null ? "first of this source" : delta == 0 ? "no change" : MemorySize.Signed(delta) + $" vs #{previous.Number}";
        IsGrowth = delta > 0;
        OnPropertyChanged(nameof(CanCompare));
    }
}

/// <summary>A type's change between the baseline and the current snapshot. Trend holds its bytes in each snapshot of the series.</summary>
public sealed record ComparisonRow(string Key, string Name, string Module, int BaselineCount, int Count, long BaselineBytes, long Bytes,
    bool IsNew, bool IsGone, bool GrowsEveryTime, IReadOnlyList<double> Trend, double DeltaRatio = 0)
{
    public string ShortName => MemoryLabels.ShortType(Name);
    public string Namespace => MemoryLabels.Namespace(Name);
    public long BytesDelta => Bytes - BaselineBytes;
    public int CountDelta => Count - BaselineCount;
    public string BytesDeltaText => BytesDelta == 0 ? "0 B" : MemorySize.Signed(BytesDelta);
    public string CountDeltaText => CountDelta.ToString("+#,0;−#,0;0", CultureInfo.CurrentCulture);
    public string CountsText => $"{BaselineCount:N0} → {Count:N0}";
    public bool IsGrowing => BytesDelta > 0;
    public bool IsShrinking => BytesDelta < 0;
    public bool HasTrend => Trend.Count > 1;
    public string Badge => IsNew ? "New" : IsGone ? "Gone" : GrowsEveryTime && BytesDelta > 0 ? "Grows every time" : "";
    public string ToolTipText => $"{Name}\n{Module}\n{CountsText} objects ({CountDeltaText}) · {MemorySize.Format(BaselineBytes)} → {MemorySize.Format(Bytes)} ({BytesDeltaText})" +
        (GrowsEveryTime ? "\nGrew in every snapshot of this process" : "");
}

/// <summary>One step of the guided leak hunt.</summary>
public sealed record LeakStep(string Number, string Title, string Detail, bool IsDone, bool IsNext);
