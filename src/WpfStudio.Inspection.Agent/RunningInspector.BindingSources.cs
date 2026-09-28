using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Inspection.Agent;

internal sealed partial class RunningInspector
{
    private readonly BindingSourceCatalog _bindingSources = new();
    private readonly ConditionalWeakTable<DependencyObject, BindingSourcePropertyBag> _bindingSourceProperties = new();
    private sealed record BindingSourceProperty(DependencyProperty Property, string Name, string OwnerType,
        string OwnerAssembly, WeakReference<BindingExpressionBase> Root, BindingSourcesSnapshot Sources);
    private sealed record BindingSourceObservation(long Revision, IReadOnlyDictionary<string, BindingSourceProperty> Properties);
    private sealed class BindingSourcePropertyBag { public BindingSourceObservation? Observation; }

    private string BindingIdentity(BindingExpressionBase expression) =>
        _bindingIdentities.GetValue(expression, _ => new(Guid.NewGuid().ToString("N"))).Id;

    public async Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request)
    {
        BindingSourceResponse Reject(string message) => new(request, false, Status: message);
        static bool Identifier(string? value) => value is { Length: > 0 and <= 128 };
        if (!Identifier(request.NodeId) || !Identifier(request.PropertyId) || !Identifier(request.BindingId) ||
            !Identifier(request.ExpressionId) || !Identifier(request.DeclarationId) || request.Property is not { Length: > 0 and <= 1024 } ||
            request.OwnerType?.Length > 2048 || request.OwnerAssembly?.Length > 1024)
            return Reject("An observed element, property, expression, and declaration identity are required.");
        if (_editingDisposed || request.Revision != _revision || !_entries.TryGetValue(request.NodeId, out var entry) ||
            !entry.Target.TryGetTarget(out var target)) return Reject("The session, tree revision, or selected element changed.");
        try
        {
            return await OnDispatcherAsync(target.Dispatcher, token =>
            {
                token.ThrowIfCancellationRequested();
                if (_editingDisposed || request.Revision != _revision || !_entries.TryGetValue(request.NodeId, out var current) ||
                    !ReferenceEquals(current, entry) || !entry.Source.TryGetTarget(out var source) || source.IsDisposed ||
                    !entry.Root.TryGetTarget(out var root) || source.RootVisual != root || !IsAttached(target, root))
                    return Reject("The observed element has left its presentation tree, or the observation is stale.");
                if (!_bindingSourceProperties.TryGetValue(target, out var bag) || bag.Observation is not { } observation ||
                    observation.Revision != request.Revision || !observation.Properties.TryGetValue(request.PropertyId!, out var property) ||
                    property.Name != request.Property || request.OwnerType is not null && request.OwnerType != property.OwnerType ||
                    request.OwnerAssembly is not null && request.OwnerAssembly != property.OwnerAssembly ||
                    property.Sources.BindingId != request.BindingId || !property.Sources.Declarations.Any(declaration =>
                        declaration.ExpressionId == request.ExpressionId && declaration.DeclarationId == request.DeclarationId))
                    return Reject("The binding source identifiers do not match this property's current inspection snapshot.");
                if (!property.Root.TryGetTarget(out var observedRoot) || observedRoot.Target != target ||
                    observedRoot.TargetProperty != property.Property || observedRoot.Status is BindingStatus.Detached or BindingStatus.Unattached)
                    return Reject("The observed binding expression is no longer attached to this property.");
                // Use the same public WPF expression lookup as property inspection.
                // It does not explicitly traverse a source path; WPF may realize a
                // dormant style expression internally. Reject detached roots first.
                var expression = BindingOperations.GetBindingExpressionBase(target, property.Property);
                if (!ReferenceEquals(expression, observedRoot)) return Reject("The property's binding expression was replaced.");
                var result = _bindingSources.Validate(observedRoot, request);
                token.ThrowIfCancellationRequested();
                if (_editingDisposed || request.Revision != _revision || source.IsDisposed || source.RootVisual != root ||
                    !IsAttached(target, root) || observedRoot.Target != target || observedRoot.TargetProperty != property.Property)
                    return Reject("The binding source observation became stale during validation.");
                return result;
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Reject("Binding declaration validation could not complete (" + TypeName(exception.GetType()) + ")."); }
    }
}
