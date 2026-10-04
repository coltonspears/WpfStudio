namespace WpfStudio.Contracts.Profiling;

public sealed record HeapCaptureRequest(string? DumpPath = null, int? ProcessId = null,
    long? ProcessStartTimeUtcTicks = null, int RuntimeIndex = 0, int MaxObjects = 1_000_000, int MaxReferences = 6_000_000,
    string? DacPath = null);

/// <summary>Per-type totals. <see cref="RetainedBytes"/> counts what all instances of the type keep alive together,
/// without double-counting instances that dominate other instances of the same type.</summary>
public sealed record MemoryTypeSummary(string Key, string Name, string Module, int Count, long Bytes,
    int ReachableCount, long LargestRetainedBytes, long RetainedBytes = 0, long ReachableBytes = 0);

public sealed record MemoryGenerationSummary(string Generation, int Count, long Bytes);
public sealed record MemoryRootKindSummary(string Kind, int Count, int Objects);

/// <summary>One row of an automatic inspection. Rows point at a type, an object, or both.</summary>
public sealed record MemoryInsightItem(string Label, string Detail, int Count, long Bytes, string? TypeKey = null, int? ObjectId = null);

/// <summary>An automatic inspection result. Category is Leak, Waste or Runtime; Severity is High, Medium, Low or Info.</summary>
public sealed record MemoryInsight(string Id, string Category, string Severity, string Title, string Summary, string Guidance,
    int Count, long Bytes, IReadOnlyList<MemoryInsightItem> Items);

public sealed record HeapSummary(string Source, string Runtime, string Architecture, DateTimeOffset CapturedAt,
    int ObjectCount, long ManagedBytes, long ReachableBytes, long UnreachableBytes, long FreeBytes,
    int ReferenceCount, int RootCount, bool IsComplete, IReadOnlyList<string> CoverageNotes,
    IReadOnlyList<MemoryTypeSummary> Types, IReadOnlyList<MemoryGenerationSummary>? Generations = null,
    IReadOnlyList<MemoryRootKindSummary>? RootKinds = null, IReadOnlyList<MemoryInsight>? Insights = null,
    IReadOnlyList<MemoryObjectInfo>? TopRetainers = null);

public sealed record MemoryObjectInfo(int Id, string Address, string TypeKey, string Type, string Module,
    long ShallowBytes, long RetainedBytes, bool IsReachable, string Generation, bool IsPinned, int RetainedCount = 0);

public sealed record MemoryObjectQuery(string? TypeKey = null, string Search = "", int Skip = 0, int Take = 200,
    bool ReachableOnly = false);
public sealed record MemoryObjectPage(int TotalCount, IReadOnlyList<MemoryObjectInfo> Objects);

// Reference IDs identify individual slots, so removing one of two references between the same pair
// of objects does not accidentally remove both. Roots share this same identity space.
public sealed record MemoryReferenceInfo(int Id, int? FromId, int ToId, string Owner, string Target,
    string Label, string Kind, bool IsRoot, bool IsPinned, bool IsPermanent = false);
public sealed record MemoryFieldInfo(string Name, string Type, string Value, int? ObjectId = null);
public sealed record MemoryRootPath(IReadOnlyList<MemoryReferenceInfo> References);
public sealed record MemoryObjectDetails(MemoryObjectInfo Object, string Preview,
    IReadOnlyList<MemoryFieldInfo> Fields, IReadOnlyList<MemoryReferenceInfo> Incoming,
    IReadOnlyList<MemoryReferenceInfo> Outgoing, IReadOnlyList<MemoryRootPath> RootPaths,
    IReadOnlyList<MemoryObjectInfo> Dominators, IReadOnlyList<string> Evidence,
    int IncomingCount, int OutgoingCount, bool FieldsTruncated, bool PathsTruncated);

/// <summary>A graph node. Incoming/outgoing counts are the object's full degree, so the UI can offer to expand
/// neighbours that are not displayed yet.</summary>
public sealed record MemoryGraphNode(MemoryObjectInfo Object, int Column, bool IsFocus, bool IsRootTarget,
    int IncomingCount = 0, int OutgoingCount = 0);
public sealed record MemoryGraph(IReadOnlyList<MemoryGraphNode> Nodes, IReadOnlyList<MemoryReferenceInfo> References,
    bool IsTruncated, string Description);
/// <summary>HiddenRootKinds removes root examples of those kinds (for example FinalizerQueue or Stack) so long-lived
/// owners such as static fields and handles stay visible.</summary>
public sealed record MemoryGraphRequest(int ObjectId, int MaxNodes = 120, int Depth = 2,
    IReadOnlyList<string>? HiddenRootKinds = null, int MaxOwners = 12, int MaxChildren = 16);
/// <summary>One hop of owners (Incoming) or referenced objects, largest retained first.</summary>
public sealed record MemoryNeighborRequest(int ObjectId, bool Incoming, int Take = 24);

public sealed record MemoryReleaseRequest(int ObjectId, int? ReferenceId = null);
public sealed record MemoryReleasedType(string Type, int Count, long Bytes, string? TypeKey = null);
public sealed record MemoryReleaseEstimate(long ReclaimableBytes, int ReclaimableObjects, bool IsComplete,
    bool SelectedObjectRemainsReachable, string Explanation, IReadOnlyList<MemoryReleasedType> Types,
    IReadOnlyList<int> ObjectSample, MemoryRootPath? RemainingRootPath);

