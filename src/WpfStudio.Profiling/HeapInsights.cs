using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling;

/// <summary>Facts read from object memory while the heap is walked. Analysis code turns them into inspections.</summary>
public sealed class HeapExtras
{
    public int PointerSize { get; init; } = 8;
    /// <summary>Objects whose disposed flag (for example <c>_disposed</c>) was true.</summary>
    public HashSet<int> Disposed { get; } = [];
    /// <summary>WPF windows that have been closed (<c>Window._disposed</c> is true).</summary>
    public HashSet<int> ClosedWindows { get; } = [];
    /// <summary>Lengths of reference-type arrays that are large enough to be worth checking for sparseness.</summary>
    public Dictionary<int, int> ReferenceArrayLengths { get; } = [];
    /// <summary>String contents grouped for duplicate detection.</summary>
    public Dictionary<string, DuplicateString> Strings { get; } = new(StringComparer.Ordinal);
    public long FreeBytes { get; set; }
    public long LargeFreeBytes { get; set; }
    public bool StringsTruncated { get; set; }

    public sealed class DuplicateString { public int Count; public long Bytes; public int Sample; }
}

/// <summary>Automatic leak, waste and runtime inspections. Each one describes evidence and next steps; none of them
/// claims a leak on its own.</summary>
public static class HeapInsights
{
    public static IReadOnlyList<MemoryInsight> Compute(HeapGraph graph, HeapAnalysis analysis, HeapExtras extras, CancellationToken token = default)
    {
        var insights = new List<MemoryInsight>();
        ClosedWindows(graph, analysis, extras, insights);
        DisposedButReachable(graph, analysis, extras, insights);
        token.ThrowIfCancellationRequested();
        EventHandlerRetention(graph, analysis, insights);
        token.ThrowIfCancellationRequested();
        FinalizerOnly(graph, analysis, insights, token);
        DuplicateStrings(graph, extras, insights);
        SparseArrays(graph, analysis, extras, insights);
        LargeObjects(graph, analysis, extras, insights);
        Pinning(graph, analysis, insights);
        return insights.OrderBy(i => Rank(i.Severity)).ThenByDescending(i => i.Bytes).ToArray();
    }

    private static int Rank(string severity) => severity switch { "High" => 0, "Medium" => 1, "Low" => 2, _ => 3 };

    private static void ClosedWindows(HeapGraph graph, HeapAnalysis analysis, HeapExtras extras, List<MemoryInsight> insights)
    {
        var alive = extras.ClosedWindows.Where(id => analysis.Reachable[id]).OrderByDescending(id => analysis.RetainedBytes[id]).ToArray();
        if (alive.Length == 0) return;
        var bytes = alive.Sum(id => analysis.RetainedBytes[id]);
        insights.Add(new("closed-windows", "Leak", "High", alive.Length == 1 ? "A closed window is still in memory" : $"{alive.Length} closed windows are still in memory",
            "These WPF windows have been closed, but something still references them, so the window and its whole visual tree stay alive.",
            "Open the window's retention path. Common causes are static events, DispatcherTimer or CompositionTarget handlers, " +
            "services holding the view or view model, and bindings to sources that don't implement INotifyPropertyChanged.",
            alive.Length, bytes, alive.Take(30).Select(id => Item(graph, analysis, id)).ToArray()));
    }

    private static void DisposedButReachable(HeapGraph graph, HeapAnalysis analysis, HeapExtras extras, List<MemoryInsight> insights)
    {
        var alive = extras.Disposed.Where(id => analysis.Reachable[id] && !extras.ClosedWindows.Contains(id)).ToArray();
        if (alive.Length == 0) return;
        var byType = alive.GroupBy(id => graph.Objects[id].TypeId)
            .Where(g => !MemoryLabels.IsFrameworkModule(graph.Types[g.Key].Module))
            .Select(g => (Type: graph.Types[g.Key], Ids: g.OrderByDescending(id => analysis.RetainedBytes[id]).ToArray()))
            .OrderByDescending(g => g.Ids.Sum(id => analysis.RetainedBytes[id])).ToArray();
        if (byType.Length == 0) return;
        var count = byType.Sum(g => g.Ids.Length);
        var bytes = byType.Sum(g => g.Ids.Sum(id => analysis.RetainedBytes[id]));
        insights.Add(new("disposed-alive", "Leak", count >= 10 || bytes > 1 << 20 ? "High" : "Medium",
            count == 1 ? "A disposed object is still referenced" : $"{count:N0} disposed objects are still referenced",
            "Their disposed flag is set, so their owner is finished with them, yet a GC root still reaches them.",
            "Check who still holds each object after Dispose: caches, event subscriptions, and collections that are never cleared.",
            count, bytes, byType.Take(20).Select(g => new MemoryInsightItem(MemoryLabels.ShortType(g.Type.Name),
                $"{g.Ids.Length:N0} disposed · {g.Type.Module}", g.Ids.Length, g.Ids.Sum(id => analysis.RetainedBytes[id]), g.Type.Key, g.Ids[0])).ToArray()));
    }

