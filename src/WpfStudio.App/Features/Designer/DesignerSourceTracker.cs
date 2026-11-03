using System.ComponentModel;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Designer;

/// <summary>
/// Ties the open designer to the editor it previews. When that editor's XAML project context changes (another
/// project or framework is chosen), the preview is closed rather than left showing a stale context; when the
/// designer moves to another file, the old editor is released. It also opens the source locations the designer asks for.
/// </summary>
public sealed class DesignerSourceTracker : IAsyncDisposable
{
    private sealed record ProjectIdentity(string? Path, string? Framework, string? Output, string? Assembly, string? Assets = null);
    private readonly DesignerViewModel _designer;
    private readonly IWorkbenchHost _host;
    private EditorViewModel? _editor;
    private ProjectIdentity? _identity;
    private bool _disposed;

    public DesignerSourceTracker(DesignerViewModel designer, IWorkbenchHost host)
    {
        _designer = designer; _host = host;
        _designer.PropertyChanged += DesignerSourceChanged;
        _designer.SourceRequested += SourceRequested;
    }

    /// <summary>The close started when the project context changed; opening the designer again waits for it.</summary>
    public Task PendingClose { get; private set; } = Task.CompletedTask;

    private static ProjectIdentity IdentityOf(EditorViewModel editor) => editor.XamlProjectPath is { } path
        ? new(path, editor.XamlProject?.TargetFramework, editor.XamlProject?.OutputPath, editor.XamlProject?.AssemblyName, editor.XamlProject?.ProjectAssetsPath)
        : new(null, null, null, null);

    /// <summary>Follows <paramref name="editor"/>, the document the designer is about to open.</summary>
    public void Track(EditorViewModel editor)
    {
        Detach();
        _editor = editor;
        _identity = IdentityOf(editor);
        editor.PropertyChanged += EditorChanged;
    }

    public void Detach()
    {
        if (_editor is { } editor) editor.PropertyChanged -= EditorChanged;
        _editor = null; _identity = null;
    }

    private void SourceRequested(SourceLocation source) => _ = _host.GuardAsync(() => _host.NavigateAsync(source.Path, source.Line, source.Column));

    private void DesignerSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DesignerViewModel.SourcePath) && _editor is { } editor
            && !string.Equals(_designer.SourcePath, editor.State.Path, StringComparison.OrdinalIgnoreCase)) Detach();
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || args.PropertyName != nameof(EditorViewModel.XamlProjectPath) || _editor is not { } editor
            || !ReferenceEquals(sender, editor) || IdentityOf(editor) == _identity) return;
        Detach();
        // CloseAsync clears the document and invalidates its preview revision
        // synchronously before waiting for the owned process to stop. A pending
        // source-edit review therefore becomes invalid in this same UI turn.
        PendingClose = _host.GuardAsync(async () =>
        {
            await _designer.CloseAsync();
            if (!_disposed && _designer.SourcePath is null)
                _designer.Status = "XAML project context changed. Open Preview again to use the selected project.";
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _designer.PropertyChanged -= DesignerSourceChanged;
        _designer.SourceRequested -= SourceRequested;
        await PendingClose;
    }
}
