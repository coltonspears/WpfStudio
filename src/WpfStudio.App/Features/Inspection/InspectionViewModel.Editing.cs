using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Inspection;

public sealed partial class InspectionViewModel
{
    private InspectionProperty? _editBase;
    private string? _editNodeId;
    private InspectionPropertyEdit? _pendingEdit;
    private bool _updatingProperties, _seedingEdit, _editDirty;
    private long _editInputStamp;
    [ObservableProperty] public partial string EditedValue { get; set; } = "";
    [ObservableProperty] public partial bool EditAsNull { get; set; }
    [ObservableProperty] public partial bool IsPropertyOperationRunning { get; set; }
    [ObservableProperty] public partial string EditStatus { get; set; } = "";
    public bool HasPropertySelection => SelectedProperty is not null;
    public bool IsEditStale => _editBase?.EditToken is { } token && token != SelectedProperty?.EditToken;
    public string PropertyEditHint => _pendingEdit is not null
        ? IsConnected ? "A previous edit has an unknown outcome. Check its outcome, or disconnect to restore owned overrides."
            : "The edit outcome was not confirmed before disconnect. Restoring owned overrides waits for the application's dispatcher to respond."
        : IsEditStale ? "The property changed in the application. Reload its value before trying another edit."
        : SelectedProperty?.EditUnavailableReason ?? (SelectedProperty is { IsOverridden: true }
            ? "Temporary override active. Reset restores the original property or binding."
            : "Temporary edits can run application property callbacks. Reset or disconnect restores inspector-owned overrides.");
    private bool CanEditProperties => IsConnected && !IsPaused &&
        _session?.Hello?.Capabilities.Contains("property-edit") == true && !IsPropertyOperationRunning && !IsSourceEditRunning && _pendingEdit is null;
    private bool CanApplyProperty() => CanEditProperties && SelectedProperty?.CanEdit == true && !IsEditStale &&
        _editBase?.PropertyId is not null && _editBase.EditToken is not null && _editNodeId == SelectedNode?.Node.Id;
    private bool CanResetProperty() => CanEditProperties && SelectedProperty is { IsOverridden: true, PropertyId: not null, EditToken: not null };
    private bool CanReloadPropertyValue() => SelectedProperty is not null && !IsPropertyOperationRunning;
    private bool CanCheckEditOutcome() => _pendingEdit is not null && IsConnected && !IsPaused && !IsPropertyOperationRunning && !IsSourceEditRunning;

    private void ResetPropertyEditing()
    {
        _pendingEdit = null; _editBase = null; _editNodeId = null;
        EditStatus = ""; _editDirty = false;
        SeedPropertyEdit(SelectedProperty);
        ResetSourceEditing();
    }

