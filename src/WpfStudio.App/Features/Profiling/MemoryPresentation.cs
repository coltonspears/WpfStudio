using System.Collections;
using System.Globalization;
using System.Windows.Data;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

public static class MemorySize
{
    public static string Format(long bytes)
    {
        var size = Math.Abs((double)bytes); var sign = bytes < 0 ? "−" : "";
        return size >= 1024 * 1024 * 1024 ? $"{sign}{size / (1024 * 1024 * 1024):N2} GiB"
            : size >= 1024 * 1024 ? $"{sign}{size / (1024 * 1024):N2} MiB"
            : size >= 1024 ? $"{sign}{size / 1024:N1} KiB" : $"{bytes:N0} B";
    }

    public static string Signed(long bytes) => bytes > 0 ? "+" + Format(bytes) : Format(bytes);

    public static string Generation(string generation) => generation switch
    {
        "Generation0" => "Gen 0", "Generation1" => "Gen 1", "Generation2" => "Gen 2", "Large" => "Large object heap",
        "Pinned" => "Pinned object heap", "Frozen" => "Frozen", _ => generation
    };

    public static string GenerationShort(string generation) => generation switch
    {
        "Generation0" => "Gen0", "Generation1" => "Gen1", "Generation2" => "Gen2", "Large" => "LOH", "Pinned" => "POH", _ => generation
    };
}

public sealed class MemorySizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long size ? MemorySize.Format(size) : value is int count ? MemorySize.Format(count) : "—";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Sums the bytes of the type rows in a CollectionView group, for group headers.</summary>
public sealed class GroupBytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IEnumerable items) return "";
        long bytes = 0; var count = 0; var types = 0;
        foreach (var item in items) if (item is MemoryTypeRow row) { bytes += row.Bytes; count += row.Count; types++; }
        return $"{MemorySize.Format(bytes)} · {count:N0} objects · {types:N0} types";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed record MemoryTypeRow(MemoryTypeSummary Type, int? BaselineCount, long? BaselineBytes, double MaxBytes = 0, double MaxRetained = 0,
    double MaxDelta = 0, string Group = "")
{
    public string Name => Type.Name;
    public string Module => Type.Module;
    public string Key => Type.Key;
    public string ShortName => MemoryLabels.ShortType(Name);
    public string Namespace => MemoryLabels.Namespace(Name);
    public string Metrics => $"{Count:N0} objects · {MemorySize.Format(Bytes)}" + (BaselineBytes.HasValue ? " · " + Growth : "");
    public int Count => Type.Count;
    public long Bytes => Type.Bytes;
    public long RetainedBytes => Type.RetainedBytes;
    public long LargestRetainedBytes => Type.LargestRetainedBytes;
    public int UnreachableCount => Type.Count - Type.ReachableCount;
    public string CountText => Type.Count.ToString("N0", CultureInfo.CurrentCulture);
    public string BytesText => MemorySize.Format(Bytes);
    public string RetainedText => RetainedBytes > 0 ? MemorySize.Format(RetainedBytes) : "—";
    public double BytesRatio => MaxBytes <= 0 ? 0 : Bytes / MaxBytes;
    public double RetainedRatio => MaxRetained <= 0 ? 0 : RetainedBytes / MaxRetained;
    public bool HasBaseline => BaselineBytes.HasValue;
    public long? BytesDelta => BaselineBytes.HasValue ? Bytes - BaselineBytes.Value : null;
    public int? CountDelta => BaselineCount.HasValue ? Count - BaselineCount.Value : null;
    public double DeltaValue => BytesDelta ?? 0;
    public bool IsGrowing => BytesDelta > 0;
    public string Growth => BytesDelta is long delta ? (delta > 0 ? "+" : "") + MemorySize.Format(delta) : "—";
    public string CountGrowth => BaselineCount.HasValue ? (Count - BaselineCount.Value).ToString("+0;-0;0", CultureInfo.InvariantCulture) : "—";
    public string ToolTipText => $"{Name}\n{Module}\n{Count:N0} objects ({Type.ReachableCount:N0} reachable) · own {MemorySize.Format(Bytes)} · retains {RetainedText}" +
        (HasBaseline ? $"\nSince baseline: {CountGrowth} objects, {Growth}" : "");
}

public sealed record PathStep(string Via, string Target, string TargetShort, int TargetId, MemoryReferenceInfo Reference, bool IsTarget);

