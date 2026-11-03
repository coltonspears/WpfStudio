using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    private IDesignerSourceHost? _sourceHost;
    /// <summary>Connects the one owner that answers the designer's requests. A second owner is refused rather than replacing the first's answers.</summary>
    public void AttachSourceHost(IDesignerSourceHost host)
    {
        if (_sourceHost is not null) throw new InvalidOperationException("The designer already has a source host.");
        _sourceHost = host;
    }

    internal Func<bool> CaptureSourceEditGuard()
    {
        var revision = _revision; var selection = _selectionRevision;
        var document = _document; var property = SelectedProperty; var value = EditedValue;
        return () => Current(revision) && selection == _selectionRevision && ReferenceEquals(document, _document)
            && property == SelectedProperty && value == EditedValue && CanWritePropertySource()
            && Scenarios.IsConfigurationCurrent();
    }

    public string SourceEditDescription => SelectedNode?.Node.Source == null
        ? "Source editing requires an authored element in the current source preview."
        : "Review the XAML diff before applying. A local value can replace a binding or override a style; template edits affect every instance.";

    private bool CanWritePropertySource() => !_disposed && !IsCompiledPreview && IsCurrent && _document is { } document && document.Version == _sourceVersion &&
        _sourceHash != null && SelectedProperty is { CanWriteSource: true } &&
        _inspectedNodeId == SelectedNode?.Node.Id && Properties.Contains(SelectedProperty) &&
        SelectedNode?.Node.Source is { } source &&
        string.Equals(source.Path, document.Path, StringComparison.OrdinalIgnoreCase) && _sourceHost != null;

    private void NotifySourceCommands()
    {
        WritePropertyToSourceCommand.NotifyCanExecuteChanged();
        RemovePropertyFromSourceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SourceEditDescription));
    }

    [RelayCommand(CanExecute = nameof(CanWritePropertySource))]
    private Task WritePropertyToSourceAsync() => EditPropertySourceAsync(clear: false);

    [RelayCommand(CanExecute = nameof(CanWritePropertySource))]
    private Task RemovePropertyFromSourceAsync() => EditPropertySourceAsync(clear: true);

    private async Task EditPropertySourceAsync(bool clear)
    {
        if (!CanWritePropertySource()) return;
        var document = _document!;
        var node = SelectedNode!.Node;
        var property = SelectedProperty!;
        var revision = _revision;
        var selection = _selectionRevision;
        var text = document.Content;
        var sourceVersion = _sourceVersion;
        var sourceHash = _sourceHash!;
        var literal = EditedValue;
        ClearBindingExplanation("Source review in progress. Refresh to observe bindings after the review.");
        ++_bindingOperationEpoch;
        ++_bindingOperations;
        ++_appearancePropertyOperations;
        BindingDeclarations.AdvanceEpoch(); RefreshBindingSourceState();
        ClearAppearance("Preparing a source edit. Refresh appearance after the review.");
        try
        {
            if (!clear)
            {
                var validation = await _client.ValidatePropertyAsync(new(revision, node.Id, property.Name, literal,
                    OwnerType: property.OwnerType, OwnerAssembly: property.OwnerAssembly), _lifetime.Token);
                if (!Current(revision) || selection != _selectionRevision || SelectedNode?.Node.Id != node.Id || SelectedProperty != property ||
                    EditedValue != literal || document.Version != sourceVersion || !ReferenceEquals(document, _document)) return;
                if (!validation.Success) { Status = validation.Error ?? "This value cannot be written to XAML."; return; }
            }

            var request = new XamlPropertyEditRequest(document.Path, text, sourceVersion, node.Source!,
                sourceVersion, sourceHash, property.Name, literal, ClearLocalValue: clear, ReplaceExistingValue: true,
                OwnerType: property.OwnerType, OwnerAssembly: property.OwnerAssembly, IsAttached: property.IsAttached,
                SourceAssembly: _sourceAssembly, ContentProperty: property.ContentProperty);
            var proposal = XamlPropertyEditService.CreateEdit(request);
            if (!proposal.Success) { Status = proposal.Error ?? proposal.Explanation; return; }
            if (_sourceHost is not { } host) return;
            bool applied = await host.ReviewPropertyEditAsync(proposal);
            if (!_disposed && ReferenceEquals(document, _document))
                Status = applied ? "XAML updated in the editor. Save to write the file; Undo workspace edit restores it." : "XAML edit cancelled.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed && ReferenceEquals(document, _document)) Status = exception.Message; }
        finally
        {
            --_bindingOperations;
            --_appearancePropertyOperations;
            RefreshAppearanceSelection();
            RefreshBindingSourceState();
        }
    }
}
