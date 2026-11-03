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
        BindingExplanation.ExplainSelected(BindingDeclarations, Bindings.Count > 0, "This host did not supply binding expression identities. A selected-binding explanation is unavailable.",
            item => Bindings.FirstOrDefault(value => value.BindingSources?.BindingId == item.BindingId && value.Name == item.Property
                && value.OwnerType == item.OwnerType && value.OwnerAssembly == item.OwnerAssembly) is { } property ? (property.Binding, property.Value, property.ValueSource) : null);
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
        var selection = BindingDeclarations.UserSelection;
        await InspectAsync(node.Node.Id);
        if (!Current(revision) || !ReferenceEquals(snapshot, _snapshot) || selection != BindingDeclarations.UserSelection
            || SelectedNode?.Node.Id != node.Node.Id || _inspectedNodeId != node.Node.Id) return;
        var matches = BindingDeclarations.Items.Where(item => item.BindingId == diagnostic.BindingId && item.Declaration.ParentExpressionId is null).ToArray();
        if (matches.Length == 1) BindingDeclarations.Selected = matches[0];
        else
        {
            BindingDeclarations.Selected = null;
            BindingDeclarations.MarkSelectionRemoved();
            BindingExplanation.Clear("That diagnostic's binding was removed or replaced. The replacement is a different expression.");
            BindingDeclarations.Status = BindingExplanation.Status;
        }
    }
}
