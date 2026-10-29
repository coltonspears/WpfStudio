using Microsoft.CodeAnalysis.QuickInfo;
using QuickInfoSection = WpfStudio.Contracts.QuickInfoSection;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    /// <summary>
    /// Roslyn's Quick Info for the symbol at a position, as Visual Studio shows it on hover: the classified signature,
    /// documentation, return value, type parameters, captured variables and nullability. Null where nothing applies.
    /// </summary>
    public async Task<QuickInfoResult?> GetQuickInfoAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        await EnsurePageProjectionAsync(cancellationToken).ConfigureAwait(false);
        var document = GetDocument(request.Path, request.Version);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        ValidatePosition(request.Position, text.Length);
        if (QuickInfoService.GetService(document) is not { } service) return null;
        var item = await service.GetQuickInfoAsync(document, request.Position, cancellationToken).ConfigureAwait(false);
        if (item is null) return null;
        var sections = item.Sections
            .Select(section => new QuickInfoSection(section.Kind, section.TaggedParts.Select(part => new QuickInfoRun(part.Tag, part.Text)).ToArray()))
            .Where(section => section.Runs.Any(run => !string.IsNullOrWhiteSpace(run.Text))).ToArray();
        return sections.Length == 0 ? null : new(request.Version, item.Span.Start, item.Span.Length, item.Tags.ToArray(), sections);
    }
}
