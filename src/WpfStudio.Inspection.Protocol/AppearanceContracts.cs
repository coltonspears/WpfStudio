namespace WpfStudio.Inspection.Protocol;

/// <summary>A property observation request. Live sessions require their opaque observed PropertyId.</summary>
public sealed record AppearanceRequest(long Revision, string NodeId, string Property,
    string? OwnerType = null, string? OwnerAssembly = null, string? PropertyId = null);
public sealed record AppearanceResponse(AppearanceRequest Request, AppearanceSnapshot Snapshot);
public sealed record AppearanceFact(string Name, string Value, string? Explanation = null);
/// <summary>A declaration is not proof that its setter or trigger currently supplies the property.</summary>
public sealed record AppearanceDeclaration(string Id, string Kind, string Description,
    string? Property = null, string? TargetName = null, InspectionSourceHint? Source = null);
/// <summary>Available dictionaries and keys, not a reconstruction of WPF's complete lookup order.</summary>
public sealed record AppearanceResourceScope(string Id, string? ParentId, string Label, string? SourceUri,
    IReadOnlyList<string> Keys, bool KeysTruncated = false);
/// <summary>Historical WPF resolution; it does not establish the property's current resource origin.</summary>
public sealed record AppearanceResourceEvidence(long Id, DateTimeOffset Timestamp, string Target,
    string Property, string Key, string? DictionaryUri, InspectionSourceHint? Source = null);
public sealed record AppearanceSnapshot(bool Available, IReadOnlyList<AppearanceFact> Facts,
    IReadOnlyList<AppearanceDeclaration> Declarations, IReadOnlyList<AppearanceResourceScope> ResourceScopes,
    IReadOnlyList<AppearanceResourceEvidence> ResourceEvents, IReadOnlyList<string> Notices,
    bool Truncated = false, string? Status = null)
{
    public static AppearanceSnapshot Unavailable(string status) => new(false, [], [], [], [], [], Status: status);
}
