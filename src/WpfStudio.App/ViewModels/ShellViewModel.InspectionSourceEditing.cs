using System.Text;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private async Task<InspectionSourceEditOutcome> EditInspectionSourceAsync(InspectionSourceEditRequest request, CancellationToken token)
    {
        if (IsPreviewOpen) return new(false, "Finish the existing source review before preparing another change.");
        var verification = await VerifyInspectionSourceAsync(request.Node, request.Modules, request.IsCurrent, token);
        if (verification.Source is not { } source) return new(false, verification.Status);
        var document = source.Document;
        var version = document.Version;
        InspectionSourcePropertyIdentity identity;
        InspectionSourcePropertyTarget authoredType;
        XamlPropertyEditResult proposal;
        IReadOnlyList<FileChange> changes;
        EditorViewModel editor;
        bool Current() => source.IsCurrent() && _store.Find(document.Path) == document && document.Version == version
            && string.Equals(document.Content, source.Text, StringComparison.Ordinal);
        using (source)
        {
            if (!Current()) return StaleSourceEdit();
            var validation = await request.ValidateAsync(false, token);
            if (!Current()) return StaleSourceEdit();
            if (!validation.Success || validation.Property is not { } property) return new(false, validation.Error ?? "The property cannot be written to XAML.");
            identity = property;
            var matching = InspectionSourcePropertyMapping.Match(identity, source.Location.ElementType,
                source.Location.ElementNamespace, source.Module.AssemblyName, request.Modules);
            if (matching is null || source.Location.Element is null)
                return new(false, "The property is not verified writable on the type declared in this XAML element. A runtime subclass may expose additional properties.");
            authoredType = matching;
            proposal = XamlPropertyEditService.CreateEdit(new(document.Path, source.Text, version, source.Location.Element,
                version, DocumentStore.Hash(Encoding.UTF8.GetBytes(source.Text)), identity.PropertyName, validation.Literal,
                ClearLocalValue: request.Remove, ReplaceExistingValue: true, OwnerType: identity.OwnerType,
                OwnerAssembly: identity.OwnerAssembly, IsAttached: identity.IsAttached, SourceAssembly: source.Module.AssemblyName,
                ContentProperty: authoredType.ContentProperty, SetNull: !request.Remove && validation.IsNull));
            if (!proposal.Success || proposal.Edit is null) return new(false, proposal.Error ?? proposal.Explanation);
            changes = await _edits.PrepareAsync(new WorkspaceEditResult([proposal.Edit], []), token);
            if (!Current()) return StaleSourceEdit();
            if (changes.All(change => change.Before == change.After)) return new(false, proposal.Explanation);
            AddDocument(document);
            editor = ActiveDocument!;
            if (editor.State != document || editor.IsReadOnly) return new(false, "The source editor is not available for this change.");
        }
        // Release the build-source read lease before showing a review. Normal
        // editing and saving remain possible; any changes invalidate this diff.
        if (IsPreviewOpen) return new(false, "Another source review opened while this change was being prepared.");
        var explanation = proposal.Explanation + " This updates the editor buffer only. The running application keeps its current build and temporary overrides until reset, disconnect or relaunch.";
        if (!await PreviewAsync(request.Remove ? "Remove local XAML value from live inspection" : "Write live property value to XAML", changes, explanation))
            return new(false, "XAML change cancelled. The source and running property were not changed by this action.");
        bool CanApply() => Current() && Documents.Contains(editor) && !editor.IsReadOnly;
        if (!CanApply()) return StaleSourceEdit();
        var modules = await request.RefreshModulesAsync(token);
        if (!CanApply()) return StaleSourceEdit();
        var checkedAgain = await VerifyInspectionSourceAsync(request.Node, modules, CanApply, token);
        if (checkedAgain.Source is not { } finalSource) return new(false, checkedAgain.Status);
        using (finalSource)
        {
            if (finalSource.Document != document || !CanApply()) return StaleSourceEdit();
            var current = await request.ValidateAsync(true, token);
            if (!CanApply()) return StaleSourceEdit();
            if (!current.Success || current.Property is not { } currentIdentity)
                return new(false, current.Error ?? "The runtime property changed during source review. Reload and review again.");
            var currentTarget = InspectionSourcePropertyMapping.Match(currentIdentity, finalSource.Location.ElementType,
                finalSource.Location.ElementNamespace, finalSource.Module.AssemblyName, modules);
            if (currentTarget is null || !SameIdentity(identity, currentIdentity) ||
                currentTarget.Type != authoredType.Type || currentTarget.Assembly != authoredType.Assembly ||
                currentTarget.ModuleVersionId != authoredType.ModuleVersionId || currentTarget.ContentProperty != authoredType.ContentProperty)
                return new(false, "The writable property identity changed during source review. Reload and review again.");
            // ApplyAsync rechecks this context after its asynchronous disk checks
            // and immediately before changing any buffer.
            await _edits.ApplyAsync(changes, token, CanApply);
            foreach (var change in changes) AddDocument(_store.Find(change.Path)!);
        }
        return new(true, "XAML updated in the editor. Save to write the file; Undo workspace edit restores it. The running application has not been rebuilt. Rebuild with inspection before writing another source change.");
    }

    private static InspectionSourceEditOutcome StaleSourceEdit() => new(false,
        "The source, selection, property or inspection session changed during review. Your newer state was preserved; prepare the change again.");

    private static bool SameIdentity(InspectionSourcePropertyIdentity first, InspectionSourcePropertyIdentity second) =>
        first.PropertyName == second.PropertyName && first.OwnerType == second.OwnerType && first.OwnerAssembly == second.OwnerAssembly
        && first.IsAttached == second.IsAttached && first.TargetType == second.TargetType && first.TargetAssembly == second.TargetAssembly
        && first.TargetModuleVersionId == second.TargetModuleVersionId && first.OwnerModuleVersionId == second.OwnerModuleVersionId;
}