    /// <summary>Objects whose immediate dominator is a delegate (or a delegate's invocation list): an event subscription
    /// is the only thing keeping them alive.</summary>
    private static void EventHandlerRetention(HeapGraph graph, HeapAnalysis analysis, List<MemoryInsight> insights)
    {
        var found = new List<int>();
        for (var id = 0; id < graph.Objects.Length; id++)
        {
            if (!analysis.Reachable[id]) continue;
            var type = graph.Types[graph.Objects[id].TypeId];
            if (type.IsDelegate || MemoryLabels.IsFrameworkModule(type.Module) || type.Name.Contains("<>c", StringComparison.Ordinal)) continue;
            var dominator = analysis.ImmediateDominators[id];
            if (dominator < 0) continue;
            var owner = graph.Types[graph.Objects[dominator].TypeId];
            if (owner.IsDelegate) { found.Add(id); continue; }
            // A multicast delegate keeps its targets through an object[] invocation list.
            if (owner.Name == "System.Object[]" && analysis.ImmediateDominators[dominator] is var listOwner && listOwner >= 0 &&
                graph.Types[graph.Objects[listOwner].TypeId].IsDelegate) found.Add(id);
        }
        if (found.Count == 0) return;
        var byType = found.GroupBy(id => graph.Objects[id].TypeId)
            .Select(g => (Type: graph.Types[g.Key], Ids: g.OrderByDescending(id => analysis.RetainedBytes[id]).ToArray()))
            .OrderByDescending(g => g.Ids.Sum(id => analysis.RetainedBytes[id])).ToArray();
        var bytes = found.Sum(id => analysis.RetainedBytes[id]);
        insights.Add(new("event-handlers", "Leak", bytes > 1 << 20 || found.Count >= 20 ? "High" : "Medium",
            found.Count == 1 ? "An object is kept alive only by an event handler" : $"{found.Count:N0} objects are kept alive only by event handlers",
            "A delegate is the only owner of these objects. That usually means they subscribed to an event that outlives them and never unsubscribed.",
            "Open an instance and follow the path to the event's publisher. Unsubscribe when the subscriber's lifetime ends, or use a weak event pattern.",
            found.Count, bytes, byType.Take(20).Select(g => new MemoryInsightItem(MemoryLabels.ShortType(g.Type.Name),
                $"{g.Ids.Length:N0} held by delegates · {g.Type.Module}", g.Ids.Length, g.Ids.Sum(id => analysis.RetainedBytes[id]), g.Type.Key, g.Ids[0])).ToArray()));
    }

    private static void FinalizerOnly(HeapGraph graph, HeapAnalysis analysis, List<MemoryInsight> insights, CancellationToken token)
    {
        if (!graph.Roots.Any(r => r.Kind == "FinalizerQueue")) return;
        var (withoutFinalizer, _) = analysis.FilteredWalk(["FinalizerQueue"], token);
        var only = new List<int>();
        for (var id = 0; id < graph.Objects.Length; id++)
            if (analysis.Reachable[id] && !withoutFinalizer[id]) only.Add(id);
        if (only.Count == 0) return;
        var bytes = only.Sum(id => graph.Objects[id].Size);
        var byType = only.GroupBy(id => graph.Objects[id].TypeId).Select(g => (Type: graph.Types[g.Key], Ids: g.ToArray()))
            .OrderByDescending(g => g.Ids.Sum(id => graph.Objects[id].Size)).ToArray();
        insights.Add(new("finalizer-only", "Runtime", bytes > 10 << 20 ? "Medium" : "Low",
            $"{only.Count:N0} objects wait only for finalization",
            "Nothing references these objects except the finalizer queue. They are released after their finalizers run, which costs an extra collection.",
            "Dispose finalizable objects deterministically and call GC.SuppressFinalize in Dispose. A large or growing queue can mean the finalizer thread is blocked.",
            only.Count, bytes, byType.Take(20).Select(g => new MemoryInsightItem(MemoryLabels.ShortType(g.Type.Name), g.Type.Module,
                g.Ids.Length, g.Ids.Sum(id => graph.Objects[id].Size), g.Type.Key, g.Ids[0])).ToArray()));
    }

