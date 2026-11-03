using WpfStudio.App.Features.Layout;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Inspection;

public sealed partial class InspectionViewModel
{
    private bool _supportsLayout, _supportsLayoutOverlay, _layoutWasPaused, _layoutWasConnected;
    public LayoutInspectorViewModel LayoutDetails => field ??= new(() => HighlightSelectedAsync().Forget(ReportFault));

    private void UpdateLayoutState()
    {
        bool resumed = IsConnected && !IsPaused && _layoutWasPaused;
        _supportsLayout = _session?.Hello?.Capabilities.Contains("layout") == true;
        _supportsLayoutOverlay = _session?.Hello?.Capabilities.Contains("layout-overlay") == true;
        if ((!IsConnected && _layoutWasConnected) || (IsPaused && !_layoutWasPaused))
        {
            ++_selection;
            _highlight?.Cancel();
        }
        if (!IsConnected) LayoutDetails.Clear("Layout unavailable: connect to a running application.");
        else if (IsPaused) LayoutDetails.Clear("Debugger paused. Resume and refresh to observe current layout.");
        else if (!_supportsLayout) LayoutDetails.Clear("This inspection agent does not provide layout details.");
        else if (_layoutWasPaused || !_layoutWasConnected) LayoutDetails.Clear("Select an element or refresh to observe its current layout.");
        _layoutWasConnected = IsConnected; _layoutWasPaused = IsPaused;
        // The target dispatcher cannot remove its frozen adorner while paused.
        // Clear it once execution resumes, even with automatic refresh disabled.
        if (resumed) HighlightSelectedAsync(clear: true).Forget(ReportFault);
    }
    private void ClearLayout(string status, bool hideOverlay = true)
    {
        LayoutDetails.Clear(!IsConnected ? "Layout unavailable: connect to a running application."
            : IsPaused ? "Debugger paused. Resume and refresh to observe current layout."
            : !_supportsLayout ? "This inspection agent does not provide layout details." : status);
        if (hideOverlay) HighlightSelectedAsync(clear: true).Forget(ReportFault);
    }
}
