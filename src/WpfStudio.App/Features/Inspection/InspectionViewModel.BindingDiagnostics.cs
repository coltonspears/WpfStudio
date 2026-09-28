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
        if (BindingDeclarations.Count == 0 && Bindings.Count > 0)
        { BindingExplanation.Clear("This agent did not supply binding expression identities. A selected-binding explanation is unavailable."); return; }
        if (SelectedBindingDeclaration is not { } item || !BindingDeclarations.Any(current => current.SameIdentity(item)))
        { BindingExplanation.Clear("Select a binding declaration. A removed or replaced expression is not the previous binding."); return; }
        var property = Bindings.FirstOrDefault(value => value.PropertyId == item.PropertyId && value.Binding?.Sources?.BindingId == item.BindingId);
        if (property is null) { BindingExplanation.Clear("The selected binding is no longer in this observation."); return; }
        var observation = item.Declaration.Observation ?? (item.Declaration.ParentExpressionId is null ? property.Binding : null);
        BindingExplanation.Apply(item, observation, property.Value, property.ValueSource);
    }
}
