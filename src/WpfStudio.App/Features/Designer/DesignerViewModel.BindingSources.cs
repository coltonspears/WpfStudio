using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.BindingSources;
using WpfStudio.Core.Documents;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Designer;

public sealed record PreviewBindingSourceNavigation(DocumentState Document, string Text, BindingSourceDeclaration Declaration,
    Func<bool> IsCurrent, Func<CancellationToken, Task<BindingSourceResponse>> RevalidateAsync);

public sealed partial class DesignerViewModel
{
    public event Func<PreviewBindingSourceNavigation, CancellationToken, Task<string>>? BindingSourceRequested;
    public ObservableCollection<BindingDeclarationItem> BindingDeclarations { get; } = [];
    [ObservableProperty] public partial BindingDeclarationItem? SelectedBindingDeclaration { get; set; }
    [ObservableProperty] public partial string BindingSourceStatus { get; set; } = "Select an element to inspect its binding declarations.";
    [ObservableProperty] public partial string BindingDeclarationCoverage { get; set; } = "";
    private long _bindingSourceEpoch;
    private bool _updatingBindingDeclarations, _openingBindingSource, _bindingSelectionRemoved;

    private void RefreshBindingDeclarations()
    {
        var previous = IsCurrent && _bindingObservedNodeId is not null ? SelectedBindingDeclaration : null;
        if (!IsCurrent || _bindingObservedNodeId is null) _bindingSelectionRemoved = false;
        BindingDeclarationCoverage = IsCurrent && Bindings.Any(property => property.BindingSources?.Truncated == true)
            ? "Declaration inspection reached its limit; some bindings or composite children were omitted. Listed declarations can still be verified." : "";
        _updatingBindingDeclarations = true;
        try
        {
            BindingDeclarations.Clear();
            if (IsCurrent)
                foreach (var property in Bindings)
                    if (property.BindingSources is { } sources && property.OwnerType is { } owner && property.OwnerAssembly is { } assembly)
                        foreach (var declaration in sources.Declarations)
                            BindingDeclarations.Add(new(property.Name, owner, assembly, null, sources.BindingId, declaration));
            var retained = BindingDeclarations.FirstOrDefault(item => item.SameIdentity(previous));
            if (previous is not null && retained is null) _bindingSelectionRemoved = true;
            SelectedBindingDeclaration = retained ?? (_bindingSelectionRemoved ? null :
                BindingDeclarations.FirstOrDefault(item => SelectedProperty is { } selected && item.Property == selected.Name && item.OwnerType == selected.OwnerType
                    && item.OwnerAssembly == selected.OwnerAssembly && item.Declaration.ParentExpressionId is null)
                ?? BindingDeclarations.FirstOrDefault());
        }
        finally { _updatingBindingDeclarations = false; }
        if (!(SelectedBindingDeclaration?.SameIdentity(previous) ?? previous is null)) BindingSourceSelectionChanged();
        RefreshBindingSourceState();
    }
    partial void OnSelectedBindingDeclarationChanged(BindingDeclarationItem? value)
    {
        if (!_updatingBindingDeclarations)
        {
            ++_bindingDiagnosticSelection;
            if (value is not null) _bindingSelectionRemoved = false;
            BindingSourceSelectionChanged();
        }
    }
    private void BindingSourceSelectionChanged()
    {
        ++_bindingSourceEpoch;
        BindingSourceStatus = SelectedBindingDeclaration?.Description ?? "No binding declaration was observed for this element.";
        RefreshBindingSourceState();
    }
    private void BindingPropertySelectionChanged()
    {
        ++_bindingDiagnosticSelection;
        ++_bindingSourceEpoch;
        if (SelectedProperty?.BindingSources is { } sources)
            SelectedBindingDeclaration = BindingDeclarations.FirstOrDefault(item => item.BindingId == sources.BindingId && item.Declaration.ParentExpressionId is null);
        RefreshBindingSourceState();
    }
    private void RefreshBindingSourceState()
    {
        if (!IsCurrent) BindingSourceStatus = "Refresh the preview to inspect binding declarations.";
        else if (IsCompiledPreview) BindingSourceStatus = "Compiled preview binding source is not verified. Launch with Live XAML inspection to verify built declarations.";
        else if (!_openingBindingSource && BindingDeclarations.Count == 0)
            BindingSourceStatus = "No binding declaration source was observed for this preview element.";
        ShowBindingSourceCommand.NotifyCanExecuteChanged();
        RefreshBindingExplanation();
    }
    private bool CanShowBindingSource(BindingDeclarationItem? item) => !_openingBindingSource && !_disposed && IsCurrent && !IsCompiledPreview
        && _appearancePropertyOperations == 0 && _document is not null && _document.Version == _sourceVersion
        && _inspectedNodeId == SelectedNode?.Node.Id && BindingSourceRequested is not null && item is not null
        && BindingDeclarations.Any(current => current.SameIdentity(item));
    [RelayCommand(CanExecute = nameof(CanShowBindingSource))]
    private async Task ShowBindingSourceAsync(BindingDeclarationItem? item)
    {
        if (!CanShowBindingSource(item) || item is null || BindingSourceRequested is not { } navigate) return;
        SelectedBindingDeclaration = item;
        var document = _document!; var text = document.Content; var version = document.Version;
        var revision = _revision; var selection = _selectionRevision; var epoch = _bindingSourceEpoch;
        var request = new BindingSourceRequest(revision, SelectedNode!.Node.Id, item.Property, item.BindingId,
            item.Declaration.ExpressionId, item.Declaration.DeclarationId, item.OwnerType, item.OwnerAssembly);
        bool CurrentBinding() => _openingBindingSource && Current(revision) && IsCurrent && !IsCompiledPreview && _appearancePropertyOperations == 0
            && selection == _selectionRevision && epoch == _bindingSourceEpoch && ReferenceEquals(document, _document)
            && document.Version == version && document.Content == text && SelectedNode?.Node.Id == request.NodeId
            && SelectedBindingDeclaration?.SameIdentity(item) == true && IsScenarioConfigurationCurrent();
        async Task<BindingSourceResponse> ValidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CurrentBinding()) throw new OperationCanceledException("The preview binding selection changed.");
            // Selection changes must not cancel the transport and terminate the preview host.
            var response = await _client.GetBindingSourceAsync(request, _lifetime.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CurrentBinding()) throw new OperationCanceledException("The preview binding selection changed.");
            if (response.Request != request || response.Available && (response.Declaration is not { } declaration ||
                !BindingDeclarationItem.SameDeclaration(item.Declaration, declaration)))
                throw new InvalidOperationException("The binding source acknowledgement did not match the observed declaration.");
            return response;
        }
        _openingBindingSource = true; BindingSourceStatus = "Verifying binding declaration source…"; RefreshBindingSourceState();
        try
        {
            var response = await ValidateAsync(_lifetime.Token);
            if (!response.Available || response.Declaration?.Source is null)
            { BindingSourceStatus = response.Status ?? response.Declaration?.UnavailableReason ?? item.Description; return; }
            var status = await navigate(new(document, text, response.Declaration, CurrentBinding, ValidateAsync), _lifetime.Token);
            if (CurrentBinding()) BindingSourceStatus = status;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (CurrentBinding()) BindingSourceStatus = "Binding source unavailable: " + exception.Message; }
        finally
        {
            if (!CurrentBinding() && BindingSourceStatus == "Verifying binding declaration source…")
                BindingSourceStatus = "Binding source navigation cancelled because its context changed.";
            _openingBindingSource = false; RefreshBindingSourceState();
        }
    }
}
