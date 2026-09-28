using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.Inspection.Agent;

internal sealed partial class RunningInspector
{
    private sealed class SourceObservationBag { public Dictionary<string, SourceObservation> Properties = new(StringComparer.Ordinal); }
    private sealed record SourceObservation(DependencyProperty Property, string Token, long Revision,
        PropertyState State, InspectionSourcePropertyIdentity Identity);
    private readonly ConditionalWeakTable<DependencyObject, SourceObservationBag> _sourceObservations = new();

    private InspectionProperty DescribeSourceProperty(DependencyObject target, DependencyProperty property, InspectionProperty display,
        PropertyEditInfo fullInfo, Dictionary<string, SourceObservation> observations)
    {
        string? reason = null;
        InspectionSourcePropertyIdentity? identity = null;
        int remaining = 65;
        if (_editingDisposed) reason = "The inspection session has ended.";
        else if (target.GetValue(property) is string { Length: > ScalarPropertyValues.MaximumTextLength }) reason = "The current scalar text exceeds the source observation limit.";
        else if (fullInfo.EditableValue is not null && display.EditableValue is null) reason = "The scalar text budget was reached; this property's complete source value was not included.";
        else if (!CanObserveBindings(BindingOperations.GetBindingExpressionBase(target, property), ref remaining)) reason = "This binding has too many children to verify its complete observed state.";
        else identity = SourcePropertyMetadata.Read(target.GetType(), property, out reason);
        string? token = null;
        if (identity is not null && display.PropertyId is { } id)
        {
            var state = PropertyState.Capture(target, property, fullInfo);
            var old = _sourceObservations.GetOrCreateValue(target).Properties.GetValueOrDefault(id);
            token = old is not null && old.State.SameAs(state) ? old.Token : Guid.NewGuid().ToString("N");
            observations[id] = new(property, token, _revision, state, identity);
        }
        return display with { SourceEditToken = token, CanWriteSource = token is not null, SourceUnavailableReason = reason };
    }

    public async Task<InspectionSourcePropertyResult> ValidateSourcePropertyAsync(InspectionSourcePropertyRequest request, CancellationToken cancellationToken = default)
    {
        InspectionSourcePropertyResult Reject(string? error) => new(false, request.Revision, request.NodeId, request.PropertyId, request.SourceEditToken, Error: error);
        if (string.IsNullOrEmpty(request.NodeId) || request.NodeId.Length > 128 || string.IsNullOrEmpty(request.PropertyId) || request.PropertyId.Length > 128
            || string.IsNullOrEmpty(request.SourceEditToken) || request.SourceEditToken.Length > 128 || request.Value?.Length > ScalarPropertyValues.MaximumTextLength)
            return Reject("The request does not contain valid observed property identifiers or bounded scalar text.");
        if (_editingDisposed || request.Revision != _revision || !_entries.TryGetValue(request.NodeId, out var entry) || !entry.Target.TryGetTarget(out var target))
            return Reject("The observed element or tree revision is stale. Refresh before preparing a source edit.");
        try
        {
            return await OnEditDispatcherAsync(target.Dispatcher, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TrySourceObservation(request, out var currentTarget, out var observed, out var error)) return Reject(error);
                if (request.VerifyOnly || request.Remove)
                    return new(true, request.Revision, request.NodeId, request.PropertyId, request.SourceEditToken, observed.Identity);
                if (!ScalarPropertyValues.TryConvert(observed.Property.PropertyType, request.Value, request.IsNull, out var value, out error)) return Reject(error);
                // Explicit validation may invoke the registered DP validation
                // callback. It never invokes a CLR wrapper, application converter,
                // coercion callback, SetValue, UpdateTarget, or UpdateSource.
                bool valid = observed.Property.IsValidValue(value);
                cancellationToken.ThrowIfCancellationRequested();
                if (!TrySourceObservation(request, out _, out var after, out error) || !ReferenceEquals(after, observed))
                    return Reject(error ?? "The property observation changed during its validation callback.");
                if (!valid) return Reject("The scalar value was rejected by this dependency property's validation rule.");
                string? literal = request.IsNull ? null : ScalarPropertyValues.ToEditableText(observed.Property.PropertyType, value);
                if (!request.IsNull && literal is null) return Reject("This scalar value has no faithful invariant XAML text representation.");
                return new(true, request.Revision, request.NodeId, request.PropertyId, request.SourceEditToken, observed.Identity, literal, request.IsNull);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An application validation exception may override Message. Read its
            // runtime type only; do not execute additional application code.
            return Reject("Source validation could not complete (" + TypeName(exception.GetType()) + "). The inspector did not set the live property.");
        }
    }

    private bool TrySourceObservation(InspectionSourcePropertyRequest request, out DependencyObject target,
        out SourceObservation observed, out string? error)
    {
        target = null!; observed = null!; error = null;
        if (_editingDisposed || request.Revision != _revision) error = "The inspection session or tree revision changed.";
        else if (!_entries.TryGetValue(request.NodeId, out var entry) || !entry.Target.TryGetTarget(out target!)) error = "The observed element is no longer available.";
        else
        {
            target.VerifyAccess();
            if (!entry.Source.TryGetTarget(out var source) || source.IsDisposed || !entry.Root.TryGetTarget(out var root)
                || source.RootVisual != root || !IsAttached(target, root)) error = "The selected element has left the observed presentation tree.";
            else if (!_sourceObservations.TryGetValue(target, out var bag) || !bag.Properties.TryGetValue(request.PropertyId, out observed!)
                || observed.Revision != request.Revision || observed.Token != request.SourceEditToken) error = "The source property observation token is stale or belongs to a different property.";
            else if (!observed.State.SameAs(PropertyState.Capture(target, observed.Property, _propertyEdits.Describe(target, observed.Property))))
                error = "The property value, expression, source, or override changed since inspection. Refresh before preparing a source edit.";
        }
        return error is null;
    }
}