    private static void DuplicateStrings(HeapGraph graph, HeapExtras extras, List<MemoryInsight> insights)
    {
        var duplicates = extras.Strings.Where(kv => kv.Value.Count > 1)
            .Select(kv => (Text: kv.Key, kv.Value.Count, Wasted: kv.Value.Bytes / kv.Value.Count * (kv.Value.Count - 1), kv.Value.Sample))
            .OrderByDescending(d => d.Wasted).ToArray();
        if (duplicates.Length == 0) return;
        var wasted = duplicates.Sum(d => d.Wasted);
        if (wasted < 4096) return;
        var stringKey = graph.Types.FirstOrDefault(t => t.Name == "System.String")?.Key;
        insights.Add(new("duplicate-strings", "Waste", wasted > 10 << 20 ? "Medium" : "Low",
            $"{MemorySizeText(wasted)} in duplicate strings",
            $"{duplicates.Length:N0} string values appear more than once. Only one copy of each is needed." +
                (extras.StringsTruncated ? " Very long strings and strings beyond the scan budget were not compared." : ""),
            "Intern or cache values that repeat (identifiers, file paths, enum-like text), or keep them as enums or numbers.",
            duplicates.Length, wasted, duplicates.Take(25).Select(d => new MemoryInsightItem(Quote(d.Text),
                $"{d.Count:N0} copies", d.Count, d.Wasted, stringKey, d.Sample)).ToArray()));
    }

    private static void SparseArrays(HeapGraph graph, HeapAnalysis analysis, HeapExtras extras, List<MemoryInsight> insights)
    {
        var sparse = new List<(int Id, int Length, int Used, long Wasted)>();
        foreach (var (id, length) in extras.ReferenceArrayLengths)
        {
            if (length < 32) continue;
            var used = graph.Outgoing.For(id).Length;
            if (used * 4 > length) continue;
            sparse.Add((id, length, used, (long)(length - used) * extras.PointerSize));
        }
        var wasted = sparse.Sum(s => s.Wasted);
        if (wasted < 16 * 1024) return;
        var byType = sparse.GroupBy(s => graph.Objects[s.Id].TypeId).Select(g => (Type: graph.Types[g.Key], Items: g.OrderByDescending(s => s.Wasted).ToArray()))
            .OrderByDescending(g => g.Items.Sum(s => s.Wasted)).ToArray();
        insights.Add(new("sparse-arrays", "Waste", wasted > 10 << 20 ? "Medium" : "Low",
            $"{MemorySizeText(wasted)} in mostly empty arrays",
            $"{sparse.Count:N0} reference arrays use 25% or less of their slots. Collections often keep their peak capacity after items are removed.",
            "Create collections with a realistic capacity, call TrimExcess after large removals, or replace long-lived oversized collections.",
            sparse.Count, wasted, byType.Take(20).Select(g => new MemoryInsightItem(MemoryLabels.ShortType(g.Type.Name),
                $"{g.Items.Length:N0} arrays · largest {g.Items[0].Used:N0} of {g.Items[0].Length:N0} slots used", g.Items.Length,
                g.Items.Sum(s => s.Wasted), g.Type.Key, g.Items[0].Id)).ToArray()));
    }

