using CommunityToolkit.Mvvm.Input;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    private bool CanUpdateSnapshot() => !_disposed && IsCurrent && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpdateSnapshot))]
    private async Task UpdateSnapshotAsync()
    {
        if (!CanUpdateSnapshot()) return;
        if (_appearancePropertyOperations != 0)
        {
            Status = "Finish the property operation before updating the snapshot.";
            return;
        }

        var revision = _revision;
        var previous = _snapshot;
        var selection = ++_selectionRevision;
        var previousInspectedNode = _inspectedNodeId;
        _inspectedNodeId = null;
        ClearBindingExplanation("Updating the preview binding observation…");
        var operationEpoch = _bindingOperationEpoch;
        ++_appearancePropertyOperations;
        ++_bindingSourceEpoch;
        RefreshBindingSourceState();
        NotifySourceCommands();
        ClearAppearance("Updating the preview observation…");
        LayoutDetails.Clear("Updating the preview observation…");
        IsBusy = true;
        Status = "Updating snapshot without recreating the view…";
        try
        {
            var snapshot = await _client.CaptureAsync(new(revision), _lifetime.Token);
            if (!Current(revision) || selection != _selectionRevision || !ReferenceEquals(previous, _snapshot)) return;
            if (snapshot.Version != revision || !snapshot.Success)
            {
                _inspectedNodeId = previousInspectedNode;
                Status = snapshot.Version != revision ? "The snapshot response is out of date. Try updating again."
                    : snapshot.Status ?? "The existing preview could not be captured.";
                // Capture superseded any earlier selection request. If that
                // request had not completed, restore an inspection path too.
                if (previousInspectedNode is null && SelectedNode is { } uninspected)
                    _ = InspectAsync(uninspected.Node.Id);
                return;
            }

            SetSnapshot(snapshot);
            if (SelectedNode is { } node)
            {
                var inspection = await _client.InspectAsync(new(revision, node.Node.Id), _lifetime.Token);
                if (!Current(revision) || selection != _selectionRevision || !ReferenceEquals(snapshot, _snapshot) ||
                    SelectedNode?.Node.Id != node.Node.Id || inspection.Version != revision ||
                    inspection.Node is { } observed && observed.Id != node.Node.Id) return;

                // Property selection and typing can change during either request.
                // Keep that current draft while replacing the observed values.
                var property = SelectedProperty;
                var draft = EditedValue;
                SetInspection(inspection, operationEpoch == _bindingOperationEpoch && _bindingOperations == 0);
                if (inspection.Node is null)
                {
                    Status = inspection.Status ?? "The selected preview element is no longer available.";
                    return;
                }
                SelectedProperty = property is null ? null : Properties.FirstOrDefault(p =>
                    p.Name == property.Name && p.OwnerType == property.OwnerType && p.OwnerAssembly == property.OwnerAssembly);
                if (SelectedProperty is not null) EditedValue = draft;
            }
            if (Current(revision)) Status = "Snapshot updated from the existing view. Source is unchanged.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (Current(revision)) { IsCurrent = false; Status = exception.Message; }
        }
        finally
        {
            --_appearancePropertyOperations;
            if (Current(revision))
            {
                IsBusy = false;
                if (Status == "Updating snapshot without recreating the view…")
                    Status = "The preview selection changed during capture. Update the snapshot again.";
            }
            NotifySourceCommands();
            RefreshAppearanceSelection();
            RefreshBindingSourceState();
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        ShowBindingDiagnosticCommand.NotifyCanExecuteChanged();
        UpdateSnapshotCommand.NotifyCanExecuteChanged();
        NotifyInteractionState();
    }
}
