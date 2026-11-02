#nullable enable
using System.IO;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    public DesignerViewModel Designer { get; }
    private bool _revealingDesignerSelection;

    /// <summary>
    /// Mirrors a selection made in the designer canvas or outline in the XAML editor: the
    /// element's start tag is selected and scrolled into view without moving keyboard focus,
    /// so designer shortcuts such as layout nudges keep working.
    /// </summary>
    private void RevealDesignerSelection(SourceLocation source)
    {
        var editor = Documents.FirstOrDefault(d => d.State.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase));
        if (editor is null) return;
        var text = editor.State.Content;
        if (source.Start < 0 || source.Start >= text.Length || source.Length <= 0) return;
        int end = StartTagEnd(text, source.Start, source.Length);
        _revealingDesignerSelection = true;
        try { editor.RestoreSelection(source.Start, source.Start, end - source.Start); }
        finally { _revealingDesignerSelection = false; }
    }

    /// <summary>End offset (exclusive) of the start tag beginning at <paramref name="start"/>, ignoring '>' inside quotes.</summary>
    internal static int StartTagEnd(string text, int start, int length)
    {
        int limit = Math.Min(text.Length, start + length);
        char quote = '\0';
        for (int i = start; i < limit; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i + 1;
        }
        return limit;
    }

    private Task<bool> PreviewDesignerEditAsync(XamlPropertyEditResult proposal) =>
        ReviewDesignerEditAsync(proposal, Designer.CaptureSourceEditGuard(), "Update XAML from inspector");

    private Task<bool> PreviewDesignerLayoutEditAsync(DesignerLayoutSourceEdit request) =>
        ReviewDesignerEditAsync(request.Proposal, request.IsCurrent, "Update XAML layout", request.Validate);

    private async Task<bool> ReviewDesignerEditAsync(XamlPropertyEditResult proposal, Func<bool> designerCurrent,
        string title, Func<CancellationToken, Task<PreviewLayoutValidationResult>>? validate = null)
    {
        if (proposal.Edit == null || IsPreviewOpen) return false;
        var editor = Documents.FirstOrDefault(d => d.State.Path.Equals(proposal.Edit.Path, StringComparison.OrdinalIgnoreCase));
        if (editor == null || editor.IsReadOnly) throw new InvalidOperationException("The designer's source document is no longer editable.");
        var version = editor.State.Version;
        bool CanApply() => Documents.Contains(editor) && !editor.IsReadOnly && editor.State.Version == version && designerCurrent();
        var changes = await _edits.PrepareAsync(new WorkspaceEditResult([proposal.Edit], []), _lifetime.Token);
        if (changes.All(change => change.Before == change.After)) return false;
        if (!CanApply()) throw new InvalidOperationException("The source or preview changed while preparing the edit. Refresh and try again.");
        if (!await PreviewAsync(title, changes, proposal.Explanation)) return false;
        if (!CanApply())
            throw new InvalidOperationException("The source document or preview changed while the edit was being reviewed. Refresh the preview and try again.");
        if (validate is not null)
        {
            var result = await validate(_lifetime.Token);
            if (!result.Success) throw new InvalidOperationException(result.Error ?? "The observed layout changed during review. Update the snapshot and try again.");
        }
        // The transaction rejects changes made while the diff was being reviewed.
        await _edits.ApplyAsync(changes, _lifetime.Token, CanApply);
        foreach (var change in changes) AddDocument(_store.Find(change.Path)!);
        return true;
    }
    [RelayCommand] private Task OpenDesignerAsync() => GuardAsync(async () =>
    {
        await DesignerContext.PendingClose;
        var document = ActiveDocument ?? Documents.FirstOrDefault(d => d.State.Path == Designer.SourcePath);
        ToolRequested?.Invoke("Designer");
        if (document?.State.Extension != ".xaml") { Status = "Open a XAML document to preview it"; return; }
        var owners = Projects.Where(p => p.Files.Any(f => f.Path.Equals(document.State.Path, StringComparison.OrdinalIgnoreCase))).ToArray();
        var selectedOwners = owners.Where(owner => owner.ProjectPath.Equals(document.XamlProjectPath, StringComparison.OrdinalIgnoreCase)
            && owner.TargetFramework == document.XamlProject?.TargetFramework).ToArray();
        var project = selectedOwners.Length == 1 ? selectedOwners[0] : owners.Length == 1 ? owners[0] : null;
        if (owners.Length > 1 && project is null)
        {
            Status = "Select the XAML project's context before opening its preview scenarios.";
            return;
        }
        var assembly = project?.OutputPath;
        if (assembly != null && Directory.Exists(assembly)) assembly = Path.Combine(assembly, (project!.AssemblyName ?? project.Name) + ".dll");
        // Keep the evaluated output path even before the first build. Refresh can
        // then discover the new assembly without requiring the designer to reopen.
        DesignerContext.Track(document);
        await Designer.OpenAsync(document.State, assembly,
            project == null ? Path.GetDirectoryName(document.State.Path) : Path.GetDirectoryName(project.ProjectPath), project?.AssemblyName, project?.ProjectAssetsPath,
            project is null ? null : token => _build.ResolvePreviewReferencesAsync(project.ProjectPath, Configuration, project.TargetFramework, token));
    });
}
