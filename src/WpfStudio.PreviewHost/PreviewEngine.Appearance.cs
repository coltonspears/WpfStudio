using System.Windows;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.PreviewHost;

public sealed partial class PreviewEngine
{
    private readonly StaticResourceEvidenceCollector _appearanceEvidence = new();

    public Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() =>
        {
            AppearanceSnapshot captured;
            try
            {
                if (request.Revision != _version || !_objects.TryGetValue(request.NodeId, out var target) || !IsInCurrentPreview(target))
                    captured = AppearanceSnapshot.Unavailable("The preview or selected element has changed. Refresh the selection.");
                else
                {
                    var property = FindProperty(target, new PreviewPropertyEdit(request.Revision, request.NodeId,
                        request.Property, null, OwnerType: request.OwnerType, OwnerAssembly: request.OwnerAssembly));
                    captured = ResourceStyleReader.Capture(target, property, _appearanceEvidence);
                }
            }
            catch (Exception exception) { captured = AppearanceSnapshot.Unavailable(exception.GetBaseException().Message); }
            return Task.FromResult(new AppearanceResponse(request, captured));
        }, cancellationToken);

    private bool IsInCurrentPreview(DependencyObject target)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        for (DependencyObject? current = target; current is not null && visited.Count < 512 && visited.Add(current);)
        {
            if (ReferenceEquals(current, _viewport)) return true;
            current = VisualParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
