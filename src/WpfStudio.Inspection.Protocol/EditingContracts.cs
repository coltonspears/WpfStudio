namespace WpfStudio.Inspection.Protocol;

/// <summary>The opaque property identity and observed-state token come from an actual inspection.</summary>
public sealed record InspectionPropertyEdit(string OperationId, long Revision, string NodeId,
    string PropertyId, string EditToken, string? Value, bool IsNull = false, bool Reset = false);
public sealed record InspectionPropertyValidation(bool Success, string? Error = null);
/// <summary>Unknown never establishes that an edit did or did not run. Query its operation ID.</summary>
public sealed record InspectionPropertyEditResult(string OperationId, string Outcome,
    InspectionElement? Element = null, string? Error = null);
public sealed record InspectionEditStatusRequest(string OperationId);
