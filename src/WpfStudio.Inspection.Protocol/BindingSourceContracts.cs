namespace WpfStudio.Inspection.Protocol;

/// <summary>A live expression and its actual declaration object; a source position is an unverified loader hint.</summary>
public sealed record BindingSourceDeclaration(string ExpressionId, string DeclarationId,
    string? ParentExpressionId, int? ChildIndex, string Kind, string? Path, string? XPath,
    string Status, InspectionSourceHint? Source, string? UnavailableReason = null, InspectionBinding? Observation = null);

public sealed record BindingSourcesSnapshot(string BindingId,
    IReadOnlyList<BindingSourceDeclaration> Declarations, bool Truncated = false);

/// <summary>Live inspection requires the observed opaque PropertyId; preview uses canonical property metadata.</summary>
public sealed record BindingSourceRequest(long Revision, string NodeId, string Property,
    string BindingId, string ExpressionId, string DeclarationId,
    string? OwnerType = null, string? OwnerAssembly = null, string? PropertyId = null);

public sealed record BindingSourceResponse(BindingSourceRequest Request, bool Available,
    BindingSourceDeclaration? Declaration = null, string? Status = null);
