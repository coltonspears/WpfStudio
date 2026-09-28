using System.IO;
using WpfStudio.App.Features.BindingSources;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _navigatingBindingSource;
    private bool SelectVerifiedBindingSpan(EditorViewModel editor, WpfStudio.Contracts.SourceLocation target, Func<bool> isCurrent)
    {
        bool previous = _navigatingBindingSource;
        _navigatingBindingSource = true;
        try
        {
            editor.Navigate(target.Start);
            if (!ReferenceEquals(ActiveDocument, editor) || !isCurrent()) return false;
            editor.RestoreSelection(target.Start + target.Length, target.Start, target.Length);
            return true;
        }
        finally { _navigatingBindingSource = previous; }
    }
    private async Task<string> NavigateInspectionBindingSourceAsync(InspectionBindingSourceNavigation request, CancellationToken token)
    {
        if (request.Declaration.Source is not { } hint) return request.Declaration.UnavailableReason ?? "WPF supplied no binding declaration source.";
        var verification = await VerifyInspectionSourceAsync(hint, request.Modules, request.IsCurrent,
            (path, text) => XamlBindingSourceLocator.Locate(path, text, hint.Line, hint.Column, request.Declaration.Kind), token);
        if (verification.Source is not { } source) return verification.Status;
        using (source)
        {
            if (!source.IsCurrent()) return "Binding source navigation cancelled because the context changed.";
            var current = await request.RevalidateAsync(token);
            if (!source.IsCurrent()) return "Binding source navigation cancelled because the context changed.";
            if (!current.Available || current.Declaration is not { } declaration || !BindingDeclarationItem.SameDeclaration(request.Declaration, declaration))
                return current.Status ?? "The actual binding declaration changed during source verification. Inspect the element again.";
            var result = NavigateVerifiedInspectionSource(source, selectDeclaration: true);
            return result.StartsWith("Opened verified source location:", StringComparison.Ordinal)
                ? result.Replace("Opened verified source location:", "Opened verified binding declaration:", StringComparison.Ordinal)
                : result;
        }
    }

    internal async Task<string> NavigatePreviewBindingSourceAsync(PreviewBindingSourceNavigation request, CancellationToken token)
    {
        var document = request.Document; var version = document.Version; var workspace = Workspace;
        bool Current() => request.IsCurrent() && ReferenceEquals(workspace, Workspace) && ReferenceEquals(_store.Find(document.Path), document)
            && document.Version == version && string.Equals(document.Content, request.Text, StringComparison.Ordinal);
        if (!Current()) return "Preview binding source navigation cancelled because the source or preview changed.";
        var hint = request.Declaration.Source;
        if (hint is null || !Uri.TryCreate(hint.Uri, UriKind.Absolute, out var uri) || !uri.IsFile ||
            !string.Equals(Path.GetFullPath(uri.LocalPath), document.Path, StringComparison.OrdinalIgnoreCase))
            return "This binding declaration does not have a verified location in the original preview source. External and compiled origins are unavailable here.";
        var location = XamlBindingSourceLocator.Locate(document.Path, request.Text, hint.Line, hint.Column, request.Declaration.Kind);
        if (location.Location is not { } target) return location.Status;
        var response = await request.RevalidateAsync(token);
        if (!Current()) return "Preview binding source navigation cancelled because the source or preview changed.";
        if (!response.Available || response.Declaration is not { } declaration || !BindingDeclarationItem.SameDeclaration(request.Declaration, declaration))
            return response.Status ?? "The preview binding declaration changed. Inspect the element again.";
        AddDocument(document);
        var editor = ActiveDocument;
        if (editor?.State != document || !Documents.Contains(editor) || !Current())
            return "Preview binding source navigation cancelled because the source or preview changed.";
        if (!SelectVerifiedBindingSpan(editor, target, Current)) return "Preview binding source navigation cancelled because the source or preview changed.";
        return $"Opened verified preview binding declaration: {document.Path}:{target.Line}:{target.Column}. The original source buffer and current preview expression matched.";
    }
}
