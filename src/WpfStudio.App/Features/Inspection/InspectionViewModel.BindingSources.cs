using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.BindingSources;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.App.Features.Inspection;

public sealed record InspectionBindingSourceNavigation(BindingSourceDeclaration Declaration, InspectionModuleCatalog Modules,
    Func<bool> IsCurrent, Func<CancellationToken, Task<BindingSourceResponse>> RevalidateAsync);

public sealed partial class InspectionViewModel : IBindingDeclarationOwner
{
    public BindingDeclarationsViewModel BindingDeclarations => field ??= new(this);

    private void RefreshBindingDeclarations(bool available = true)
    {
        BindingDeclarations.Replace(available ? ObservedDeclarations() : [], BindingDeclarations.Selected,
            available && Bindings.Any(property => property.Binding?.Sources?.Truncated == true),
            item => item.PropertyId == SelectedProperty?.PropertyId && item.Declaration.ParentExpressionId is null);
        if (!available) BindingDeclarations.ForgetRemoval();
    }
    private IEnumerable<BindingDeclarationItem> ObservedDeclarations()
    {
        foreach (var property in Bindings)
            if (property.Binding?.Sources is { } sources)
                foreach (var declaration in sources.Declarations)
                    yield return new(property.Name, property.OwnerType, property.OwnerAssembly, property.PropertyId, sources.BindingId, declaration);
    }
    private void RefreshBindingSourceState()
    {
        if (!IsConnected) BindingDeclarations.Status = "Connect to an application to navigate binding declarations.";
        else if (IsPaused) BindingDeclarations.ShowCondition(BindingSourceCondition.DebuggerPaused, "Debugger paused. Resume before verifying a binding declaration.");
        else if (_session?.Hello?.Capabilities.Contains("binding-source") != true)
            BindingDeclarations.Status = "This inspection agent does not provide binding declaration identities.";
        else if (!BindingDeclarations.IsOpening && BindingDeclarations.Items.Count == 0)
            BindingDeclarations.Status = "No binding declarations are available in this element observation.";
        else if (BindingDeclarations.Condition == BindingSourceCondition.DebuggerPaused)
            BindingDeclarations.Status = BindingDeclarations.Selected?.Description ?? "Select a binding declaration.";
        BindingDeclarations.ShowSourceCommand.NotifyCanExecuteChanged();
        ShowBindingIssueSourceCommand.NotifyCanExecuteChanged();
        RefreshBindingExplanation();
    }
    void IBindingDeclarationOwner.BindingDeclarationsChanged() => RefreshBindingSourceState();
    public bool CanShowBindingSource(BindingDeclarationItem? item) => !BindingDeclarations.IsOpening && !_disposed && IsConnected && !IsPaused
        && !IsPropertyOperationRunning && !IsSourceEditRunning && _host is not null && SelectedNode is not null
        && _session?.Hello?.Capabilities.Contains("binding-source") == true && _session.Hello.Capabilities.Contains("modules")
        && item?.PropertyId is not null && BindingDeclarations.Items.Any(current => current.SameIdentity(item));

    public async Task ShowBindingSourceAsync(BindingDeclarationItem? item)
    {
        if (!CanShowBindingSource(item) || item is null || _session is not { } session || _host is not { } host) return;
        BindingDeclarations.Selected = item;
        var generation = _generation; var epoch = BindingDeclarations.Epoch; var sourceEpoch = _sourceEpoch;
        string nodeId = SelectedNode!.Node.Id;
        bool CurrentBinding() => !_disposed && BindingDeclarations.IsOpening && generation == _generation && epoch == BindingDeclarations.Epoch
            && sourceEpoch == _sourceEpoch && ReferenceEquals(session, _session) && IsConnected && !IsPaused
            && !IsPropertyOperationRunning && !IsSourceEditRunning && SelectedNode?.Node.Id == nodeId
            && BindingDeclarations.Selected?.SameIdentity(item) == true && BindingDeclarations.Items.Any(current => current.SameIdentity(item));
        async Task<BindingSourceResponse> ValidateAsync(CancellationToken cancellationToken)
        {
            if (!CurrentBinding()) throw new OperationCanceledException("Binding selection or session changed.");
            var request = new BindingSourceRequest(_revision, nodeId, item.Property, item.BindingId, item.Declaration.ExpressionId,
                item.Declaration.DeclarationId, item.OwnerType, item.OwnerAssembly, item.PropertyId);
            var response = await session.GetBindingSourceAsync(request, cancellationToken);
            if (!CurrentBinding()) throw new OperationCanceledException("Binding selection or session changed.");
            item.RequireAcknowledgement(request, response);
            return response;
        }
        async Task<string?> OpenAsync(BindingSourceDeclaration declaration, CancellationToken token)
        {
            var modules = await session.GetModulesAsync(token);
            if (!CurrentBinding()) return null;
            return await host.NavigateBindingSourceAsync(new(declaration, modules, CurrentBinding, ValidateAsync), token);
        }
        await BindingDeclarations.OpenAsync(item, CurrentBinding, ValidateAsync, OpenAsync, _poll?.Token ?? default);
    }

    private bool CanShowBindingIssueSource(InspectionBindingIssue? issue) => !BindingDeclarations.IsOpening && CanSelectBindingIssue(issue)
        && !IsPropertyOperationRunning && !IsSourceEditRunning && _host is not null
        && _session?.Hello?.Capabilities.Contains("binding-source") == true;
    [RelayCommand(CanExecute = nameof(CanShowBindingIssueSource))]
    private async Task ShowBindingIssueSourceAsync(InspectionBindingIssue? issue)
    {
        if (!CanShowBindingIssueSource(issue) || issue is null) return;
        var generation = _generation;
        var selecting = SelectBindingIssueAsync(issue);
        var sourceEpoch = _sourceEpoch;
        var userSelection = BindingDeclarations.UserSelection;
        await selecting;
        if (generation != _generation || sourceEpoch != _sourceEpoch || userSelection != BindingDeclarations.UserSelection
            || SelectedNode?.Node.Id != issue.NodeId || !IsConnected || IsPaused) return;
        var roots = BindingDeclarations.Items.Where(item => item.BindingId == issue.Id && item.Declaration.ParentExpressionId is null).ToArray();
        if (roots.Length != 1)
        { BindingDeclarations.Status = "That issue's binding expression is no longer present in the selected element. Its replacement is not the same declaration."; return; }
        BindingDeclarations.Selected = roots[0];
        await ShowBindingSourceAsync(roots[0]);
    }
}
