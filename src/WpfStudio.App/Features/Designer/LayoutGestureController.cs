using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Designer;

public sealed record DesignerLayoutSourceEdit(XamlPropertyEditResult Proposal, Func<bool> IsCurrent,
    Func<CancellationToken, Task<PreviewLayoutValidationResult>> Validate);

/// <summary>One move or resize on the preview at a time: its draft while it runs, then one reviewed XAML edit when it ends.</summary>
public sealed partial class LayoutGestureController(ILayoutGestureHost host) : ObservableObject
{
    private PreviewLayoutEditContext? _gestureContext;
    private Func<bool>? _gestureCurrent;
    private LayoutGestureSource? _gestureSource;
    private XamlLayoutHandle _gestureHandle;
    private long _gestureEpoch;
    private bool _reviewPending;

    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool SnapToLayout { get; set; } = true;
    /// <summary>What the selected element's observation allows, once the designer has matched it to the current preview.</summary>
    [ObservableProperty] public partial PreviewLayoutEditContext? Context { get; set; }
    [ObservableProperty] public partial XamlLayoutGestureResult? Draft { get; set; }

    public string Status => _reviewPending ? "Review the layout changes before applying."
        : Draft?.Error is { } error ? error
        : !IsEnabled ? "Enable layout editing to move and resize an authored element."
        : host.IsInteracting ? "Choose Inspect to edit layout."
        : Context is not { Available: true } ? Context?.Status ?? "Select an authored Canvas or Grid child in the current source preview."
        : Draft?.Bounds is { } bounds ? $"{bounds.Width:0.##} × {bounds.Height:0.##} · {bounds.X:0.##}, {bounds.Y:0.##} · release or Enter to review; Escape cancels"
        : "Drag to move; use the handles to resize. Arrows nudge, Shift accelerates, Ctrl+arrows resize; Alt bypasses snapping.";

    partial void OnIsEnabledChanged(bool value)
    {
        if (!value) CancelGesture();
        NotifyStatus();
    }
    partial void OnContextChanged(PreviewLayoutEditContext? value)
    {
        CancelGesture();
        NotifyStatus();
    }
    partial void OnDraftChanged(XamlLayoutGestureResult? value) => NotifyStatus();
    internal void NotifyStatus() => OnPropertyChanged(nameof(Status));

    internal void Clear()
    {
        CancelGesture();
        Context = null;
    }

    private void CancelGesture()
    {
        ++_gestureEpoch;
        _gestureContext = null;
        _gestureCurrent = null;
        _gestureSource = null;
        Draft = null;
    }

    private bool CanBeginGesture() => IsEnabled && !_reviewPending
        && Context is { Available: true, Token.Length: > 0, Element: not null, Parent: not null, Bounds: not null } context && host.CanEditLayout(context);

    [RelayCommand]
    private async Task GestureAsync(PreviewLayoutGesture request)
    {
        if (request.Phase == PreviewLayoutGesturePhase.Cancel) { CancelGesture(); return; }
        if (request.Phase == PreviewLayoutGesturePhase.Begin)
        {
            CancelGesture();
            if (!CanBeginGesture()) return;
            var context = Context!;
            var source = host.CaptureGestureSource(context);
            var epoch = _gestureEpoch;
            _gestureContext = context;
            _gestureSource = source;
            _gestureHandle = request.Handle;
            _gestureCurrent = () => epoch == _gestureEpoch && IsEnabled && ReferenceEquals(context, Context) && source.IsUnchanged();
            Draft = XamlLayoutEditService.Calculate(context, new(request.Handle, 0, 0, request.Zoom, true));
            return;
        }
        // Moves only redraw the draft, and come with every mouse event, so reading and hashing the scenario file
        // waits for the commit. Its watcher still invalidates the preview, and so the gesture, on an ordinary change.
        if (_gestureCurrent is not { } live || !live() || (request.Phase == PreviewLayoutGesturePhase.Commit && !host.IsScenarioConfigurationCurrent())
            || _gestureContext is not { } captured || _gestureSource is not { } edited || request.Handle != _gestureHandle) { CancelGesture(); return; }
        if (_reviewPending) return;
        Draft = XamlLayoutEditService.Calculate(captured,
            new(request.Handle, request.DeltaX, request.DeltaY, request.Zoom, request.BypassSnap || !SnapToLayout));
        if (request.Phase != PreviewLayoutGesturePhase.Commit || Draft is not { Success: true, Bounds: { } bounds } draft) return;
        if (bounds == captured.Bounds) { CancelGesture(); return; }
        var validation = new PreviewLayoutValidationRequest(captured.Version, captured.NodeId, captured.Token!, Surface: captured.Surface, Values: draft.Values);
        bool StillCurrent() => live() && host.IsScenarioConfigurationCurrent();
        async Task<PreviewLayoutValidationResult> Validate(CancellationToken token)
        {
            if (!StillCurrent()) return new(validation, false, "The source or preview changed during the layout gesture. Refresh and try again.");
            var result = await host.ValidateLayoutEditAsync(validation, token);
            if (result.Request != validation || !StillCurrent())
                return new(validation, false, "The layout observation changed. Refresh and try again.");
            return result;
        }
        _reviewPending = true;
        NotifyStatus();
        try
        {
            var valid = await Validate(host.Lifetime);
            if (!valid.Success) { host.ReportStatus(valid.Error ?? "Layout changed. Update the snapshot and try again."); return; }
            var proposal = XamlLayoutEditService.CreateEdit(new(edited.Document.Path, edited.Document.Content,
                edited.Document.Version, edited.SourceVersion, edited.SourceHash, captured, bounds, request.Handle,
                SourceAssembly: edited.SourceAssembly));
            if (!proposal.Success) { host.ReportStatus(proposal.Error ?? proposal.Explanation); return; }
            if (!StillCurrent()) return;
            bool applied = await host.ReviewLayoutEditAsync(new(proposal, StillCurrent, Validate));
            if (edited.IsDocumentOpen())
                host.ReportStatus(applied ? "Layout updated in XAML. Save writes the file; Undo workspace edit restores the whole gesture."
                    : "Layout edit cancelled.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!host.IsDisposed) host.ReportStatus(exception.Message); }
        finally
        {
            _reviewPending = false;
            CancelGesture();
            NotifyStatus();
        }
    }
}
