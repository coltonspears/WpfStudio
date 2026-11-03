using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Designer;

/// <summary>
/// The designer's requests that need the editor and the change review, answered by one owner. Each request carries its
/// own staleness guard, so the answer can be checked against the preview that asked.
/// </summary>
public interface IDesignerSourceHost
{
    /// <summary>Reviews and applies a property edit; false when the review was cancelled.</summary>
    Task<bool> ReviewPropertyEditAsync(XamlPropertyEditResult proposal);
    /// <summary>Reviews and applies a layout gesture; false when the review was cancelled.</summary>
    Task<bool> ReviewLayoutEditAsync(DesignerLayoutSourceEdit edit);
    /// <summary>Opens a verified binding declaration and returns the status to show.</summary>
    Task<string> NavigateBindingSourceAsync(PreviewBindingSourceNavigation navigation, CancellationToken cancellationToken);
    /// <summary>The user chose this element in the designer: show its markup in the editor.</summary>
    void RevealSelection(SourceLocation source);
}
