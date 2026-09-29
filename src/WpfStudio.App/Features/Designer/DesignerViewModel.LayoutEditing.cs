using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Designer;

public sealed record DesignerLayoutSourceEdit(XamlPropertyEditResult Proposal, Func<bool> IsCurrent,
    Func<CancellationToken, Task<PreviewLayoutValidationResult>> Validate);

public sealed partial class DesignerViewModel
{
    private PreviewLayoutEditContext? _gestureContext;
    private Func<bool>? _gestureCurrent;
    private XamlLayoutHandle _gestureHandle;
    private long _layoutGestureEpoch;
    private bool _layoutReviewPending;

    public event Func<DesignerLayoutSourceEdit, Task<bool>>? LayoutEditRequested;
    [ObservableProperty] public partial bool IsLayoutEditingEnabled { get; set; }
    [ObservableProperty] public partial bool SnapToLayout { get; set; } = true;
    [ObservableProperty] public partial PreviewLayoutEditContext? LayoutEditing { get; set; }
    [ObservableProperty] public partial XamlLayoutGestureResult? LayoutDraft { get; set; }

    public string LayoutEditingStatus => _layoutReviewPending ? "Review the layout changes before applying."
        : LayoutDraft?.Error is { } error ? error
        : !IsLayoutEditingEnabled ? "Enable layout editing to move and resize an authored element."
        : IsInteracting ? "Choose Inspect to edit layout."
        : LayoutEditing is not { Available: true } ? LayoutEditing?.Status ?? "Select an authored Canvas or Grid child in the current source preview."
        : LayoutDraft?.Bounds is { } bounds ? $"{bounds.Width:0.##} × {bounds.Height:0.##} · {bounds.X:0.##}, {bounds.Y:0.##} · release or Enter to review; Escape cancels"
        : "Drag to move; use the handles to resize. Arrows nudge, Shift accelerates, Ctrl+arrows resize; Alt bypasses snapping.";

    partial void OnIsLayoutEditingEnabledChanged(bool value)
    {
        if (!value) CancelLayoutGesture();
        NotifyLayoutEditingState();
    }
    partial void OnLayoutEditingChanged(PreviewLayoutEditContext? value)
    {
        CancelLayoutGesture();
        NotifyLayoutEditingState();
    }
    partial void OnLayoutDraftChanged(XamlLayoutGestureResult? value) => NotifyLayoutEditingState();
    private void NotifyLayoutEditingState() => OnPropertyChanged(nameof(LayoutEditingStatus));

    private void ClearLayoutEditing()
    {
        CancelLayoutGesture();
        LayoutEditing = null;
    }

    private void ApplyLayoutEditing(PreviewInspection inspection)
    {
        var context = inspection.LayoutEditing;
        LayoutEditing = IsCurrent && !IsCompiledPreview && inspection.Node is { } node
            && context?.NodeId == node.Id && context.Version == _revision && context.Surface == _snapshot?.Surface
            ? context : null;
    }

    private void CancelLayoutGesture()
    {
        ++_layoutGestureEpoch;
        _gestureContext = null;
        _gestureCurrent = null;
        LayoutDraft = null;
    }

    private bool CanBeginLayoutGesture() => !_disposed && IsLayoutEditingEnabled && !_layoutReviewPending
        && IsCurrent && !IsBusy && !IsCompiledPreview && !IsInteracting && _bindingOperations == 0
        && _appearancePropertyOperations == 0 && _document?.Version == _sourceVersion && _sourceHash is not null
        && LayoutEditing is { Available: true, Token.Length: > 0, Element: not null, Parent: not null, Bounds: not null } context
        && context.Version == _revision && context.NodeId == SelectedNode?.Node.Id && context.NodeId == _inspectedNodeId
        && context.SourceHash == _sourceHash && context.Surface == _snapshot?.Surface && LayoutEditRequested is not null
        && IsScenarioConfigurationCurrent();

