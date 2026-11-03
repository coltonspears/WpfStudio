using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel : ILayoutGestureHost
{
    public LayoutGestureController LayoutGesture { get; }

    private void ApplyLayoutEditing(PreviewInspection inspection)
    {
        var context = inspection.LayoutEditing;
        LayoutGesture.Context = IsCurrent && !IsCompiledPreview && inspection.Node is { } node
            && context?.NodeId == node.Id && context.Version == _revision && context.Surface == _snapshot?.Surface
            ? context : null;
    }

    bool ILayoutGestureHost.IsDisposed => _disposed;
    CancellationToken ILayoutGestureHost.Lifetime => _lifetime.Token;
    bool ILayoutGestureHost.CanEditLayout(PreviewLayoutEditContext context) => !_disposed
        && IsCurrent && !IsBusy && !IsCompiledPreview && !IsInteracting && _bindingOperations == 0
        && _appearancePropertyOperations == 0 && _document?.Version == _sourceVersion && _sourceHash is not null
        && context.Version == _revision && context.NodeId == SelectedNode?.Node.Id && context.NodeId == _inspectedNodeId
        && context.SourceHash == _sourceHash && context.Surface == _snapshot?.Surface && _sourceHost is not null
        && Scenarios.IsConfigurationCurrent();
    LayoutGestureSource ILayoutGestureHost.CaptureGestureSource(PreviewLayoutEditContext context)
    {
        var document = _document!;
        var snapshot = _snapshot;
        long revision = _revision, selection = _selectionRevision;
        long sourceVersion = _sourceVersion, propertyEpoch = _bindingOperationEpoch;
        string sourceHash = _sourceHash!;
        return new(document, sourceVersion, sourceHash, _sourceAssembly,
            () => !_disposed && Current(revision) && IsCurrent && !IsBusy && !IsInteracting && !IsCompiledPreview
                && selection == _selectionRevision && propertyEpoch == _bindingOperationEpoch
                && _bindingOperations == 0 && _appearancePropertyOperations == 0
                && ReferenceEquals(document, _document) && document.Version == sourceVersion && _sourceHash == sourceHash
                && ReferenceEquals(snapshot, _snapshot) && context.NodeId == SelectedNode?.Node.Id && _inspectedNodeId == context.NodeId,
            () => !_disposed && ReferenceEquals(document, _document));
    }
    bool ILayoutGestureHost.IsScenarioConfigurationCurrent() => Scenarios.IsConfigurationCurrent();
    Task<PreviewLayoutValidationResult> ILayoutGestureHost.ValidateLayoutEditAsync(PreviewLayoutValidationRequest request, CancellationToken cancellationToken) =>
        _client.ValidateLayoutEditAsync(request, cancellationToken);
    // A gesture only begins with a host attached, and a host is never detached.
    Task<bool> ILayoutGestureHost.ReviewLayoutEditAsync(DesignerLayoutSourceEdit edit) => _sourceHost!.ReviewLayoutEditAsync(edit);
    void ILayoutGestureHost.ReportStatus(string status) => Status = status;
}
