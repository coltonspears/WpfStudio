namespace WpfStudio.App.Features.Inspection;

/// <summary>
/// Live inspection's requests that need the build, the editor and the change review, answered by one owner. Each request
/// carries its own staleness guard, so the answer can be checked against the session that asked.
/// </summary>
public interface IInspectionRequestHost
{
    Task LaunchAsync(bool debug);
    /// <summary>Opens the verified source of an element and returns the status to show.</summary>
    Task<string> NavigateSourceAsync(InspectionSourceRequest request, CancellationToken cancellationToken);
    /// <summary>Opens a verified binding declaration and returns the status to show.</summary>
    Task<string> NavigateBindingSourceAsync(InspectionBindingSourceNavigation navigation, CancellationToken cancellationToken);
    /// <summary>Reviews a XAML edit for an observed property and reports what came of it.</summary>
    Task<InspectionSourceEditOutcome> EditSourceAsync(InspectionSourceEditRequest request, CancellationToken cancellationToken);
}
