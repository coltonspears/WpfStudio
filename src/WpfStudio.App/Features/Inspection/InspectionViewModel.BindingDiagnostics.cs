using WpfStudio.App.Features.BindingDiagnostics;

namespace WpfStudio.App.Features.Inspection;

public sealed partial class InspectionViewModel
{
    public BindingExplanationViewModel BindingExplanation { get; } = new();
    private bool _bindingObservationReady;
    private long _bindingObservationRevision;
    private long _bindingOperationEpoch;

    private void ClearBindingExplanation(string status)
    {
        _bindingObservationReady = false;
        BindingExplanation.Clear(status);
    }
    private void RefreshBindingExplanation()
    {
        if (_disposed || !IsConnected || IsPaused)
        {
            ClearBindingExplanation(IsPaused ? "Debugger paused. Resume and refresh to observe current bindings."
                : "Connect and select an element to observe its bindings.");
            return;
        }
        if (IsPropertyOperationRunning || IsSourceEditRunning || !_bindingObservationReady || _bindingObservationRevision != _revision)
        { BindingExplanation.Clear("Refresh or finish inspecting the selected element to observe its bindings."); return; }
        BindingExplanation.ExplainSelected(BindingDeclarations, Bindings.Count > 0, "This agent did not supply binding expression identities. A selected-binding explanation is unavailable.",
            item => Bindings.FirstOrDefault(value => value.PropertyId == item.PropertyId && value.Binding?.Sources?.BindingId == item.BindingId) is { } property
                ? (property.Binding, property.Value, property.ValueSource) : null);
    }
}
