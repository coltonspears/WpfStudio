namespace WpfStudio.Contracts;

public enum PreviewMode { Source, Compiled }

/// <summary>Explicit public static parameterless entry point in the selected project assembly.</summary>
public sealed record PreviewFactory(string TypeName, string MethodName);
public sealed record PreviewScenario(string Name, PreviewFactory? ViewFactory = null, PreviewFactory? DataContextFactory = null);
public sealed record PreviewScenarioProvenance(PreviewScenario Configuration, string AssemblyPath,
    string AssemblySha256, string ModuleVersionId, string ViewTypeName);

/// <summary>
/// Compiled mode explicitly activates the built view, never the project application startup path.
/// <paramref name="SizeToRoot"/> sizes the artboard from the root element's explicit Width/Height
/// (including applied d:DesignWidth/d:DesignHeight); <paramref name="Width"/> and
/// <paramref name="Height"/> remain the fallback for a dimension the root leaves automatic.
/// <paramref name="InferDesignData"/> lets a source preview whose root declares no DataContext fill one with sample
/// data from the view model that matches the view by name. It applies only with <paramref name="UseDesignTimeValues"/>.
/// </summary>
public sealed record PreviewRequest(string Path, string Text, long Version, double Width = 960, double Height = 640,
    string? AssemblyPath = null, string? ProjectDirectory = null, PreviewMode Mode = PreviewMode.Source,
    string? ViewTypeName = null, string? ApplicationResourcePath = "App.xaml",
    PreviewScenario? Scenario = null, bool UseDesignTimeValues = true, bool SizeToRoot = false,
    string? ProjectAssetsPath = null, PreviewReferenceSet? References = null, bool InferDesignData = true);
/// <summary>
/// A non-package assembly the project references, as MSBuild resolved it: a HintPath or file reference, a project
/// reference's output, or a dependency or satellite found beside one. <see cref="RelativePath"/> is its place in
/// the output folder, for example <c>Vendor.Controls.dll</c> or <c>fr/Vendor.Controls.resources.dll</c>.
/// </summary>
public sealed record PreviewReference(string Path, string RelativePath);
/// <summary>
/// The references MSBuild resolved for the previewed project and its project references, and the folders their
/// HintPaths point to, which the preview also searches for assemblies loaded later.
/// </summary>
public sealed record PreviewReferenceSet(IReadOnlyList<PreviewReference> Assemblies, IReadOnlyList<string> SearchDirectories, IReadOnlyList<string> Warnings);
public sealed record PreviewBuildProvenance(string AssemblyPath, string AssemblyName, string AssemblySha256,
    string ModuleVersionId, string ViewTypeName, string? ApplicationResourcePath);
public sealed record PreviewBounds(double X, double Y, double Width, double Height);
public sealed record PreviewDiagnostic(string Message, string Severity = "Error", int? Line = null, int? Column = null,
    string? NodeId = null, string? Property = null, string? BindingId = null);
public sealed record PreviewNode(string Id, string? ParentId, string? LogicalParentId, string Type, string? Name,
    PreviewBounds? Bounds, SourceLocation? Source, bool IsVisual);
/// <summary>
/// Where one piece of a preview's design-time data came from. <paramref name="Property"/> is the property it was
/// declared for, such as DataContext or ItemsSource, and <paramref name="TypeName"/> the declared or matched type.
/// <paramref name="AssemblyName"/> accompanies a type matched by name, so that the match can be written into the view.
/// </summary>
public sealed record PreviewDesignData(string Property, string Kind, string? TypeName, string Summary, int? Line = null, int? Column = null,
    string? AssemblyName = null);
/// <summary>The values of <see cref="PreviewDesignData.Kind"/>.</summary>
public static class PreviewDesignDataKinds
{
    /// <summary>An instance the type's own parameterless constructor built (IsDesignTimeCreatable).</summary>
    public const string Created = "Created";
    /// <summary>Sample values generated from the declared type without running its constructor.</summary>
    public const string Sample = "Sample";
    /// <summary>Sample values generated from a view model matched to the view by name, with nothing declared.</summary>
    public const string Inferred = "Inferred";
    /// <summary>Sample items with no declared shape, answering bindings by property name.</summary>
    public const string Untyped = "Untyped";
    /// <summary>Objects loaded from a design data XAML file (d:DesignData).</summary>
    public const string File = "File";
}
public sealed record PreviewSnapshot(long Version, bool Success, byte[]? PngBytes, int PixelWidth, int PixelHeight,
    IReadOnlyList<PreviewNode> Nodes, IReadOnlyList<PreviewDiagnostic> Diagnostics, string? Status = null,
    PreviewBuildProvenance? Build = null, PreviewScenarioProvenance? Scenario = null, PreviewSurfaceIdentity? Surface = null,
    IReadOnlyList<PreviewDesignData>? DesignData = null);
/// <summary>Observes the existing preview instance without reloading source, recreating the view, or applying scenario data.</summary>
public sealed record PreviewCaptureRequest(long Version);
public sealed record PreviewNodeRequest(long Version, string NodeId);
/// <summary><paramref name="PreferAuthored"/> selects the deepest element declared in the source
/// rather than a template or framework part under the point, when one contains it.</summary>
public sealed record PreviewPickRequest(long Version, double X, double Y, bool PreferAuthored = false);
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

// The .NET Framework runtime has no default interface methods. Its host implements every member anyway.
#if NETFRAMEWORK
public interface IPreviewRpc
{
    Task<PreviewHostSession> GetSessionAsync(CancellationToken cancellationToken);
    Task<PreviewSurfaceResponse> UpdateSurfaceAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken);
    Task<PreviewSurfaceHeartbeat> HeartbeatSurfaceAsync(PreviewSurfaceHeartbeatRequest request, CancellationToken cancellationToken);
    Task<PreviewSnapshot> RenderAsync(PreviewRequest request, CancellationToken cancellationToken);
    Task<PreviewSnapshot> CaptureAsync(PreviewCaptureRequest request, CancellationToken cancellationToken);
    Task<PreviewInspection> InspectAsync(PreviewNodeRequest request, CancellationToken cancellationToken);
    Task<PreviewInspection> PickAsync(PreviewPickRequest request, CancellationToken cancellationToken);
    Task<PreviewEditResult> SetPropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken);
    Task<PreviewPropertyValidation> ValidatePropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken);
    Task<PreviewLayoutValidationResult> ValidateLayoutEditAsync(PreviewLayoutValidationRequest request, CancellationToken cancellationToken);
    Task<WpfStudio.Inspection.Protocol.AppearanceResponse> GetAppearanceAsync(
        WpfStudio.Inspection.Protocol.AppearanceRequest request, CancellationToken cancellationToken);
    Task<WpfStudio.Inspection.Protocol.BindingSourceResponse> GetBindingSourceAsync(
        WpfStudio.Inspection.Protocol.BindingSourceRequest request, CancellationToken cancellationToken);
}
#else
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
#endif
