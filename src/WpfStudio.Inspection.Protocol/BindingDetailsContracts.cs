namespace WpfStudio.Inspection.Protocol;

public sealed record InspectionBindingDetails(string SourceKind, string Mode, string? ConverterType,
    string SourceDescription, IReadOnlyList<InspectionBindingValidation> ValidationErrors,
    IReadOnlyList<InspectionBindingEvidence> Evidence, bool EvidenceTruncated = false,
    bool ValidationTruncated = false, InspectionBindingPathState? PathState = null);

/// <summary>WPF's cached path state, not a fresh evaluation of application properties. Levels are zero-based.</summary>
public sealed record InspectionBindingPathState(bool Available, string Status,
    IReadOnlyList<InspectionBindingPathSegment> Segments, int? FirstUnresolvedLevel = null,
    bool Truncated = false, string? UnavailableReason = null, string? TargetType = null,
    bool? UsesFallbackValue = null);

/// <summary>Only metadata already retained by WPF. An unresolved owner may have been erased by WPF.</summary>
public sealed record InspectionBindingPathSegment(int Level, string Kind, string? Name, string State,
    string? OwnerType = null, string? AccessorKind = null, string? ValueType = null,
    bool HasCollectionView = false);

public sealed record InspectionBindingValidation(string Kind, string RuleType, string ValidationStep,
    string? Message = null, string? ExceptionType = null);

/// <summary>
/// A historical WPF notification, not proof of the binding's current failure cause.
/// SourceMatches compares only the root source identity; nested values may have changed.
/// </summary>
public sealed record InspectionBindingEvidence(long Id, DateTimeOffset ObservedAt, string Kind, int Code,
    string Message, string? Member, string? OwnerType, string? SourceType, string ObservedStatus,
    bool SourceMatches, string? BindingPath = null);
