using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.BindingSources;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.App.Features.Inspection;

public sealed record InspectionBindingSourceNavigation(BindingSourceDeclaration Declaration, InspectionModuleCatalog Modules,
    Func<bool> IsCurrent, Func<CancellationToken, Task<BindingSourceResponse>> RevalidateAsync);

public sealed partial class InspectionViewModel
{
    public event Func<InspectionBindingSourceNavigation, CancellationToken, Task<string>>? BindingSourceRequested;
    public ObservableCollection<BindingDeclarationItem> BindingDeclarations { get; } = [];
    [ObservableProperty] public partial BindingDeclarationItem? SelectedBindingDeclaration { get; set; }
    [ObservableProperty] public partial string BindingSourceStatus { get; set; } = "Select an element to inspect its binding declarations.";
    [ObservableProperty] public partial string BindingDeclarationCoverage { get; set; } = "";
    private long _bindingSourceEpoch, _bindingSourceUserSelection;
    private bool _updatingBindingDeclarations, _openingBindingSource, _bindingSelectionRemoved;

    private void RefreshBindingDeclarations(bool available = true)
    {
        var previous = SelectedBindingDeclaration;
        BindingDeclarationCoverage = available && Bindings.Any(property => property.Binding?.Sources?.Truncated == true)
            ? "Declaration inspection reached its limit; some bindings or composite children were omitted. Listed declarations can still be verified." : "";
        _updatingBindingDeclarations = true;
        try
        {
            BindingDeclarations.Clear();
            if (available)
                foreach (var property in Bindings)
                    if (property.Binding?.Sources is { } sources)
                        foreach (var declaration in sources.Declarations)
                            BindingDeclarations.Add(new(property.Name, property.OwnerType, property.OwnerAssembly, property.PropertyId, sources.BindingId, declaration));
            var retained = BindingDeclarations.FirstOrDefault(item => item.SameIdentity(previous));
            if (previous is not null && retained is null) _bindingSelectionRemoved = true;
            if (!available) _bindingSelectionRemoved = false;
            SelectedBindingDeclaration = retained ?? (_bindingSelectionRemoved ? null :
                BindingDeclarations.FirstOrDefault(item => item.PropertyId == SelectedProperty?.PropertyId && item.Declaration.ParentExpressionId is null)
                ?? BindingDeclarations.FirstOrDefault());
        }
        finally { _updatingBindingDeclarations = false; }
        if (!(SelectedBindingDeclaration?.SameIdentity(previous) ?? previous is null)) BindingSourceSelectionChanged();
        RefreshBindingSourceState();
    }

    partial void OnSelectedBindingDeclarationChanged(BindingDeclarationItem? value)
    { if (!_updatingBindingDeclarations) { if (value is not null) _bindingSelectionRemoved = false; ++_bindingSourceUserSelection; BindingSourceSelectionChanged(); } }
    private void BindingSourceSelectionChanged()
    {
        ++_bindingSourceEpoch;
        BindingSourceStatus = SelectedBindingDeclaration?.Description ?? "No binding declaration was observed for this element.";
        RefreshBindingSourceState();
    }
    private void BindingPropertySelectionChanged()
    {
        ++_bindingSourceUserSelection;
        ++_bindingSourceEpoch;
        if (SelectedProperty?.Binding?.Sources is { } sources)
            SelectedBindingDeclaration = BindingDeclarations.FirstOrDefault(item => item.BindingId == sources.BindingId && item.Declaration.ParentExpressionId is null);
        RefreshBindingSourceState();
    }
    private void RefreshBindingSourceState()
    {
        if (!IsConnected) BindingSourceStatus = "Connect to an application to navigate binding declarations.";
        else if (IsPaused) BindingSourceStatus = "Debugger paused. Resume before verifying a binding declaration.";
        else if (_session?.Hello?.Capabilities.Contains("binding-source") != true)
            BindingSourceStatus = "This inspection agent does not provide binding declaration identities.";
        else if (!_openingBindingSource && BindingDeclarations.Count == 0)
            BindingSourceStatus = "No binding declarations are available in this element observation.";
        else if (BindingSourceStatus == "Debugger paused. Resume before verifying a binding declaration.")
            BindingSourceStatus = SelectedBindingDeclaration?.Description ?? "Select a binding declaration.";
        ShowBindingSourceCommand.NotifyCanExecuteChanged();
        ShowBindingIssueSourceCommand.NotifyCanExecuteChanged();
        RefreshBindingExplanation();
    }
    private bool CanShowBindingSource(BindingDeclarationItem? item) => !_openingBindingSource && !_disposed && IsConnected && !IsPaused
        && !IsPropertyOperationRunning && !IsSourceEditRunning && BindingSourceRequested is not null && SelectedNode is not null
        && _session?.Hello?.Capabilities.Contains("binding-source") == true && _session.Hello.Capabilities.Contains("modules")
        && item?.PropertyId is not null && BindingDeclarations.Any(current => current.SameIdentity(item));