/// <summary>Dominator-tree browsing. ParentId null lists objects dominated only by the GC roots. With GroupByType,
/// sibling instances of one type are folded into a single node; TypeKey lists the instances of one such group.</summary>
public sealed record MemoryDominatorQuery(int? ParentId = null, string? TypeKey = null, int Take = 100, bool GroupByType = true);
public sealed record MemoryDominatorNode(string TypeKey, string Type, int Count, long RetainedBytes, long ShallowBytes,
    int ChildCount, MemoryObjectInfo? Object, string? Via = null);
public sealed record MemoryDominatorPage(int? ParentId, string? TypeKey, int TotalCount, long TotalRetainedBytes,
    IReadOnlyList<MemoryDominatorNode> Nodes, int OtherCount, long OtherBytes);

/// <summary>Everything an object keeps alive (its dominator subtree), grouped by type.</summary>
public sealed record MemoryRetainedComposition(int ObjectId, long Bytes, int Count, IReadOnlyList<MemoryReleasedType> Types,
    bool IsTruncated);

/// <summary>Aggregated shortest retention paths (a Sankey). Level 0 is the investigated type or object; higher
/// levels are owners, ending in GC roots. Links point from owner to owned.</summary>
public sealed record MemoryFlowRequest(string? TypeKey = null, int? ObjectId = null, int MaxDepth = 6, int MaxNodesPerLevel = 7,
    int MaxInstances = 5_000, IReadOnlyList<string>? HiddenRootKinds = null);
/// <summary>Kind: Target, Owner, Root, Static, Other, Unrooted or Truncated.</summary>
public sealed record MemoryFlowNode(int Id, int Level, string Label, string Detail, string Kind, int Count, long Bytes,
    string? TypeKey = null, int? SampleObjectId = null);
public sealed record MemoryFlowLink(int FromId, int ToId, string Label, int Count, long Bytes);
public sealed record MemoryRetentionFlow(string Title, IReadOnlyList<MemoryFlowNode> Nodes, IReadOnlyList<MemoryFlowLink> Links,
    int Instances, int Sampled, int Unrooted, bool IsTruncated);

/// <summary>Object-browser children. Raw lists an object's declared fields even when it has a collection view.</summary>
public sealed record MemoryChildrenRequest(int ObjectId, int Skip = 0, int Take = 100, bool Raw = false);
/// <summary>Kind: Field, Element, Entry, Key, Value, Raw, Delegate or More.</summary>
public sealed record MemoryChildItem(string Name, string Type, string Value, string Kind, int? ObjectId = null,
    long RetainedBytes = 0, long ShallowBytes = 0, bool HasChildren = false, bool IsNull = false);
/// <summary>Shape: Object, Array, List, Dictionary, Set, String, Delegate or Value.</summary>
public sealed record MemoryObjectChildren(int ObjectId, string Shape, string Preview, int TotalCount, int Skip,
    IReadOnlyList<MemoryChildItem> Items);

public interface IMemoryProfilerRpc
{
    Task<string> GetArchitectureAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default);
    Task<HeapSummary> LoadAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default);
    Task<MemoryObjectPage> GetObjectsAsync(MemoryObjectQuery query, CancellationToken cancellationToken = default);
    Task<MemoryObjectDetails> InspectAsync(int objectId, CancellationToken cancellationToken = default);
    Task<MemoryGraph> GetGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken = default);
    Task<MemoryGraph> GetNeighborsAsync(MemoryNeighborRequest request, CancellationToken cancellationToken = default);
    Task<MemoryReleaseEstimate> EstimateReleaseAsync(MemoryReleaseRequest request, CancellationToken cancellationToken = default);
    Task<MemoryDominatorPage> GetDominatorsAsync(MemoryDominatorQuery query, CancellationToken cancellationToken = default);
    Task<MemoryRetainedComposition> GetRetainedAsync(int objectId, CancellationToken cancellationToken = default);
    Task<MemoryRetentionFlow> GetRetentionFlowAsync(MemoryFlowRequest request, CancellationToken cancellationToken = default);
    Task<MemoryObjectChildren> GetChildrenAsync(MemoryChildrenRequest request, CancellationToken cancellationToken = default);
}

public interface IMemorySession : IAsyncDisposable
{
    HeapSummary Summary { get; }
    Task<MemoryObjectPage> GetObjectsAsync(MemoryObjectQuery query, CancellationToken cancellationToken = default);
    Task<MemoryObjectDetails> InspectAsync(int objectId, CancellationToken cancellationToken = default);
    Task<MemoryGraph> GetGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken = default);
    Task<MemoryGraph> GetNeighborsAsync(MemoryNeighborRequest request, CancellationToken cancellationToken = default);
    Task<MemoryReleaseEstimate> EstimateReleaseAsync(MemoryReleaseRequest request, CancellationToken cancellationToken = default);
    Task<MemoryDominatorPage> GetDominatorsAsync(MemoryDominatorQuery query, CancellationToken cancellationToken = default);
    Task<MemoryRetainedComposition> GetRetainedAsync(int objectId, CancellationToken cancellationToken = default);
    Task<MemoryRetentionFlow> GetRetentionFlowAsync(MemoryFlowRequest request, CancellationToken cancellationToken = default);
    Task<MemoryObjectChildren> GetChildrenAsync(MemoryChildrenRequest request, CancellationToken cancellationToken = default);
}

public interface IMemoryProfiler
{
    Task<IMemorySession> OpenAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileProcess>> GetProcessesAsync(CancellationToken cancellationToken = default);
}

public sealed record ProfileProcess(int Id, string Name, long StartTimeUtcTicks, long WorkingSetBytes,
    string RuntimeHint);
