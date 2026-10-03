namespace WpfStudio.Contracts.Profiling;

public sealed record HeapCaptureRequest(string? DumpPath = null, int? ProcessId = null,
    long? ProcessStartTimeUtcTicks = null, int RuntimeIndex = 0, int MaxObjects = 1_000_000, int MaxReferences = 6_000_000,
    string? DacPath = null);

public sealed record MemoryTypeSummary(string Key, string Name, string Module, int Count, long Bytes,
    int ReachableCount, long LargestRetainedBytes);

public sealed record HeapSummary(string Source, string Runtime, string Architecture, DateTimeOffset CapturedAt,
    int ObjectCount, long ManagedBytes, long ReachableBytes, long UnreachableBytes, long FreeBytes,
    int ReferenceCount, int RootCount, bool IsComplete, IReadOnlyList<string> CoverageNotes,
    IReadOnlyList<MemoryTypeSummary> Types);

public sealed record MemoryObjectInfo(int Id, string Address, string TypeKey, string Type, string Module,
    long ShallowBytes, long RetainedBytes, bool IsReachable, string Generation, bool IsPinned);

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

public sealed record MemoryGraphNode(MemoryObjectInfo Object, int Column, bool IsFocus, bool IsRootTarget);
public sealed record MemoryGraph(IReadOnlyList<MemoryGraphNode> Nodes, IReadOnlyList<MemoryReferenceInfo> References,
    bool IsTruncated, string Description);
public sealed record MemoryGraphRequest(int ObjectId, int MaxNodes = 120, int Depth = 2);

public sealed record MemoryReleaseRequest(int ObjectId, int? ReferenceId = null);
public sealed record MemoryReleasedType(string Type, int Count, long Bytes);
public sealed record MemoryReleaseEstimate(long ReclaimableBytes, int ReclaimableObjects, bool IsComplete,
    bool SelectedObjectRemainsReachable, string Explanation, IReadOnlyList<MemoryReleasedType> Types,
    IReadOnlyList<int> ObjectSample, MemoryRootPath? RemainingRootPath);

public interface IMemoryProfilerRpc
{
    Task<string> GetArchitectureAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default);
    Task<HeapSummary> LoadAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default);
    Task<MemoryObjectPage> GetObjectsAsync(MemoryObjectQuery query, CancellationToken cancellationToken = default);
    Task<MemoryObjectDetails> InspectAsync(int objectId, CancellationToken cancellationToken = default);
    Task<MemoryGraph> GetGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken = default);
    Task<MemoryReleaseEstimate> EstimateReleaseAsync(MemoryReleaseRequest request, CancellationToken cancellationToken = default);
}

public interface IMemorySession : IAsyncDisposable
{
    HeapSummary Summary { get; }
    Task<MemoryObjectPage> GetObjectsAsync(MemoryObjectQuery query, CancellationToken cancellationToken = default);
    Task<MemoryObjectDetails> InspectAsync(int objectId, CancellationToken cancellationToken = default);
    Task<MemoryGraph> GetGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken = default);
    Task<MemoryReleaseEstimate> EstimateReleaseAsync(MemoryReleaseRequest request, CancellationToken cancellationToken = default);
}

public interface IMemoryProfiler
{
    Task<IMemorySession> OpenAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileProcess>> GetProcessesAsync(CancellationToken cancellationToken = default);
}

public sealed record ProfileProcess(int Id, string Name, long StartTimeUtcTicks, long WorkingSetBytes,
    string RuntimeHint);