    private static void LargeObjects(HeapGraph graph, HeapAnalysis analysis, HeapExtras extras, List<MemoryInsight> insights)
    {
        var large = new List<int>();
        for (var id = 0; id < graph.Objects.Length; id++) if (graph.Objects[id].Generation == "Large") large.Add(id);
        var managed = graph.Objects.Sum(o => o.Size);
        var fragmented = extras.FreeBytes > 8 << 20 && extras.FreeBytes > managed / 4;
        if (large.Count == 0 && !fragmented) return;
        var bytes = large.Sum(id => graph.Objects[id].Size);
        var byType = large.GroupBy(id => graph.Objects[id].TypeId).Select(g => (Type: graph.Types[g.Key], Ids: g.OrderByDescending(id => graph.Objects[id].Size).ToArray()))
            .OrderByDescending(g => g.Ids.Sum(id => graph.Objects[id].Size)).ToArray();
        var summary = $"{large.Count:N0} objects of 85,000 bytes or more live on the large object heap ({MemorySizeText(bytes)}). " +
            $"Heap segments also hold {MemorySizeText(extras.FreeBytes)} of free space" +
            (extras.LargeFreeBytes > 0 ? $", {MemorySizeText(extras.LargeFreeBytes)} of it on the large object heap." : ".");
        insights.Add(new("large-objects", "Runtime", fragmented ? "Medium" : "Info",
            fragmented ? "The heap is fragmented" : "Large object heap usage", summary,
            "Large objects are collected only with generation 2 and are not compacted by default. Pool or reuse big buffers (ArrayPool<T>) instead of allocating them repeatedly.",
            large.Count, bytes, byType.Take(20).Select(g => new MemoryInsightItem(MemoryLabels.ShortType(g.Type.Name),
                $"{g.Ids.Length:N0} large objects · biggest {MemorySizeText(graph.Objects[g.Ids[0]].Size)}", g.Ids.Length,
                g.Ids.Sum(id => graph.Objects[id].Size), g.Type.Key, g.Ids[0])).ToArray()));
    }

    private static void Pinning(HeapGraph graph, HeapAnalysis analysis, List<MemoryInsight> insights)
    {
        var pinned = graph.Roots.Where(r => r.IsPinned && !r.IsPermanent).Select(r => r.ObjectId).Distinct()
            .Where(id => graph.Objects[id].Generation is not ("Pinned" or "Frozen")).ToArray();
        if (pinned.Length == 0) return;
        var bytes = pinned.Sum(id => graph.Objects[id].Size);
        var ephemeral = pinned.Count(id => graph.Objects[id].Generation is "Generation0" or "Generation1");
        var byType = pinned.GroupBy(id => graph.Objects[id].TypeId).Select(g => (Type: graph.Types[g.Key], Ids: g.ToArray()))
            .OrderByDescending(g => g.Ids.Length).ToArray();
        insights.Add(new("pinned", "Runtime", ephemeral > 50 ? "Low" : "Info",
            $"{pinned.Length:N0} objects are pinned",
            $"Pinned objects cannot be moved during compaction. {ephemeral:N0} of them are in generation 0 or 1, where pinning fragments the heap the most.",
            "Keep pins short (fixed blocks, GCHandle.Free promptly) and use the pinned object heap (GC.AllocateArray(..., pinned: true)) for long-lived buffers.",
            pinned.Length, bytes, byType.Take(20).Select(g => new MemoryInsightItem(MemoryLabels.ShortType(g.Type.Name), g.Type.Module,
                g.Ids.Length, g.Ids.Sum(id => graph.Objects[id].Size), g.Type.Key, g.Ids[0])).ToArray()));
    }

    private static MemoryInsightItem Item(HeapGraph graph, HeapAnalysis analysis, int id)
    {
        var obj = graph.Objects[id]; var type = graph.Types[obj.TypeId];
        return new(MemoryLabels.ShortType(type.Name), $"0x{obj.Address:X} · retains {MemorySizeText(analysis.RetainedBytes[id])}", 1,
            analysis.RetainedBytes[id], type.Key, id);
    }

    private static string Quote(string text)
    {
        var single = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return "\"" + (single.Length > 60 ? single[..60] + "…" : single) + "\"";
    }

    internal static string MemorySizeText(long bytes)
    {
        var size = Math.Abs((double)bytes);
        return size >= 1 << 30 ? $"{size / (1 << 30):N2} GiB" : size >= 1 << 20 ? $"{size / (1 << 20):N1} MiB" : size >= 1024 ? $"{size / 1024:N1} KiB" : $"{bytes:N0} B";
    }
}
