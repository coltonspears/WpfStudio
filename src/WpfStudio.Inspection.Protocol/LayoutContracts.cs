namespace WpfStudio.Inspection.Protocol;

public sealed record LayoutPoint(double X, double Y);
public sealed record LayoutSize(double Width, double Height);
public sealed record LayoutRect(double X, double Y, double Width, double Height);
public sealed record LayoutInsets(double Left, double Top, double Right, double Bottom);
public sealed record LayoutFact(string Name, string Value, string? Detail = null);
/// <summary>Finite polygon vertices in the capture coordinate root's device-independent pixels.</summary>
public sealed record LayoutOverlay(string Kind, IReadOnlyList<LayoutPoint> Points, string Label);
public sealed record LayoutSnapshot(bool Available, IReadOnlyList<LayoutFact> Facts,
    IReadOnlyList<LayoutOverlay> Overlays, IReadOnlyList<string> Notices, string? Status = null);
