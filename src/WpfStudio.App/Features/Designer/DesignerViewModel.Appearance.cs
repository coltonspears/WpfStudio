using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Appearance;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    private int _appearancePropertyOperations;
    private AppearanceReader Appearance => field ??= new(RefreshAppearanceCommand.NotifyCanExecuteChanged);
    public AppearanceInspectorViewModel AppearanceDetails => Appearance.Details;
    [ObservableProperty] public partial bool AppearanceTabIsActive { get; set; }

    partial void OnAppearanceTabIsActiveChanged(bool value)
    {
        ClearAppearance(value ? "Select a property to inspect its appearance." : "Open Appearance to inspect a property.");
        if (value) RefreshAppearanceAsync().Forget(ReportFault);
    }
    private bool CanRefreshAppearance() => !_disposed && _appearancePropertyOperations == 0 && IsCurrent && SelectedNode is { } node
        && _inspectedNodeId == node.Node.Id && SelectedProperty is { OwnerType: not null, OwnerAssembly: not null } property
        && Properties.Contains(property);
    private void ClearAppearance(string status) => Appearance.Clear(status);
    private void RefreshAppearanceSelection()
    {
        ClearAppearance(IsCurrent ? "Select a property to inspect its appearance." : "Refresh the preview before inspecting appearance.");
        if (AppearanceTabIsActive && CanRefreshAppearance()) RefreshAppearanceAsync().Forget(ReportFault);
    }
    [RelayCommand(CanExecute = nameof(CanRefreshAppearance))]
    private Task RefreshAppearanceAsync()
    {
        if (!CanRefreshAppearance()) return Task.CompletedTask;
        var property = SelectedProperty!;
        var revision = _revision; var selection = _selectionRevision;
        var request = new AppearanceRequest(revision, SelectedNode!.Node.Id, property.Name, property.OwnerType, property.OwnerAssembly);
        return Appearance.ReadAsync(request, property.ValueSource, property.IsOverridden, _lifetime.Token,
            () => Current(revision) && IsCurrent && selection == _selectionRevision && SelectedNode?.Node.Id == request.NodeId
                && ReferenceEquals(SelectedProperty, property) && Properties.Contains(property),
            // Preview transport cancellation stops its isolated host. Selection
            // changes only discard this read; lifetime cancellation can stop it.
            (asked, _) => _client.GetAppearanceAsync(asked, _lifetime.Token));
    }
}
