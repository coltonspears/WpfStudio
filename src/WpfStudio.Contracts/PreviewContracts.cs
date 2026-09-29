namespace WpfStudio.Contracts;

public enum PreviewMode { Source, Compiled }

/// <summary>Explicit public static parameterless entry point in the selected project assembly.</summary>
public sealed record PreviewFactory(string TypeName, string MethodName);
public sealed record PreviewScenario(string Name, PreviewFactory? ViewFactory = null, PreviewFactory? DataContextFactory = null);
public sealed record PreviewScenarioProvenance(PreviewScenario Configuration, string AssemblyPath,
    string AssemblySha256, string ModuleVersionId, string ViewTypeName);

/// <summary>Compiled mode explicitly activates the built view, never the project application startup path.</summary>
public sealed record PreviewRequest(string Path, string Text, long Version, double Width = 960, double Height = 640,
    string? AssemblyPath = null, string? ProjectDirectory = null, PreviewMode Mode = PreviewMode.Source,
    string? ViewTypeName = null, string? ApplicationResourcePath = "App.xaml",
    PreviewScenario? Scenario = null, bool UseDesignTimeValues = true);
public sealed record PreviewBuildProvenance(string AssemblyPath, string AssemblyName, string AssemblySha256,
    string ModuleVersionId, string ViewTypeName, string? ApplicationResourcePath);
public sealed record PreviewBounds(double X, double Y, double Width, double Height);
public sealed record PreviewDiagnostic(string Message, string Severity = "Error", int? Line = null, int? Column = null,
    string? NodeId = null, string? Property = null, string? BindingId = null);
public sealed record PreviewNode(string Id, string? ParentId, string? LogicalParentId, string Type, string? Name,
    PreviewBounds? Bounds, SourceLocation? Source, bool IsVisual);
public sealed record PreviewSnapshot(long Version, bool Success, byte[]? PngBytes, int PixelWidth, int PixelHeight,
    IReadOnlyList<PreviewNode> Nodes, IReadOnlyList<PreviewDiagnostic> Diagnostics, string? Status = null,
    PreviewBuildProvenance? Build = null, PreviewScenarioProvenance? Scenario = null, PreviewSurfaceIdentity? Surface = null);
/// <summary>Observes the existing preview instance without reloading source, recreating the view, or applying scenario data.</summary>
public sealed record PreviewCaptureRequest(long Version);
public sealed record PreviewNodeRequest(long Version, string NodeId);
public sealed record PreviewPickRequest(long Version, double X, double Y);
public sealed record PreviewProperty(string Name, string Type, string Value, string ValueSource, bool IsExpression,
    bool IsAnimated, bool IsCoerced, bool CanEdit, string? BindingPath = null, string? BindingStatus = null,
    string? DataContextType = null, bool IsOverridden = false, string? OwnerType = null,
    string? OwnerAssembly = null, bool IsAttached = false, string? EditableValue = null, bool CanWriteSource = false,
    string? ContentProperty = null, WpfStudio.Inspection.Protocol.BindingSourcesSnapshot? BindingSources = null,
    WpfStudio.Inspection.Protocol.InspectionBinding? Binding = null);
public sealed record PreviewInspection(long Version, PreviewNode? Node, IReadOnlyList<PreviewProperty> Properties,
    IReadOnlyList<PreviewDiagnostic> Diagnostics, string? Status = null,
    WpfStudio.Inspection.Protocol.LayoutSnapshot? Layout = null, PreviewLayoutEditContext? LayoutEditing = null);
/// <summary>Edits affect only the preview. Reset restores the original local value or binding.</summary>
public sealed record PreviewPropertyEdit(long Version, string NodeId, string Property, string? Value, bool Reset = false,
    string? OwnerType = null, string? OwnerAssembly = null);
public sealed record PreviewEditResult(bool Success, PreviewSnapshot Snapshot, PreviewInspection Inspection, string? Error = null);
public sealed record PreviewPropertyValidation(bool Success, string? Error = null);

public interface IPreviewRpc
{
    Task<PreviewHostSession> GetSessionAsync(CancellationToken cancellationToken) =>
        Task.FromException<PreviewHostSession>(new NotSupportedException("This host does not support native preview sessions."));
    Task<PreviewSurfaceResponse> UpdateSurfaceAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new PreviewSurfaceResponse(request, false, "This host does not support native interaction."));
    Task<PreviewSurfaceHeartbeat> HeartbeatSurfaceAsync(PreviewSurfaceHeartbeatRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new PreviewSurfaceHeartbeat(request.Surface, false, Status: "This host does not support native interaction."));
    Task<PreviewSnapshot> RenderAsync(PreviewRequest request, CancellationToken cancellationToken);
    Task<PreviewSnapshot> CaptureAsync(PreviewCaptureRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new PreviewSnapshot(request.Version, false, null, 0, 0, [], [], "This preview host does not support updating snapshots."));
    Task<PreviewInspection> InspectAsync(PreviewNodeRequest request, CancellationToken cancellationToken);
    Task<PreviewInspection> PickAsync(PreviewPickRequest request, CancellationToken cancellationToken);
    Task<PreviewEditResult> SetPropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken);
    Task<PreviewPropertyValidation> ValidatePropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken);
    Task<PreviewLayoutValidationResult> ValidateLayoutEditAsync(PreviewLayoutValidationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new PreviewLayoutValidationResult(request, false, "This preview host does not support layout source editing."));
    Task<WpfStudio.Inspection.Protocol.AppearanceResponse> GetAppearanceAsync(
        WpfStudio.Inspection.Protocol.AppearanceRequest request, CancellationToken cancellationToken);
    Task<WpfStudio.Inspection.Protocol.BindingSourceResponse> GetBindingSourceAsync(
        WpfStudio.Inspection.Protocol.BindingSourceRequest request, CancellationToken cancellationToken);
}
