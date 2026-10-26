using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    public async Task<EditorDecorationsResult> GetEditorDecorationsAsync(DocumentRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var document = GetDocument(request.Path, request.Version);
        var spans = await EditorDecorations.ClassifyAsync(document, cancellationToken).ConfigureAwait(false);
        var hints = await EditorDecorations.GetHintsAsync(document, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return _versions.GetValueOrDefault(Path.GetFullPath(request.Path)) == request.Version && ReferenceEquals(document.Project.Solution, _solution)
                ? new(request.Version, spans, hints) : new(request.Version, [], []);
    }
}
