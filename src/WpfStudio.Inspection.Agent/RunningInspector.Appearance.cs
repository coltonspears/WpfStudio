using System.Runtime.CompilerServices;
using System.Windows;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Inspection.Agent;

internal sealed partial class RunningInspector
{
    // Appearance is available for read-only/non-scalar properties too. Do not use
    // the narrower edit-token bag, or resolve a client-supplied owner/type name.
    private readonly ConditionalWeakTable<DependencyObject, AppearancePropertyBag> _appearanceProperties = new();
    private sealed record AppearanceProperty(DependencyProperty Property, string Name, string OwnerType, string OwnerAssembly);
    private sealed record AppearanceObservation(long Revision, IReadOnlyDictionary<string, AppearanceProperty> Properties);
    private sealed class AppearancePropertyBag { public AppearanceObservation? Observation; }

    public async Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request)
    {
        AppearanceResponse Unavailable(string status) => new(request, AppearanceSnapshot.Unavailable(status));
        if (string.IsNullOrEmpty(request.NodeId) || request.NodeId.Length > 128 ||
            string.IsNullOrEmpty(request.PropertyId) || request.PropertyId.Length > 128 ||
            request.Property is null || request.Property.Length > 1024 || request.OwnerType?.Length > 2048 || request.OwnerAssembly?.Length > 1024)
            return Unavailable("An observed element and opaque property identity are required.");
        if (_editingDisposed || request.Revision != _revision || !_entries.TryGetValue(request.NodeId, out var entry) ||
            !entry.Target.TryGetTarget(out var target))
            return Unavailable("The session, tree revision, or selected element changed. Refresh its properties.");
        try
        {
            return await OnDispatcherAsync(target.Dispatcher, token =>
            {
                token.ThrowIfCancellationRequested();
                if (_editingDisposed || request.Revision != _revision || !_entries.TryGetValue(request.NodeId, out var current) ||
                    !ReferenceEquals(current, entry) || !current.Target.TryGetTarget(out var observedTarget) || !ReferenceEquals(observedTarget, target) ||
                    !entry.Source.TryGetTarget(out var source) || source.IsDisposed || !entry.Root.TryGetTarget(out var root) ||
                    source.RootVisual != root || !IsAttached(target, root))
                    return Unavailable("The selected element has left its observed presentation tree, or the observation is stale.");
                if (!_appearanceProperties.TryGetValue(target, out var bag) || bag.Observation is not { } observation ||
                    observation.Revision != request.Revision || !observation.Properties.TryGetValue(request.PropertyId, out var property))
                    return Unavailable("The property identity was not observed on this element at this tree revision. Inspect its properties again.");
                if (request.Property.Length > 0 && request.Property != property.Name ||
                    request.OwnerType is not null && request.OwnerType != property.OwnerType ||
                    request.OwnerAssembly is not null && request.OwnerAssembly != property.OwnerAssembly)
                    return Unavailable("The requested property name or owner does not match the observed dependency property.");
                var snapshot = ResourceStyleReader.Capture(target, property.Property, resources);
                token.ThrowIfCancellationRequested();
                if (_editingDisposed || request.Revision != _revision || source.IsDisposed || source.RootVisual != root || !IsAttached(target, root))
                    return Unavailable("The appearance observation became stale while it was captured.");
                return new AppearanceResponse(request, snapshot);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("Appearance is unavailable: " + Limit(exception.GetBaseException().Message, 1500)); }
    }
}