    private void UpdatePropertyEditingState()
    {
        ApplyPropertyCommand.NotifyCanExecuteChanged(); ResetPropertyCommand.NotifyCanExecuteChanged();
        ValidatePropertyCommand.NotifyCanExecuteChanged(); ReloadPropertyValueCommand.NotifyCanExecuteChanged();
        CheckEditOutcomeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasPropertySelection)); OnPropertyChanged(nameof(IsEditStale)); OnPropertyChanged(nameof(PropertyEditHint));
        UpdateSourceEditingState();
    }

    partial void OnSelectedPropertyChanged(InspectionProperty? value)
    {
        if (_updatingProperties) return;
        SeedPropertyEdit(value);
        UpdatePropertyEditingState();
        RefreshAppearanceSelection();
        BindingPropertySelectionChanged();
    }

    private void RefreshPropertyEditSelection()
    {
        if (!_editDirty || SelectedProperty?.PropertyId != _editBase?.PropertyId || _editNodeId != SelectedNode?.Node.Id)
            SeedPropertyEdit(SelectedProperty);
        UpdatePropertyEditingState();
        RefreshAppearanceSelection();
    }

    private void SeedPropertyEdit(InspectionProperty? value)
    {
        _seedingEdit = true;
        try
        {
            _editBase = value; _editNodeId = SelectedNode?.Node.Id;
            EditedValue = value?.EditableValue ?? ""; EditAsNull = value?.IsNull == true;
            _editDirty = false; _editInputStamp++;
        }
        finally { _seedingEdit = false; }
    }

    private void EditInputChanged()
    {
        if (_seedingEdit) return;
        _editInputStamp++;
        _editDirty = EditedValue != (_editBase?.EditableValue ?? "") || EditAsNull != (_editBase?.IsNull == true);
        UpdatePropertyEditingState();
    }
    partial void OnEditedValueChanged(string value) => EditInputChanged();
    partial void OnEditAsNullChanged(bool value) => EditInputChanged();
    partial void OnIsPropertyOperationRunningChanged(bool value)
    {
        UpdatePropertyEditingState();
        AppearanceOperationChanged(value);
        if (value) { ++_bindingSourceEpoch; ++_bindingOperationEpoch; ClearBindingExplanation("Property operation in progress. Waiting for a new binding observation…"); }
        RefreshBindingSourceState();
    }

    private InspectionPropertyEdit CreatePropertyEdit(bool reset)
    {
        var property = (reset ? SelectedProperty : _editBase)!;
        return new(Guid.NewGuid().ToString("N"), _revision, SelectedNode!.Node.Id,
            property.PropertyId!, property.EditToken!, reset ? null : EditedValue, !reset && EditAsNull, reset);
    }

    [RelayCommand(CanExecute = nameof(CanReloadPropertyValue))]
    private void ReloadPropertyValue()
    {
        SeedPropertyEdit(SelectedProperty); EditStatus = "Editor reloaded from the latest property observation.";
        UpdatePropertyEditingState();
    }

    [RelayCommand(CanExecute = nameof(CanApplyProperty))]
    private async Task ValidatePropertyAsync()
    {
        if (!CanApplyProperty() || _session is not { } session) return;
        var generation = _generation; var stamp = _editInputStamp;
        IsPropertyOperationRunning = true;
        try
        {
            var result = await session.ValidatePropertyAsync(CreatePropertyEdit(false), _poll?.Token ?? default);
            if (generation == _generation && stamp == _editInputStamp)
                EditStatus = result.Success ? "Value is valid. The application has not been changed." : result.Error ?? "The value is not supported.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (generation == _generation && stamp == _editInputStamp) EditStatus = exception.Message; }
        finally { if (generation == _generation) IsPropertyOperationRunning = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApplyProperty))]
    private Task ApplyPropertyAsync() => ChangePropertyAsync(reset: false);
    [RelayCommand(CanExecute = nameof(CanResetProperty))]
    private Task ResetPropertyAsync() => ChangePropertyAsync(reset: true);

    private async Task ChangePropertyAsync(bool reset)
    {
        if (!(reset ? CanResetProperty() : CanApplyProperty()) || _session is not { } session) return;
        var request = CreatePropertyEdit(reset);
        var generation = _generation; var stamp = _editInputStamp;
        ++_selection;
        ClearLayout("Property changing. Refreshing layout after the edit completes…");
        IsPropertyOperationRunning = true;
        InspectionPropertyEditResult? result = null;
        try
        {
            result = await session.SetPropertyAsync(request, _poll?.Token ?? default);
            if (generation != _generation) return;
            ApplyEditOutcome(request, result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation != _generation) return;
            _pendingEdit = request;
            EditStatus = "The edit outcome could not be confirmed. " + exception.Message;
        }
        finally { if (generation == _generation) IsPropertyOperationRunning = false; }
        if (generation != _generation || result?.Outcome is not ("Applied" or "Reset")) return;
        await RefreshAfterKnownEditAsync(request, result, stamp, generation);
    }

    private void ApplyEditOutcome(InspectionPropertyEdit request, InspectionPropertyEditResult result)
    {
        _pendingEdit = result.Outcome == "Unknown" ? request : null;
        EditStatus = result.Outcome switch
        {
            "Applied" => "Temporary override applied. The source file was not changed.",
            "Reset" => "The original property or binding was restored.",
            "Conflict" => result.Error ?? "The application replaced this override. Its new value was preserved.",
            "Unknown" => result.Error ?? "The edit is still unconfirmed. Check its outcome; do not resend it.",
            _ => result.Error ?? "The edit was rejected."
        };
        UpdatePropertyEditingState();
    }

    private async Task RefreshAfterKnownEditAsync(InspectionPropertyEdit request, InspectionPropertyEditResult result, long stamp, long generation)
    {
        bool sameProperty = SelectedNode?.Node.Id == request.NodeId && SelectedProperty?.PropertyId == request.PropertyId;
        bool retainDraft = sameProperty && stamp != _editInputStamp;
        if (sameProperty && !retainDraft) _editDirty = false;
        await RefreshAsync();
        if (generation != _generation || !retainDraft || SelectedNode?.Node.Id != request.NodeId || SelectedProperty?.PropertyId != request.PropertyId) return;
        var applied = result.Element?.Properties.FirstOrDefault(property => property.PropertyId == request.PropertyId);
        if (applied?.EditToken is { } token && token == SelectedProperty.EditToken)
        {
            _editBase = SelectedProperty; _editNodeId = request.NodeId;
            _editDirty = EditedValue != (SelectedProperty.EditableValue ?? "") || EditAsNull != SelectedProperty.IsNull;
        }
        UpdatePropertyEditingState();
    }

    [RelayCommand(CanExecute = nameof(CanCheckEditOutcome))]
    private async Task CheckEditOutcomeAsync()
    {
        if (!CanCheckEditOutcome() || _pendingEdit is not { } request || _session is not { } session) return;
        var generation = _generation; var stamp = _editInputStamp;
        InspectionPropertyEditResult? result = null;
        IsPropertyOperationRunning = true;
        try
        {
            result = await session.GetEditStatusAsync(new(request.OperationId), _poll?.Token ?? default);
            if (generation == _generation) ApplyEditOutcome(request, result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (generation == _generation) EditStatus = "Could not confirm the edit outcome. " + exception.Message; }
        finally { if (generation == _generation) IsPropertyOperationRunning = false; }
        if (generation == _generation && result?.Outcome is ("Applied" or "Reset"))
            await RefreshAfterKnownEditAsync(request, result, stamp, generation);
    }
}
