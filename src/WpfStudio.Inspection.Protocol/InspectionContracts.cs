using System.Text.Json;

namespace WpfStudio.Inspection.Protocol;

public static class InspectionProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public const string PipeVariable = "WPFSTUDIO_INSPECTION_PIPE";
    public const string TokenVariable = "WPFSTUDIO_INSPECTION_TOKEN";
    public const string OwnerVariable = "WPFSTUDIO_INSPECTION_OWNER_PID";
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

public sealed record InspectionMessage(string Kind, long Id, JsonElement Payload, string? Error = null)
{
    public static InspectionMessage Create<T>(string kind, long id, T payload, string? error = null) =>
        new(kind, id, JsonSerializer.SerializeToElement(payload, InspectionProtocol.JsonOptions), error);
    public T GetPayload<T>() => Payload.Deserialize<T>(InspectionProtocol.JsonOptions)
        ?? throw new InvalidDataException("The inspection message has no payload.");
}

public sealed record InspectionHello(int ProtocolVersion, string Token, int ProcessId, string RuntimeVersion,
    string AgentVersion, IReadOnlyList<string> Capabilities);
public sealed record InspectionWelcome(int ProtocolVersion, string SessionId);
public sealed record InspectionTreeRequest(int MaximumNodes = 3000);
public sealed record InspectionNodeRequest(long Revision, string NodeId);
public sealed record InspectionBounds(double X, double Y, double Width, double Height);
// A BAML/source location is only a hint. It is not a verified current-buffer edit target.
public sealed record InspectionSourceHint(string Uri, int Line, int Column);
public sealed record InspectionNode(string Id, string? ParentId, string? LogicalParentId, int DispatcherId,
    string Type, string? Name, InspectionBounds? Bounds, bool IsVisual, string? WindowTitle = null,
    InspectionSourceHint? Source = null);
public sealed record InspectionTrace(long Id, DateTimeOffset Timestamp, string Message, int Count);
public sealed record InspectionTree(long Revision, IReadOnlyList<InspectionNode> Nodes,
    IReadOnlyList<InspectionTrace> Traces, bool Truncated = false, string? Status = null,
    IReadOnlyList<InspectionBindingObservation>? BindingObservations = null,
    IReadOnlyList<string>? ScannedBindingNodes = null, bool BindingScanTruncated = false,
    InspectionPickState? Pick = null);
public sealed record InspectionBinding(string? Path, string Status, string Category, string Explanation,
    string? SourceType = null, string? ResolvedSourceType = null, string? ResolvedProperty = null,
    bool HasValidationError = false, InspectionBindingDetails? Details = null, BindingSourcesSnapshot? Sources = null);
public sealed record InspectionProperty(string Name, string OwnerType, string OwnerAssembly, string Type,
    string Value, string ValueSource, bool IsExpression, bool IsAnimated, bool IsCoerced,
    InspectionBinding? Binding = null, string? PropertyId = null, string? EditToken = null,
    bool CanEdit = false, string? EditableValue = null, bool IsNull = false,
    bool IsOverridden = false, string? EditUnavailableReason = null,
    string? SourceEditToken = null, bool CanWriteSource = false, string? SourceUnavailableReason = null);
public sealed record InspectionElement(long Revision, string NodeId, IReadOnlyList<InspectionProperty> Properties,
    string? DataContextType, bool Available, string? Status = null, LayoutSnapshot? Layout = null);
public sealed record InspectionBindingObservation(string BindingId, string NodeId, string TargetProperty,
    string OwnerType, string OwnerAssembly, InspectionBinding Binding);
