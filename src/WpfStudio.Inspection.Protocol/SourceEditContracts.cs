namespace WpfStudio.Inspection.Protocol;

/// <summary>Validates a proposed authored value; never sets a live dependency property.</summary>
public sealed record InspectionSourcePropertyRequest(long Revision, string NodeId, string PropertyId,
    string SourceEditToken, string? Value, bool IsNull = false, bool Remove = false, bool VerifyOnly = false);

public sealed record InspectionSourcePropertyIdentity(string PropertyName, string OwnerType, string OwnerAssembly,
    bool IsAttached, string? ContentProperty, string TargetType, string TargetAssembly,
    Guid TargetModuleVersionId, Guid OwnerModuleVersionId, IReadOnlyList<InspectionSourcePropertyTarget>? AuthoredTargets = null);

public sealed record InspectionSourcePropertyTarget(string Type, string Assembly, Guid ModuleVersionId,
    IReadOnlyList<string> XmlNamespaces, string? ContentProperty);

/// <summary>
/// The literal is invariant scalar text, not escaped XML. IsNull requests an explicit
/// XAML null. Remove and VerifyOnly return identity without converting a proposal.
/// </summary>
public sealed record InspectionSourcePropertyResult(bool Success, long Revision, string NodeId, string PropertyId,
    string SourceEditToken, InspectionSourcePropertyIdentity? Property = null, string? Literal = null,
    bool IsNull = false, string? Error = null);
