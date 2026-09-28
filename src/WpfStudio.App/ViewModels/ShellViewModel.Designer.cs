#nullable enable
using System.IO;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    public DesignerViewModel Designer { get; }
    private EditorViewModel? _designerEditor;
    private DesignerProjectIdentity? _designerProjectIdentity;
    private Task _designerContextClose = Task.CompletedTask;
    private sealed record DesignerProjectIdentity(string? Path, string? Framework, string? Output, string? Assembly);
    private static DesignerProjectIdentity DesignerIdentity(EditorViewModel editor) => editor.XamlProjectPath is { } path
        ? new(path, editor.XamlProject?.TargetFramework, editor.XamlProject?.OutputPath, editor.XamlProject?.AssemblyName)
        : new(null, null, null, null);
    private void InitializeDesignerContext() => Designer.PropertyChanged += DesignerSourceChanged;
    private void TrackDesignerEditor(EditorViewModel editor)
    {
        DetachDesignerEditor();
        _designerEditor = editor;
        _designerProjectIdentity = DesignerIdentity(editor);
        editor.PropertyChanged += DesignerEditorChanged;
    }
    private void DetachDesignerEditor()
    {
        if (_designerEditor is { } editor) editor.PropertyChanged -= DesignerEditorChanged;
        _designerEditor = null; _designerProjectIdentity = null;
    }
    private void DesignerSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DesignerViewModel.SourcePath) && _designerEditor is { } editor
            && !string.Equals(Designer.SourcePath, editor.State.Path, StringComparison.OrdinalIgnoreCase)) DetachDesignerEditor();
    }
    private void DesignerEditorChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || args.PropertyName != nameof(EditorViewModel.XamlProjectPath) || _designerEditor is not { } editor
            || !ReferenceEquals(sender, editor) || DesignerIdentity(editor) == _designerProjectIdentity) return;
        DetachDesignerEditor();
        // CloseAsync clears the document and invalidates its preview revision
        // synchronously before waiting for the owned process to stop. A pending
        // source-edit review therefore becomes invalid in this same UI turn.
        _designerContextClose = GuardAsync(async () =>
        {
            await Designer.CloseAsync();
            if (!_disposed && Designer.SourcePath is null)
                Designer.Status = "XAML project context changed. Open Preview again to use the selected project.";
        });
    }
    private async Task DisposeDesignerContextAsync()
    {
        DetachDesignerEditor();
        Designer.PropertyChanged -= DesignerSourceChanged;
        await _designerContextClose;
    }
    private async Task<bool> PreviewDesignerEditAsync(XamlPropertyEditResult proposal)
    {
        if (proposal.Edit == null || IsPreviewOpen) return false;
        var editor = Documents.FirstOrDefault(d => d.State.Path.Equals(proposal.Edit.Path, StringComparison.OrdinalIgnoreCase));
        if (editor == null || editor.IsReadOnly) throw new InvalidOperationException("The designer's source document is no longer editable.");
        var version = editor.State.Version;
        var designerCurrent = Designer.CaptureSourceEditGuard();
        bool CanApply() => Documents.Contains(editor) && !editor.IsReadOnly && editor.State.Version == version && designerCurrent();
        var changes = await _edits.PrepareAsync(new WorkspaceEditResult([proposal.Edit], []), _lifetime.Token);
        if (changes.All(change => change.Before == change.After)) return false;
        if (!await PreviewAsync("Update XAML from inspector", changes, proposal.Explanation)) return false;
        if (!CanApply())
            throw new InvalidOperationException("The source document or preview changed while the edit was being reviewed. Refresh the preview and try again.");
        // The transaction rejects changes made while the diff was being reviewed.
        await _edits.ApplyAsync(changes, _lifetime.Token, CanApply);
        foreach (var change in changes) AddDocument(_store.Find(change.Path)!);
        return true;
    }
    [RelayCommand] private Task OpenDesignerAsync() => GuardAsync(async () =>
    {
        await _designerContextClose;
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
        TrackDesignerEditor(document);
        await Designer.OpenAsync(document.State, assembly,
            project == null ? Path.GetDirectoryName(document.State.Path) : Path.GetDirectoryName(project.ProjectPath), project?.AssemblyName);
    });
}
