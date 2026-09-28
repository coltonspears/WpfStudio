using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Inspection;

public sealed record InspectionSourceEditRequest(InspectionNode Node, InspectionProperty Property,
    InspectionModuleCatalog Modules, string? Value, bool IsNull, bool Remove, Func<bool> IsCurrent,
    Func<bool, CancellationToken, Task<InspectionSourcePropertyResult>> ValidateAsync,
    Func<CancellationToken, Task<InspectionModuleCatalog>> RefreshModulesAsync);
public sealed record InspectionSourceEditOutcome(bool Applied, string Status);

public sealed partial class InspectionViewModel
{
    public event Func<InspectionSourceEditRequest, CancellationToken, Task<InspectionSourceEditOutcome>>? SourceEditRequested;

    private sealed record SourceEditContext(string? NodeId, string? PropertyId, string? Token, string Value, bool IsNull);
    private SourceEditContext? _sourceEditContext;
    private long _sourceEditEpoch, _sourceEditOperation;
    [ObservableProperty] public partial bool IsSourceEditRunning { get; set; }
    [ObservableProperty] public partial string SourceEditStatus { get; set; } = "";
    public string SourcePropertyEditHint => _pendingEdit is not null
        ? "Resolve the unknown temporary edit outcome before preparing a source edit."
        : SelectedNode?.Node.Source is null ? "WPF supplied no source location for this element."
        : SelectedProperty?.SourceUnavailableReason ?? (IsSourcePropertyStale
            ? "The property changed in the application. Reload its value before preparing a source edit."
            : "Review the XAML diff before applying. Binding/resource replacements and shared template changes are shown in the diff; this does not set the live property.");

    private bool IsSourcePropertyStale => _editBase?.SourceEditToken != SelectedProperty?.SourceEditToken;
    private bool HasSourcePropertyContext => !_disposed && IsConnected && !IsPaused && _pendingEdit is null
        && SelectedNode?.Node.Source is not null && SelectedProperty is
            { CanWriteSource: true, PropertyId: not null, SourceEditToken: not null } property
        && Properties.Contains(property) && _editBase?.PropertyId == property.PropertyId
        && !IsSourcePropertyStale && _editNodeId == SelectedNode.Node.Id
        && _session?.Hello is { } hello && hello.Capabilities.Contains("modules")
        && hello.Capabilities.Contains("source-property-validation");
    private bool CanWritePropertySource() => HasSourcePropertyContext && !IsSourceEditRunning
        && !IsPropertyOperationRunning && SourceEditRequested is not null;

    private void ResetSourceEditing()
    {
        _sourceEditOperation++; _sourceEditEpoch++; _sourceEditContext = null;
        SourceEditStatus = ""; IsSourceEditRunning = false;
    }

    private void UpdateSourceEditingState()
    {
        var context = new SourceEditContext(SelectedNode?.Node.Id, SelectedProperty?.PropertyId,
            SelectedProperty?.SourceEditToken, EditedValue, EditAsNull);
        // Auto-refresh can replace every record and reseed an unchanged draft.
        // Only material context changes invalidate a pending source review.
        // Each intermediate user change is observed, so changing away and back
        // cannot revive an obsolete request.
        if (_sourceEditContext != context)
        {
            _sourceEditContext = context; _sourceEditEpoch++;
            SourceEditStatus = "";
        }
        if (_session is null || !IsConnected)
        {
            _sourceEditOperation++;
            if (SourceEditStatus == "Preparing a verified XAML edit…") SourceEditStatus = "XAML edit cancelled because the inspector disconnected.";
            IsSourceEditRunning = false;
        }
        WritePropertyToSourceCommand.NotifyCanExecuteChanged();
        RemovePropertyFromSourceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SourcePropertyEditHint));
    }

    partial void OnIsSourceEditRunningChanged(bool value)
    {
        UpdatePropertyEditingState();
        AppearanceOperationChanged(value);
        if (value) { ++_bindingSourceEpoch; ++_bindingOperationEpoch; ClearBindingExplanation("Source review in progress. Refresh to observe bindings after the review."); }
        RefreshBindingSourceState();
    }

    [RelayCommand(CanExecute = nameof(CanWritePropertySource))]
    private Task WritePropertyToSourceAsync() => EditPropertySourceAsync(remove: false);

    [RelayCommand(CanExecute = nameof(CanWritePropertySource))]
    private Task RemovePropertyFromSourceAsync() => EditPropertySourceAsync(remove: true);

    private async Task EditPropertySourceAsync(bool remove)
    {
        if (!CanWritePropertySource() || _session is not { } session || SourceEditRequested is not { } preview) return;
        UpdateSourceEditingState();
        var generation = _generation; var epoch = _sourceEditEpoch; var sourceEpoch = _sourceEpoch;
        var node = SelectedNode!.Node; var property = SelectedProperty!;
        string value = EditedValue; bool isNull = EditAsNull;
        long operation = ++_sourceEditOperation;
        var lifetime = _poll?.Token ?? default;
        bool Current() => generation == _generation && epoch == _sourceEditEpoch && sourceEpoch == _sourceEpoch
            && operation == _sourceEditOperation && IsSourceEditRunning && ReferenceEquals(session, _session) && HasSourcePropertyContext
            && SameSource(node, SelectedNode?.Node) && SelectedProperty?.PropertyId == property.PropertyId
            && SelectedProperty?.SourceEditToken == property.SourceEditToken && EditedValue == value && EditAsNull == isNull;

        async Task<InspectionSourcePropertyResult> ValidateAsync(bool verifyOnly, CancellationToken cancellationToken)
        {
            if (!Current()) throw new OperationCanceledException("The source edit context changed.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            var request = new InspectionSourcePropertyRequest(_revision, node.Id, property.PropertyId!, property.SourceEditToken!,
                remove || verifyOnly ? null : value, !remove && !verifyOnly && isNull, remove, verifyOnly);
            var result = await session.ValidateSourcePropertyAsync(request, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException("The source edit context changed.");
            if (result.Revision != request.Revision || result.NodeId != request.NodeId || result.PropertyId != request.PropertyId
                || result.SourceEditToken != request.SourceEditToken || result.Success && (result.Property is null
                    || !remove && !verifyOnly && (result.IsNull != isNull || !isNull && result.Literal is null)))
                throw new InvalidDataException("The source validation acknowledgement does not match this property or proposed value.");
            return result;
        }

        async Task<InspectionModuleCatalog> RefreshModulesAsync(CancellationToken cancellationToken)
        {
            if (!Current()) throw new OperationCanceledException("The source edit context changed.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            var modules = await session.GetModulesAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException("The source edit context changed.");
            return modules;
        }

        IsSourceEditRunning = true;
        SourceEditStatus = "Preparing a verified XAML edit…";
        try
        {
            var modules = await RefreshModulesAsync(lifetime);
            var result = await preview(new(node, property, modules, remove ? null : value, !remove && isNull,
                remove, Current, ValidateAsync, RefreshModulesAsync), lifetime);
            if (Current()) SourceEditStatus = result.Status;
        }
        catch (OperationCanceledException)
        {
            if (Current()) SourceEditStatus = "XAML edit cancelled.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (Current()) SourceEditStatus = "Source edit unavailable: " + exception.Message;
        }
        finally
        {
            if (generation == _generation && operation == _sourceEditOperation)
            {
                if (!Current() && SourceEditStatus == "Preparing a verified XAML edit…")
                    SourceEditStatus = "XAML edit cancelled because the inspection context changed.";
                IsSourceEditRunning = false;
                UpdateSourceEditingState();
            }
        }
    }
}
