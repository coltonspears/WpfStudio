using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.BindingDiagnostics;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    public BindingExplanationViewModel BindingExplanation { get; } = new();
    [ObservableProperty] public partial int SelectedTabIndex { get; set; }
    private bool _bindingObservationReady, _updatingBindingProperties;
    private string? _bindingObservedNodeId;
    private long _bindingDiagnosticSelection;
    private long _bindingOperationEpoch;
    private int _bindingOperations;

    private void ClearBindingExplanation(string status)
    {
        _bindingObservationReady = false;
        BindingExplanation.Clear(status);
    }
    private void RefreshBindingExplanation()
    {
        ShowBindingDiagnosticCommand.NotifyCanExecuteChanged();
        if (_disposed || !IsCurrent)
        { BindingExplanation.Clear("Refresh the preview to observe its bindings."); return; }
        if (_appearancePropertyOperations != 0 || !_bindingObservationReady || _inspectedNodeId != SelectedNode?.Node.Id)
        { BindingExplanation.Clear("Refresh or finish inspecting the selected element to observe its bindings."); return; }
        if (BindingDeclarations.Count == 0 && Bindings.Count > 0)
        { BindingExplanation.Clear("This host did not supply binding expression identities. A selected-binding explanation is unavailable."); return; }
        if (SelectedBindingDeclaration is not { } item || !BindingDeclarations.Any(current => current.SameIdentity(item)))
        { BindingExplanation.Clear("Select a binding declaration. A removed or replaced expression is not the previous binding."); return; }
        var property = Bindings.FirstOrDefault(value => value.BindingSources?.BindingId == item.BindingId
            && value.Name == item.Property && value.OwnerType == item.OwnerType && value.OwnerAssembly == item.OwnerAssembly);
        if (property is null) { BindingExplanation.Clear("The selected binding is no longer in this observation."); return; }
        var observation = item.Declaration.Observation ?? (item.Declaration.ParentExpressionId is null ? property.Binding : null);
        BindingExplanation.Apply(item, observation, property.Value, property.ValueSource);
    }

    private bool CanShowBindingDiagnostic(PreviewDiagnostic? diagnostic) => !_disposed && IsCurrent && !IsBusy
        && _appearancePropertyOperations == 0 && diagnostic?.BindingId is not null && diagnostic.NodeId is not null
        && Diagnostics.Contains(diagnostic) && _snapshot?.Nodes.Any(node => node.Id == diagnostic.NodeId) == true;

    [RelayCommand(CanExecute = nameof(CanShowBindingDiagnostic))]
    private async Task ShowBindingDiagnosticAsync(PreviewDiagnostic? diagnostic)
    {
        if (!CanShowBindingDiagnostic(diagnostic) || diagnostic is null) return;
        if (_snapshot?.Nodes.FirstOrDefault(node => node.Id == diagnostic.NodeId)?.IsVisual == false) ShowLogicalTree = true;
        if (FindNode(Tree, diagnostic.NodeId!) is not { } node) return;
        var revision = _revision;
        var snapshot = _snapshot;
        _picking = true;
        try { SelectedNode = node; SelectedTabIndex = 1; }
        finally { _picking = false; }
        var selection = _bindingDiagnosticSelection;
        await InspectAsync(node.Node.Id);
        if (!Current(revision) || !ReferenceEquals(snapshot, _snapshot) || selection != _bindingDiagnosticSelection
            || SelectedNode?.Node.Id != node.Node.Id || _inspectedNodeId != node.Node.Id) return;
        var matches = BindingDeclarations.Where(item => item.BindingId == diagnostic.BindingId && item.Declaration.ParentExpressionId is null).ToArray();
        if (matches.Length == 1) SelectedBindingDeclaration = matches[0];
        else
        {
            SelectedBindingDeclaration = null;
            _bindingSelectionRemoved = true;
            BindingExplanation.Clear("That diagnostic's binding was removed or replaced. The replacement is a different expression.");
            BindingSourceStatus = BindingExplanation.Status;
        }
    }
}