/// <summary>One GC-root example, shown as a vertical chain: the root, then each owner and the field that holds the next.
/// Static fields are shown as the root itself; the runtime array that stores statics is left out.</summary>
public sealed record RootPathRow(string Label, IReadOnlyList<MemoryReferenceInfo> References)
{
    private int StaticIndex => References.ToList().FindIndex(r => r.Label.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal));
    public bool IsStatic => StaticIndex >= 0;
    public string Title => IsStatic ? MemoryLabels.ShortStatic(References[StaticIndex].Label) : MemoryLabels.RootDisplay(References[0].Kind, References[0].Label);
    public string FullTitle => IsStatic ? References[StaticIndex].Label : References[0].Label;
    public string KindText => IsStatic ? "Static field" : MemoryLabels.RootKindName(References[0].Kind);
    public bool IsLongLived => IsStatic || MemoryLabels.RootPriority(References[0].Kind, References[0].Label) <= 1;
    public string Summary => Steps.Count <= 1 ? "Directly rooted" : $"{Steps.Count - 1} hop{(Steps.Count == 2 ? "" : "s")}";
    public IReadOnlyList<PathStep> Steps { get; } = BuildSteps(References);
    public static RootPathRow Create(MemoryRootPath path) => new(path.References[0].Owner, path.References);

    private static IReadOnlyList<PathStep> BuildSteps(IReadOnlyList<MemoryReferenceInfo> references)
    {
        var start = Math.Max(0, references.ToList().FindIndex(r => r.Label.StartsWith(MemoryLabels.StaticPrefix, StringComparison.Ordinal)));
        return references.Skip(start).Select((r, i) => new PathStep(i == 0 ? "" : r.Label, r.Target, ShortTarget(r.Target), r.ToId, r,
            start + i == references.Count - 1)).ToArray();
    }

    private static string ShortTarget(string target)
    {
        var at = target.LastIndexOf(" @ ", StringComparison.Ordinal);
        return at < 0 ? MemoryLabels.ShortType(target) : MemoryLabels.ShortType(target[..at]) + target[at..];
    }
}

public sealed record KpiTile(string Label, string Value, string Detail, string Delta, string Tone, string ToolTip)
{
    public bool HasDelta => Delta.Length > 0;
}

public sealed record FindingItemRow(MemoryInsightItem Item)
{
    public string Label => Item.Label;
    public string Detail => Item.Detail;
    public string BytesText => MemorySize.Format(Item.Bytes);
    public bool CanOpen => Item.ObjectId is not null || Item.TypeKey is not null;
}

public sealed record FindingRow(MemoryInsight Insight)
{
    public string Title => Insight.Title;
    public string Summary => Insight.Summary;
    public string Guidance => Insight.Guidance;
    public string Severity => Insight.Severity;
    public string Category => Insight.Category;
    public string CategoryText => Insight.Category switch { "Leak" => Insight.Severity == "High" ? "Likely leak" : "Possible leak", "Waste" => "Wasted memory", _ => "Runtime" };
    public string Icon => Insight.Category switch { "Leak" => "Leak", "Waste" => "Layers", _ => "Info" };
    public string BytesText => Insight.Bytes > 0 ? MemorySize.Format(Insight.Bytes) : "";
    public IReadOnlyList<FindingItemRow> Items { get; } = Insight.Items.Select(i => new FindingItemRow(i)).ToArray();
}

public sealed record RetainerRow(MemoryObjectInfo Object, double Ratio)
{
    public string Title => MemoryLabels.ShortType(Object.Type);
    public string Namespace => MemoryLabels.Namespace(Object.Type);
    public string RetainedText => MemorySize.Format(Object.RetainedBytes);
    public string Detail => $"{Object.Address} · own {MemorySize.Format(Object.ShallowBytes)} · {Object.RetainedCount:N0} objects";
}

public sealed record GrowthRow(MemoryTypeRow Type, double Ratio)
{
    public string Title => Type.ShortName;
    public string DeltaText => Type.Growth;
    public string CountText => Type.CountGrowth + " objects";
}

public sealed record RetainedTypeRow(MemoryReleasedType Type, double Ratio)
{
    public string Title => MemoryLabels.ShortType(Type.Type);
    public string BytesText => MemorySize.Format(Type.Bytes);
    public string CountText => Type.Count == 1 ? "1 object" : $"{Type.Count:N0} objects";
}

public sealed record TrailItem(int Id, string Label, bool IsCurrent);

/// <summary>Maps a categorical slot index to its theme brush (Chart1Brush…Chart8Brush); negative is neutral.</summary>
public sealed class SeriesBrushConverter : IValueConverter
{
    public static SeriesBrushConverter Instance { get; } = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Application.Current?.TryFindResource(value is int index && index >= 0 ? $"Chart{index % 8 + 1}Brush" : "SubtleBrush");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
