using Microsoft.CodeAnalysis.Completion;
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

    /// <summary>
    /// What an entry of the open completion list is: its signature and documentation, shaped like Quick Info. Answers from
    /// the snapshot the list came from, because typing filters the list and changes the document meanwhile. Null once the list has gone.
    /// </summary>
    public async Task<QuickInfoResult?> GetCompletionDetailsAsync(CompletionEditRequest request, CancellationToken cancellationToken)
    {
        OpenCompletionList? open;
        lock (_gate) open = OpenCompletions;
        if (open is null || !open.Items.TryGetValue(request.ItemId, out var item)
            || !string.Equals(open.Document.FilePath, Path.GetFullPath(request.Path), StringComparison.OrdinalIgnoreCase)) return null;
        if (CompletionService.GetService(open.Document) is not { } service) return null;
        var description = await service.GetDescriptionAsync(open.Document, item, cancellationToken).ConfigureAwait(false);
        if (description is null) return null;
        // Roslyn gives one run of text: the signature, a line break, then the documentation.
        var signature = description.TaggedParts.TakeWhile(part => part.Tag != "LineBreak").Select(part => new QuickInfoRun(part.Tag, part.Text)).ToArray();
        var documentation = description.TaggedParts.SkipWhile(part => part.Tag != "LineBreak").SkipWhile(part => part.Tag == "LineBreak").Select(part => new QuickInfoRun(part.Tag, part.Text)).ToArray();
        var sections = new[] { new QuickInfoSection("Description", signature), new QuickInfoSection("DocumentationComments", documentation) }
            .Where(section => section.Runs.Any(run => !string.IsNullOrWhiteSpace(run.Text))).ToArray();
        return sections.Length == 0 ? null : new(request.Version, 0, 0, item.Tags.ToArray(), sections);
    }
}
