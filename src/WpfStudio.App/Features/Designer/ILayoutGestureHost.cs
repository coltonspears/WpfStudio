using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.Features.Designer;

/// <summary>
/// What a layout gesture edits, as it was when the gesture began. <see cref="IsUnchanged"/> holds while the preview, its
/// source and its selection stay that way; <see cref="IsDocumentOpen"/> says whether its outcome is still worth reporting.
/// </summary>
public sealed record LayoutGestureSource(DocumentState Document, long SourceVersion, string SourceHash, string? SourceAssembly, Func<bool> IsUnchanged, Func<bool> IsDocumentOpen);

/// <summary>The designer's state and services a layout gesture is checked against and committed through.</summary>
public interface ILayoutGestureHost
{
    bool IsInteracting { get; }
    bool IsDisposed { get; }
    CancellationToken Lifetime { get; }
    /// <summary>Whether a gesture may begin on this observation: nothing is running and the preview still shows it.</summary>
    bool CanEditLayout(PreviewLayoutEditContext context);
    LayoutGestureSource CaptureGestureSource(PreviewLayoutEditContext context);
    /// <summary>Reads the scenario file, so it is for a commit rather than for every mouse move.</summary>
    bool IsScenarioConfigurationCurrent();
    Task<PreviewLayoutValidationResult> ValidateLayoutEditAsync(PreviewLayoutValidationRequest request, CancellationToken cancellationToken);
    /// <summary>Reviews and applies the edit; false when the review was cancelled.</summary>
    Task<bool> ReviewLayoutEditAsync(DesignerLayoutSourceEdit edit);
    void ReportStatus(string status);
}
