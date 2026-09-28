using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    public event Func<XamlPropertyEditResult, Task<bool>>? SourceEditRequested;

    internal Func<bool> CaptureSourceEditGuard()
    {
        var revision = _revision; var selection = _selectionRevision;
        var document = _document; var property = SelectedProperty; var value = EditedValue;
        return () => Current(revision) && selection == _selectionRevision && ReferenceEquals(document, _document)
            && property == SelectedProperty && value == EditedValue && CanWritePropertySource()
            && IsScenarioConfigurationCurrent();
    }

    public string SourceEditDescription => SelectedNode?.Node.Source == null
        ? "Source editing requires an authored element in the current source preview."
        : "Review the XAML diff before applying. A local value can replace a binding or override a style; template edits affect every instance.";

    private bool CanWritePropertySource() => !_disposed && !IsCompiledPreview && IsCurrent && _document is { } document && document.Version == _sourceVersion &&
        _sourceHash != null && SelectedProperty is { CanWriteSource: true } &&
        _inspectedNodeId == SelectedNode?.Node.Id && Properties.Contains(SelectedProperty) &&
        SelectedNode?.Node.Source is { } source &&
        string.Equals(source.Path, document.Path, StringComparison.OrdinalIgnoreCase) && SourceEditRequested != null;

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
        ++_bindingSourceEpoch; RefreshBindingSourceState();
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
            var apply = SourceEditRequested;
            if (apply == null) return;
            bool applied = await apply(proposal);
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
