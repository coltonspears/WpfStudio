using WpfStudio.App.Features.BindingSources;
using WpfStudio.Core.Documents;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Designer;

public sealed record PreviewBindingSourceNavigation(DocumentState Document, string Text, BindingSourceDeclaration Declaration,
    Func<bool> IsCurrent, Func<CancellationToken, Task<BindingSourceResponse>> RevalidateAsync);

public sealed partial class DesignerViewModel : IBindingDeclarationOwner
{
    public BindingDeclarationsViewModel BindingDeclarations => field ??= new(this);

    private void RefreshBindingDeclarations()
    {
        var observed = IsCurrent && _bindingObservedNodeId is not null;
        if (!observed) BindingDeclarations.ForgetRemoval();
        BindingDeclarations.Replace(IsCurrent ? ObservedDeclarations() : [], observed ? BindingDeclarations.Selected : null,
            IsCurrent && Bindings.Any(property => property.BindingSources?.Truncated == true),
            item => SelectedProperty is { } selected && item.Property == selected.Name && item.OwnerType == selected.OwnerType
                && item.OwnerAssembly == selected.OwnerAssembly && item.Declaration.ParentExpressionId is null);
    }
    private IEnumerable<BindingDeclarationItem> ObservedDeclarations()
    {
        foreach (var property in Bindings)
            if (property.BindingSources is { } sources && property.OwnerType is { } owner && property.OwnerAssembly is { } assembly)
                foreach (var declaration in sources.Declarations)
                    yield return new(property.Name, owner, assembly, null, sources.BindingId, declaration);
    }
    private void RefreshBindingSourceState()
    {
        if (!IsCurrent) BindingDeclarations.Status = "Refresh the preview to inspect binding declarations.";
        else if (IsCompiledPreview) BindingDeclarations.Status = "Compiled preview binding source is not verified. Launch with Live XAML inspection to verify built declarations.";
        else if (!BindingDeclarations.IsOpening && BindingDeclarations.Items.Count == 0)
            BindingDeclarations.Status = "No binding declaration source was observed for this preview element.";
        BindingDeclarations.ShowSourceCommand.NotifyCanExecuteChanged();
        RefreshBindingExplanation();
    }
    void IBindingDeclarationOwner.BindingDeclarationsChanged() => RefreshBindingSourceState();
    public bool CanShowBindingSource(BindingDeclarationItem? item) => !BindingDeclarations.IsOpening && !_disposed && IsCurrent && !IsCompiledPreview
        && _appearancePropertyOperations == 0 && _document is not null && _document.Version == _sourceVersion
        && _inspectedNodeId == SelectedNode?.Node.Id && _sourceHost is not null && item is not null
        && BindingDeclarations.Items.Any(current => current.SameIdentity(item));
    public async Task ShowBindingSourceAsync(BindingDeclarationItem? item)
    {
        if (!CanShowBindingSource(item) || item is null || _sourceHost is not { } host) return;
        BindingDeclarations.Selected = item;
        var document = _document!; var text = document.Content; var version = document.Version;
        var revision = _revision; var selection = _selectionRevision; var epoch = BindingDeclarations.Epoch;
        var request = new BindingSourceRequest(revision, SelectedNode!.Node.Id, item.Property, item.BindingId,
            item.Declaration.ExpressionId, item.Declaration.DeclarationId, item.OwnerType, item.OwnerAssembly);
        bool CurrentBinding() => BindingDeclarations.IsOpening && Current(revision) && IsCurrent && !IsCompiledPreview && _appearancePropertyOperations == 0
            && selection == _selectionRevision && epoch == BindingDeclarations.Epoch && ReferenceEquals(document, _document)
            && document.Version == version && document.Content == text && SelectedNode?.Node.Id == request.NodeId
            && BindingDeclarations.Selected?.SameIdentity(item) == true && IsScenarioConfigurationCurrent();
        async Task<BindingSourceResponse> ValidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CurrentBinding()) throw new OperationCanceledException("The preview binding selection changed.");
            // Selection changes must not cancel the transport and terminate the preview host.
            var response = await _client.GetBindingSourceAsync(request, _lifetime.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CurrentBinding()) throw new OperationCanceledException("The preview binding selection changed.");
            item.RequireAcknowledgement(request, response);
            return response;
        }
        await BindingDeclarations.OpenAsync(item, CurrentBinding, ValidateAsync,
            async (declaration, token) => await host.NavigateBindingSourceAsync(new(document, text, declaration, CurrentBinding, ValidateAsync), token), _lifetime.Token);
    }
}
