using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Appearance;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    private CancellationTokenSource? _appearanceRead;
    private long _appearanceSequence;
    private int _appearancePropertyOperations;
    public AppearanceInspectorViewModel AppearanceDetails { get; } = new();
    [ObservableProperty] public partial bool AppearanceTabIsActive { get; set; }

    partial void OnAppearanceTabIsActiveChanged(bool value)
    {
        ClearAppearance(value ? "Select a property to inspect its appearance." : "Open Appearance to inspect a property.");
        if (value) _ = RefreshAppearanceAsync();
    }
    private bool CanRefreshAppearance() => !_disposed && _appearancePropertyOperations == 0 && IsCurrent && SelectedNode is { } node
        && _inspectedNodeId == node.Node.Id && SelectedProperty is { OwnerType: not null, OwnerAssembly: not null } property
        && Properties.Contains(property);
    private void ClearAppearance(string status)
    {
        ++_appearanceSequence;
        _appearanceRead?.Cancel(); _appearanceRead?.Dispose(); _appearanceRead = null;
        AppearanceDetails.Clear(status);
        RefreshAppearanceCommand.NotifyCanExecuteChanged();
    }
    private void RefreshAppearanceSelection()
    {
        ClearAppearance(IsCurrent ? "Select a property to inspect its appearance." : "Refresh the preview before inspecting appearance.");
        if (AppearanceTabIsActive && CanRefreshAppearance()) _ = RefreshAppearanceAsync();
    }
    [RelayCommand(CanExecute = nameof(CanRefreshAppearance))]
    private async Task RefreshAppearanceAsync()
    {
        if (!CanRefreshAppearance()) return;
        ClearAppearance("Reading appearance…");
        var property = SelectedProperty!;
        var revision = _revision; var selection = _selectionRevision; var sequence = _appearanceSequence;
        var request = new AppearanceRequest(revision, SelectedNode!.Node.Id, property.Name, property.OwnerType, property.OwnerAssembly);
        _appearanceRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _appearanceRead.Token;
        AppearanceDetails.Begin(property.ValueSource, property.IsOverridden);
        bool CurrentAppearance() => !token.IsCancellationRequested && Current(revision) && IsCurrent && sequence == _appearanceSequence
            && selection == _selectionRevision && SelectedNode?.Node.Id == request.NodeId && ReferenceEquals(SelectedProperty, property)
            && Properties.Contains(property);
        try
        {
            // Preview transport cancellation stops its isolated host. Selection
            // changes only discard this read; lifetime cancellation can stop it.
            var response = await _client.GetAppearanceAsync(request, _lifetime.Token);
            if (!CurrentAppearance()) return;
            if (response.Request != request) { AppearanceDetails.Clear("Appearance response did not match the selected property. Refresh to try again."); return; }
            AppearanceDetails.Apply(response.Snapshot);
        }
        catch (OperationCanceledException) { if (CurrentAppearance()) AppearanceDetails.Clear("Appearance request cancelled. Refresh to try again."); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (CurrentAppearance()) AppearanceDetails.Clear("Appearance unavailable: " + exception.Message); }
    }
}
