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
}

public sealed class MemorySizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long size ? MemorySize.Format(size) : "—";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed record MemoryTypeRow(MemoryTypeSummary Type, int? BaselineCount, long? BaselineBytes)
{
    public string Name => Type.Name;
    public string Module => Type.Module;
    public string Key => Type.Key;
    public string ShortName
    {
        get
        {
            if (Name.Length == 0) return Name;
            var generic = Name.IndexOf('<');
            return Name[(Name.LastIndexOf('.', generic >= 0 ? generic : Name.Length - 1) + 1)..];
        }
    }
    public string Metrics => $"{Count:N0} objects · {MemorySize.Format(Bytes)}" + (BaselineBytes.HasValue ? " · " + Growth : "");
    public int Count => Type.Count;
    public long Bytes => Type.Bytes;
    public long LargestRetainedBytes => Type.LargestRetainedBytes;
    public long? BytesDelta => BaselineBytes.HasValue ? Bytes - BaselineBytes.Value : null;
    public string Growth => BytesDelta is long delta ? (delta > 0 ? "+" : "") + MemorySize.Format(delta) : "—";
    public string CountGrowth => BaselineCount.HasValue ? (Count - BaselineCount.Value).ToString("+0;-0;0", CultureInfo.InvariantCulture) : "—";
}

public sealed record RootPathRow(string Label, IReadOnlyList<MemoryReferenceInfo> References)
{
    public static RootPathRow Create(MemoryRootPath path) => new(path.References[0].Owner, path.References);
}