    [RelayCommand]
    private async Task LayoutGestureAsync(PreviewLayoutGesture request)
    {
        if (request.Phase == PreviewLayoutGesturePhase.Cancel) { CancelLayoutGesture(); return; }
        if (request.Phase == PreviewLayoutGesturePhase.Begin)
        {
            CancelLayoutGesture();
            if (!CanBeginLayoutGesture()) return;
            var document = _document!;
            var context = LayoutEditing!;
            var snapshot = _snapshot;
            long epoch = _layoutGestureEpoch, revision = _revision, selection = _selectionRevision;
            long sourceVersion = _sourceVersion, propertyEpoch = _bindingOperationEpoch;
            string sourceHash = _sourceHash!;
            _gestureContext = context;
            _gestureHandle = request.Handle;
            _gestureCurrent = () => !_disposed && epoch == _layoutGestureEpoch && IsLayoutEditingEnabled
                && Current(revision) && IsCurrent && !IsBusy && !IsInteracting && !IsCompiledPreview
                && selection == _selectionRevision && propertyEpoch == _bindingOperationEpoch
                && _bindingOperations == 0 && _appearancePropertyOperations == 0
                && ReferenceEquals(document, _document) && document.Version == sourceVersion && _sourceHash == sourceHash
                && ReferenceEquals(snapshot, _snapshot) && ReferenceEquals(context, LayoutEditing)
                && context.NodeId == SelectedNode?.Node.Id && _inspectedNodeId == context.NodeId && IsScenarioConfigurationCurrent();
            LayoutDraft = XamlLayoutEditService.Calculate(context, new(request.Handle, 0, 0, request.Zoom, true));
            return;
        }
        if (_gestureCurrent is not { } current || !current() || _gestureContext is not { } captured
            || request.Handle != _gestureHandle) { CancelLayoutGesture(); return; }
        if (_layoutReviewPending) return;
        LayoutDraft = XamlLayoutEditService.Calculate(captured,
            new(request.Handle, request.DeltaX, request.DeltaY, request.Zoom, request.BypassSnap || !SnapToLayout));
        if (request.Phase != PreviewLayoutGesturePhase.Commit || LayoutDraft is not { Success: true, Bounds: { } bounds } draft) return;
        if (bounds == captured.Bounds) { CancelLayoutGesture(); return; }
        var documentForEdit = _document!;
        var sourceVersionForEdit = _sourceVersion;
        var sourceHashForEdit = _sourceHash!;
        var validation = new PreviewLayoutValidationRequest(captured.Version, captured.NodeId, captured.Token!, Surface: captured.Surface, Values: draft.Values);
        async Task<PreviewLayoutValidationResult> Validate(CancellationToken token)
        {
            if (!current()) return new(validation, false, "The source or preview changed during the layout gesture. Refresh and try again.");
            var result = await _client.ValidateLayoutEditAsync(validation, token);
            if (result.Request != validation || !current())
                return new(validation, false, "The layout observation changed. Refresh and try again.");
            return result;
        }
        _layoutReviewPending = true;
        NotifyLayoutEditingState();
        try
        {
            var valid = await Validate(_lifetime.Token);
            if (!valid.Success) { Status = valid.Error ?? "Layout changed. Update the snapshot and try again."; return; }
            var proposal = XamlLayoutEditService.CreateEdit(new(documentForEdit.Path, documentForEdit.Content,
                documentForEdit.Version, sourceVersionForEdit, sourceHashForEdit, captured, bounds, request.Handle,
                SourceAssembly: _sourceAssembly));
            if (!proposal.Success) { Status = proposal.Error ?? proposal.Explanation; return; }
            if (!current() || LayoutEditRequested is not { } apply) return;
            bool applied = await apply(new(proposal, current, Validate));
            if (!_disposed && ReferenceEquals(documentForEdit, _document))
                Status = applied ? "Layout updated in XAML. Save writes the file; Undo workspace edit restores the whole gesture."
                    : "Layout edit cancelled.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) Status = exception.Message; }
        finally
        {
            _layoutReviewPending = false;
            CancelLayoutGesture();
            NotifyLayoutEditingState();
        }
    }
}
