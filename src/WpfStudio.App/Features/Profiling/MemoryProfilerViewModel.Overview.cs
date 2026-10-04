using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Profiling.Visuals;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>The Overview: headline numbers, automatic findings, heap composition and the biggest owners.</summary>
public sealed partial class MemoryProfilerViewModel
{
    public IReadOnlyList<string> CompositionMetrics { get; } = ["Own bytes", "Retained bytes", "Objects"];
    public IReadOnlyList<string> CompositionGroupings { get; } = ["Namespace", "Assembly"];
    public ObservableCollection<FindingRow> Findings { get; } = [];
    [ObservableProperty] public partial IReadOnlyList<KpiTile> Kpis { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<BarSegment> GenerationSegments { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<TreemapItem> CompositionItems { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<RetainerRow> TopRetainers { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<GrowthRow> GrowthRows { get; set; } = [];
    [ObservableProperty] public partial string CompositionMetric { get; set; } = "Own bytes";
    [ObservableProperty] public partial string CompositionGrouping { get; set; } = "Namespace";
    [ObservableProperty] public partial string? SelectedCompositionKey { get; set; }
    [ObservableProperty] public partial string CompositionSelection { get; set; } = "Click a rectangle to see its type; double-click to open it.";
    [ObservableProperty] public partial MemoryTypeRow? CompositionSelectedType { get; set; }
    public bool HasFindings => Findings.Count > 0;
    public bool HasGrowth => GrowthRows.Count > 0;
    public string FindingsHeading => Findings.Count == 0 ? "No findings" : Findings.Count == 1 ? "1 finding" : $"{Findings.Count} findings";
    public string FindingsEmptyText => HasCapture ? "No leak or waste patterns were detected in this snapshot. Compare two snapshots to find growth." : "";

    partial void OnCompositionMetricChanged(string value) => RefreshComposition();
    partial void OnCompositionGroupingChanged(string value) => RefreshComposition();

    private void RefreshOverview()
    {
        if (_disposed) return;
        var summary = Summary;
        Findings.Clear();
        if (summary is null)
        {
            Kpis = []; GenerationSegments = []; CompositionItems = []; TopRetainers = []; GrowthRows = [];
            NotifyOverview(); return;
        }
        Kpis = BuildKpis(summary);
        var order = new[] { "Generation0", "Generation1", "Generation2", "Large", "Pinned", "Frozen" };
        GenerationSegments = (summary.Generations ?? []).Where(g => g.Bytes > 0)
            .Select(g => new BarSegment(MemorySize.Generation(g.Generation), g.Bytes, Array.IndexOf(order, g.Generation) is var i && i >= 0 ? i : 7,
                $"{MemorySize.Format(g.Bytes)} · {g.Count:N0} objects · {(double)g.Bytes / Math.Max(1, summary.ManagedBytes):P0}")).ToArray();
        foreach (var insight in summary.Insights ?? []) Findings.Add(new(insight));
        var growth = BaselineGrowthInsight();
        if (growth is not null) Findings.Insert(Findings.TakeWhile(f => f.Severity == "High").Count(), new(growth));
        var top = summary.TopRetainers ?? [];
        var max = top.Count == 0 ? 1 : Math.Max(1, top.Max(r => r.RetainedBytes));
        TopRetainers = top.Select(r => new RetainerRow(r, (double)r.RetainedBytes / max)).ToArray();
        var growing = Types.Where(t => t.BytesDelta > 0).OrderByDescending(t => t.BytesDelta).Take(8).ToArray();
        var maxGrowth = growing.Length == 0 ? 1 : Math.Max(1, growing.Max(t => t.BytesDelta ?? 0));
        GrowthRows = growing.Select(t => new GrowthRow(t, (double)(t.BytesDelta ?? 0) / maxGrowth)).ToArray();
        RefreshComposition();
        NotifyOverview();
    }

    private void NotifyOverview()
    {
        OnPropertyChanged(nameof(HasFindings)); OnPropertyChanged(nameof(HasGrowth));
        OnPropertyChanged(nameof(FindingsHeading)); OnPropertyChanged(nameof(FindingsEmptyText));
    }

    private IReadOnlyList<KpiTile> BuildKpis(HeapSummary s)
    {
        string Delta(long current, long? previous) => previous is long p ? (current - p == 0 ? "no change" : MemorySize.Signed(current - p) + " since baseline") : "";
        var reachableShare = s.ManagedBytes == 0 ? 0 : (double)s.ReachableBytes / s.ManagedBytes;
        var segments = s.ManagedBytes + s.FreeBytes;
        var freeShare = segments == 0 ? 0 : (double)s.FreeBytes / segments;
        var roots = s.RootKinds is { Count: > 0 } kinds
            ? string.Join(" · ", kinds.Where(k => k.Kind != "Frozen segment").OrderByDescending(k => k.Count).Take(2)
                .Select(k => $"{k.Count:N0} {MemoryLabels.RootKindName(k.Kind).ToLowerInvariant()}"))
            : $"{s.ReferenceCount:N0} references";
        return
        [
            new("Managed heap", MemorySize.Format(s.ManagedBytes), $"{s.ObjectCount:N0} objects · {s.Types.Count:N0} types", Delta(s.ManagedBytes, _baseline?.ManagedBytes), "Neutral",
                "Bytes of every managed object in the snapshot. Native memory, mapped files and GC free space are not included."),
            new("Kept alive", MemorySize.Format(s.ReachableBytes), $"{reachableShare:P0} of the heap", Delta(s.ReachableBytes, _baseline?.ReachableBytes), "Accent",
                "Objects reachable from a GC root. Leaks live here: anything still reachable after its owner is finished."),
            new("Collectible now", MemorySize.Format(s.UnreachableBytes), "Garbage the next GC frees", "", "Success",
                "Objects with no path from any GC root. They are garbage the collector has not reclaimed yet; they are not leaks."),
            new("GC roots", s.RootCount.ToString("N0", CultureInfo.CurrentCulture), roots, "", "Neutral",
                "Static fields, handles, stack slots and the finalizer queue keep objects alive. Every retained object traces back to one of these."),
            new("Heap free space", MemorySize.Format(s.FreeBytes), $"{freeShare:P0} of heap segments", "", freeShare > 0.25 && s.FreeBytes > 8 << 20 ? "Warning" : "Neutral",
                "Free gaps inside GC segments. A large share means fragmentation, often caused by pinning or the large object heap."),
        ];
    }

    /// <summary>Client-side finding: types that grew since the baseline, ranked by bytes.</summary>
    private MemoryInsight? BaselineGrowthInsight()
    {
        if (_baseline is null || Summary is null) return null;
        var grown = Types.Where(t => t.BytesDelta > 0 && t.CountDelta > 0).OrderByDescending(t => t.BytesDelta).ToArray();
        if (grown.Length == 0) return null;
        var bytes = grown.Sum(t => t.BytesDelta ?? 0);
        var items = grown.Take(20).Select(t => new MemoryInsightItem(t.ShortName, $"{t.CountGrowth} objects · now {t.Count:N0}", t.CountDelta ?? 0, t.BytesDelta ?? 0, t.Key)).ToArray();
        return new("baseline-growth", "Leak", bytes > 1 << 20 ? "Medium" : "Low", $"{grown.Length:N0} types grew since the baseline",
            $"Compared with the baseline, these types gained objects and {MemorySize.Format(bytes)} in total. Growth that survives repeated workloads is the classic leak signal.",
            "Repeat the workload a few times. Types that keep growing after each repetition are the best leak candidates; open one and check its retention paths.",
            grown.Length, bytes, items);
    }

    private void RefreshComposition()
    {
        if (Summary is null) { CompositionItems = []; return; }
        double Metric(MemoryTypeSummary t) => CompositionMetric switch { "Retained bytes" => t.RetainedBytes, "Objects" => t.Count, _ => t.Bytes };
        string Text(double value) => CompositionMetric == "Objects" ? $"{value:N0} objects" : MemorySize.Format((long)value);
        var byAssembly = CompositionGrouping == "Assembly";
        var groups = Summary.Types.Where(t => Metric(t) > 0)
            .GroupBy(t => byAssembly ? t.Module : MemoryLabels.Namespace(t.Name))
            .Select(g => (Name: g.Key, Types: g.OrderByDescending(Metric).ToArray(), Total: g.Sum(Metric)))
            .OrderByDescending(g => g.Total).ToArray();
        var items = new List<TreemapItem>();
        // Eight hues in fixed order; everything after the eighth group folds into a neutral "Other" group.
        for (var i = 0; i < groups.Length && i < 8; i++)
            items.Add(Group(groups[i].Name, groups[i].Types, groups[i].Total, i));
        if (groups.Length > 8)
        {
            var rest = groups.Skip(8).ToArray();
            var others = rest.SelectMany(g => g.Types).OrderByDescending(Metric).ToArray();
            items.Add(Group($"{rest.Length:N0} other {(byAssembly ? "assemblies" : "namespaces")}", others, rest.Sum(g => g.Total), -1));
        }
        CompositionItems = items;

        TreemapItem Group(string name, MemoryTypeSummary[] types, double total, int color)
        {
            var shown = types.Take(24).Select(t => new TreemapItem(t.Key, MemoryLabels.ShortType(t.Name), $"{t.Name}\n{t.Module} · {t.Count:N0} objects · own {MemorySize.Format(t.Bytes)} · retains {MemorySize.Format(t.RetainedBytes)}",
                Metric(t), color, Text(Metric(t)), null, t.Key)).ToList();
            if (types.Length > 24)
            {
                var remainder = types.Skip(24).Sum(Metric);
                if (remainder > 0) shown.Add(new TreemapItem("other|" + name, $"{types.Length - 24:N0} more types", "Smaller types in this group", remainder, color, Text(remainder)));
            }
            return new TreemapItem("group|" + name, name, $"{types.Length:N0} types", total, color, Text(total), shown);
        }
    }

    [RelayCommand]
    private void SelectComposition(TreemapItem? item)
    {
        if (item?.Tag is not string key) { CompositionSelectedType = null; CompositionSelection = item is null ? "" : $"{item.Label} · {item.ValueText}"; return; }
        CompositionSelectedType = Types.FirstOrDefault(t => t.Key == key);
        CompositionSelection = CompositionSelectedType is { } type ? $"{type.Name} · {type.Count:N0} objects · own {type.BytesText} · retains {type.RetainedText}" : item.Label;
    }

    [RelayCommand] private void OpenComposition(TreemapItem? item) { if (item?.Tag is string key) OpenType(key); }

    [RelayCommand]
    private void OpenFindingItem(FindingItemRow? row)
    {
        if (row is null) return;
        if (row.Item.ObjectId is int id) { ShowObject(id); return; }
        if (row.Item.TypeKey is string key) OpenType(key);
    }

    [RelayCommand] private void OpenRetainer(RetainerRow? row) { if (row is not null) ShowObject(row.Object.Id); }
    [RelayCommand] private void OpenGrowth(GrowthRow? row) { if (row is not null) OpenType(row.Type.Key); }

    /// <summary>Opens an object in the browser without leaving the current view.</summary>
    private void ShowObject(int id)
    {
        ShowBrowser = true; if (FocusGraph) FocusGraph = false;
        _ = InspectObjectAsync(id, _lifetime.Token);
    }

    [RelayCommand]
    private void OpenType(string? typeKey)
    {
        if (typeKey is null) return;
        if (TypeFilter.Length > 0 && !Types.Any(t => t.Key == typeKey)) TypeFilter = "";
        var row = Types.FirstOrDefault(t => t.Key == typeKey);
        if (row is null) return;
        SelectedView = TypesView;
        SelectedType = row;
    }
}