    [RelayCommand(CanExecute = nameof(CanShowBindingSource))]
    private async Task ShowBindingSourceAsync(BindingDeclarationItem? item)
    {
        if (!CanShowBindingSource(item) || item is null || _session is not { } session || BindingSourceRequested is not { } navigate) return;
        SelectedBindingDeclaration = item;
        var generation = _generation; var epoch = _bindingSourceEpoch; var sourceEpoch = _sourceEpoch;
        string nodeId = SelectedNode!.Node.Id;
        bool CurrentBinding() => !_disposed && _openingBindingSource && generation == _generation && epoch == _bindingSourceEpoch
            && sourceEpoch == _sourceEpoch && ReferenceEquals(session, _session) && IsConnected && !IsPaused
            && !IsPropertyOperationRunning && !IsSourceEditRunning && SelectedNode?.Node.Id == nodeId
            && SelectedBindingDeclaration?.SameIdentity(item) == true && BindingDeclarations.Any(current => current.SameIdentity(item));
        async Task<BindingSourceResponse> ValidateAsync(CancellationToken cancellationToken)
        {
            if (!CurrentBinding()) throw new OperationCanceledException("Binding selection or session changed.");
            var request = new BindingSourceRequest(_revision, nodeId, item.Property, item.BindingId, item.Declaration.ExpressionId,
                item.Declaration.DeclarationId, item.OwnerType, item.OwnerAssembly, item.PropertyId);
            var response = await session.GetBindingSourceAsync(request, cancellationToken);
            if (!CurrentBinding()) throw new OperationCanceledException("Binding selection or session changed.");
            if (response.Request != request || response.Available && (response.Declaration is not { } declaration ||
                !BindingDeclarationItem.SameDeclaration(item.Declaration, declaration)))
                throw new InvalidOperationException("The binding source acknowledgement did not match the observed declaration.");
            return response;
        }
        _openingBindingSource = true; BindingSourceStatus = "Verifying binding declaration source…"; RefreshBindingSourceState();
        try
        {
            var token = _poll?.Token ?? default;
            var response = await ValidateAsync(token);
            if (!response.Available || response.Declaration?.Source is null)
            { BindingSourceStatus = response.Status ?? response.Declaration?.UnavailableReason ?? item.Description; return; }
            var modules = await session.GetModulesAsync(token);
            if (!CurrentBinding()) return;
            var status = await navigate(new(response.Declaration, modules, CurrentBinding, ValidateAsync), token);
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

    private bool CanShowBindingIssueSource(InspectionBindingIssue? issue) => !_openingBindingSource && CanSelectBindingIssue(issue)
        && !IsPropertyOperationRunning && !IsSourceEditRunning && BindingSourceRequested is not null
        && _session?.Hello?.Capabilities.Contains("binding-source") == true;
    [RelayCommand(CanExecute = nameof(CanShowBindingIssueSource))]
    private async Task ShowBindingIssueSourceAsync(InspectionBindingIssue? issue)
    {
        if (!CanShowBindingIssueSource(issue) || issue is null) return;
        var generation = _generation;
        var selecting = SelectBindingIssueAsync(issue);
        var sourceEpoch = _sourceEpoch;
        var userSelection = _bindingSourceUserSelection;
        await selecting;
        if (generation != _generation || sourceEpoch != _sourceEpoch || userSelection != _bindingSourceUserSelection
            || SelectedNode?.Node.Id != issue.NodeId || !IsConnected || IsPaused) return;
        var roots = BindingDeclarations.Where(item => item.BindingId == issue.Id && item.Declaration.ParentExpressionId is null).ToArray();
        if (roots.Length != 1)
        { BindingSourceStatus = "That issue's binding expression is no longer present in the selected element. Its replacement is not the same declaration."; return; }
        SelectedBindingDeclaration = roots[0];
        await ShowBindingSourceAsync(roots[0]);
    }
}
