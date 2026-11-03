using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Appearance;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Inspection;

public sealed partial class InspectionViewModel
{
    private bool _appearanceElementAvailable;
    private AppearanceReader Appearance => field ??= new(RefreshAppearanceCommand.NotifyCanExecuteChanged);
    public AppearanceInspectorViewModel AppearanceDetails => Appearance.Details;
    [ObservableProperty] public partial bool AppearanceTabIsActive { get; set; }

    partial void OnAppearanceTabIsActiveChanged(bool value)
    {
        ClearAppearance(value ? AppearanceUnavailableStatus() : "Open Appearance to inspect a property.");
        if (value) RefreshAppearanceAsync().Forget(ReportFault);
    }
    private string AppearanceUnavailableStatus() => !IsConnected ? "Connect to a running application to inspect appearance."
        : IsPaused ? "Debugger paused. Resume and refresh the element to inspect appearance."
        : _session?.Hello?.Capabilities.Contains("appearance") != true ? "This inspection agent does not provide appearance details."
        : !_appearanceElementAvailable ? "Refresh and select an available element to inspect appearance."
        : SelectedProperty?.PropertyId is null ? "Select a property with an observed identity to inspect its appearance."
        : "Refresh appearance for the selected property.";
    private bool CanRefreshAppearance() => !_disposed && IsConnected && !IsPaused && _appearanceElementAvailable
        && !IsPropertyOperationRunning && !IsSourceEditRunning && _session?.Hello?.Capabilities.Contains("appearance") == true
        && SelectedNode is not null && SelectedProperty is { PropertyId: not null } property && Properties.Contains(property);
    private void ClearAppearance(string status, bool invalidateElement = false)
    {
        if (invalidateElement) _appearanceElementAvailable = false;
        Appearance.Clear(status);
    }
    private void UpdateAppearanceState()
    {
        if (!IsConnected || IsPaused || !_appearanceElementAvailable || _session?.Hello?.Capabilities.Contains("appearance") != true)
            ClearAppearance(AppearanceUnavailableStatus(), invalidateElement: true);
        RefreshAppearanceCommand.NotifyCanExecuteChanged();
    }
    private void RefreshAppearanceSelection()
    {
        ClearAppearance(AppearanceUnavailableStatus());
        if (AppearanceTabIsActive && CanRefreshAppearance()) RefreshAppearanceAsync().Forget(ReportFault);
    }
    private void AppearanceOperationChanged(bool running)
    {
        if (running) ClearAppearance("Property operation in progress. Refresh appearance after it completes.");
        RefreshAppearanceCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanRefreshAppearance))]
    private Task RefreshAppearanceAsync()
    {
        if (!CanRefreshAppearance() || _session is not { } session) return Task.CompletedTask;
        var property = SelectedProperty!;
        var generation = _generation; var revision = _revision; var selection = _selection;
        var request = new AppearanceRequest(revision, SelectedNode!.Node.Id, property.Name, property.OwnerType, property.OwnerAssembly, property.PropertyId);
        return Appearance.ReadAsync(request, property.ValueSource, property.IsOverridden, _poll?.Token ?? default,
            () => !_disposed && generation == _generation && revision == _revision
                && selection == _selection && ReferenceEquals(session, _session) && session.IsConnected
                && !session.IsDebuggerPaused && IsConnected && !IsPaused && _appearanceElementAvailable
                && !IsPropertyOperationRunning && !IsSourceEditRunning && SelectedNode?.Node.Id == request.NodeId
                && ReferenceEquals(SelectedProperty, property) && Properties.Contains(property),
            session.GetAppearanceAsync);
    }
}
