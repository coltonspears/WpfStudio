using WpfStudio.App.Features.Inspection;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    private async Task<string> NavigateInspectionSourceAsync(InspectionSourceRequest request, CancellationToken token)
    {
        var verification = await InspectionSources.VerifyAsync(request.Node, request.Modules, request.IsCurrent, token);
        if (verification.Source is not { } source) return verification.Status;
        using (source) return NavigateVerifiedInspectionSource(source);
    }

    internal string NavigateVerifiedInspectionSource(VerifiedInspectionSource source, bool selectDeclaration = false)
    {
        if (!source.IsCurrent()) return "Source navigation cancelled because the source buffer or inspection context changed.";
        var target = source.Location.Location!;
        // Activating an editor can synchronously trigger selection/context and
        // buffer changes. Recheck the version captured by the verification lease.
        AddDocument(source.Document);
        var editor = ActiveDocument;
        if (editor?.State != source.Document || !Documents.Contains(editor) || !source.IsCurrent())
            return "Source navigation cancelled because the source buffer or inspection context changed.";
        // No awaits between the final buffer check and navigation.
        if (selectDeclaration)
        {
            if (!SelectVerifiedBindingSpan(editor, target, source.IsCurrent))
                return "Source navigation cancelled because the source buffer or inspection context changed.";
        }
        else editor.Navigate(target.Start);
        return $"Opened verified source location: {source.Document.Path}:{target.Line}:{target.Column}. Module, compiled resource, build checksum and editor buffer matched at navigation. Show XAML again to recheck after edits.";
    }
}
