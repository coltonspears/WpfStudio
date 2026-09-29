namespace WpfStudio.Contracts;

public sealed record PreviewLayoutInsets(double Left, double Top, double Right, double Bottom);
public sealed record PreviewLayoutSibling(string NodeId, PreviewBounds Bounds);

/// <summary>One verified source-preview layout observation. Bounds are viewport DIPs; null dimensions mean Auto/unbounded and null Canvas anchors mean unset.</summary>
public sealed record PreviewLayoutEditContext(bool Available, long Version, string NodeId,
    string? Token = null, SourceLocation? Element = null, SourceLocation? Parent = null,
    string? ParentKind = null, PreviewBounds? Bounds = null, PreviewBounds? ParentBounds = null,
    PreviewBounds? SlotBounds = null, PreviewLayoutInsets? Margin = null,
    double? Width = null, double? Height = null, double MinWidth = 0, double MinHeight = 0,
    double? MaxWidth = null, double? MaxHeight = null,
    string? HorizontalAlignment = null, string? VerticalAlignment = null,
    double? CanvasLeft = null, double? CanvasTop = null, double? CanvasRight = null, double? CanvasBottom = null,
    int GridRow = 0, int GridColumn = 0, int GridRowSpan = 1, int GridColumnSpan = 1,
    IReadOnlyList<PreviewProperty>? EditProperties = null, IReadOnlyList<PreviewLayoutSibling>? Siblings = null,
    string? Status = null, PreviewSurfaceIdentity? Surface = null, string? SourceHash = null,
    bool SiblingsTruncated = false);

/// <summary>Proposed literal assignments; null fields leave the corresponding source property unchanged. Validation never mutates the preview.</summary>
public sealed record PreviewLayoutEditValues(double? Width = null, double? Height = null,
    PreviewLayoutInsets? Margin = null, string? HorizontalAlignment = null, string? VerticalAlignment = null,
    double? CanvasLeft = null, double? CanvasTop = null, double? CanvasRight = null, double? CanvasBottom = null,
    int? GridRow = null, int? GridColumn = null, int? GridRowSpan = null, int? GridColumnSpan = null);

public sealed record PreviewLayoutValidationRequest(long Version, string NodeId, string Token,
    PreviewSurfaceIdentity? Surface = null, PreviewLayoutEditValues? Values = null);
public sealed record PreviewLayoutValidationResult(PreviewLayoutValidationRequest Request, bool Success,
    string? Error = null);
