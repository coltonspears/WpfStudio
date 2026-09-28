namespace WpfStudio.Inspection.Protocol;

public sealed record InspectionPickRequest(bool Enabled);
public sealed record InspectionPickState(bool IsActive, long Sequence, string? NodeId = null, string? Status = null);
public sealed record InspectionHighlightRequest(long Revision, string? NodeId, bool ShowLayout = false);
public sealed record InspectionHighlightResult(bool Applied, string? Status = null);
