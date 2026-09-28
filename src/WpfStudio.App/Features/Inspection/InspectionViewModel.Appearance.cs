using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Appearance;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Inspection;

public sealed partial class InspectionViewModel
{
    private CancellationTokenSource? _appearanceRead;
    private long _appearanceSequence;
    private bool _appearanceElementAvailable;
    public AppearanceInspectorViewModel AppearanceDetails { get; } = new();
    [ObservableProperty] public partial bool AppearanceTabIsActive { get; set; }

    partial void OnAppearanceTabIsActiveChanged(bool value)
    {
        ClearAppearance(value ? AppearanceUnavailableStatus() : "Open Appearance to inspect a property.");
        if (value) _ = RefreshAppearanceAsync();
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
        ++_appearanceSequence;
        _appearanceRead?.Cancel(); _appearanceRead?.Dispose(); _appearanceRead = null;
        if (invalidateElement) _appearanceElementAvailable = false;
        AppearanceDetails.Clear(status);
        RefreshAppearanceCommand.NotifyCanExecuteChanged();
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
        if (AppearanceTabIsActive && CanRefreshAppearance()) _ = RefreshAppearanceAsync();
    }
    private void AppearanceOperationChanged(bool running)
    {
        if (running) ClearAppearance("Property operation in progress. Refresh appearance after it completes.");
        RefreshAppearanceCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanRefreshAppearance))]
    private async Task RefreshAppearanceAsync()
    {
        if (!CanRefreshAppearance() || _session is not { } session) return;
        ClearAppearance("Reading appearance…");
        var property = SelectedProperty!;
        var generation = _generation; var revision = _revision; var selection = _selection; var sequence = _appearanceSequence;
        var request = new AppearanceRequest(revision, SelectedNode!.Node.Id, property.Name, property.OwnerType, property.OwnerAssembly, property.PropertyId);
        _appearanceRead = CancellationTokenSource.CreateLinkedTokenSource(_poll?.Token ?? default);
        var token = _appearanceRead.Token;
        AppearanceDetails.Begin(property.ValueSource, property.IsOverridden);
        bool CurrentAppearance() => !token.IsCancellationRequested && !_disposed && generation == _generation && revision == _revision
            && selection == _selection && sequence == _appearanceSequence && ReferenceEquals(session, _session) && session.IsConnected
            && !session.IsDebuggerPaused && IsConnected && !IsPaused && _appearanceElementAvailable
            && !IsPropertyOperationRunning && !IsSourceEditRunning && SelectedNode?.Node.Id == request.NodeId
            && ReferenceEquals(SelectedProperty, property) && Properties.Contains(property);
        try
        {
            var response = await session.GetAppearanceAsync(request, token);
            if (!CurrentAppearance()) return;
            if (response.Request != request) { AppearanceDetails.Clear("Appearance response did not match the selected property. Refresh to try again."); return; }
            AppearanceDetails.Apply(response.Snapshot);
        }
        catch (OperationCanceledException) { if (CurrentAppearance()) AppearanceDetails.Clear("Appearance request cancelled. Refresh to try again."); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (CurrentAppearance()) AppearanceDetails.Clear("Appearance unavailable: " + exception.Message); }
    }
}
