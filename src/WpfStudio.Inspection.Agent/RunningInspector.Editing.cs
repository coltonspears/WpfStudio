using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.Inspection.Agent;

internal sealed partial class RunningInspector
{
    private const int MaximumEditOperations = 1024;
    private readonly TemporaryPropertyEdits _propertyEdits = new();
    private readonly ConditionalWeakTable<DependencyProperty, Identity> _propertyIdentities = new();
    private readonly ConditionalWeakTable<DependencyObject, ObservationBag> _propertyObservations = new();
    private readonly object _editGate = new();
    private readonly Dictionary<string, EditOperation> _editOperations = new(StringComparer.Ordinal);
    private volatile bool _editingDisposed;

    private sealed class ObservationBag
    {
        public Dictionary<string, ObservedProperty> Properties = new(StringComparer.Ordinal);
    }
    private sealed record ObservedProperty(DependencyProperty Property, string Token, long Revision, PropertyState State,
        bool CanApply, string? UnavailableReason);
    private sealed class EditOperation(string fingerprint, string id)
    {
        public string Fingerprint { get; } = fingerprint;
        public InspectionPropertyEditResult Result = new(id, "Unknown", Error: "The operation has not finished. Check its outcome before taking another action.");
    }

    private InspectionProperty DescribeEditableProperty(DependencyObject target, DependencyProperty property,
        InspectionProperty display, Dictionary<string, ObservedProperty> observations, ref int editableTextRemaining)
    {
        var info = _propertyEdits.Describe(target, property);
        int remainingBindings = 65;
        if (info.CanEdit && !CanObserveBindings(BindingOperations.GetBindingExpressionBase(target, property), ref remainingBindings))
            info = info with { CanEdit = false, Reason = "This binding has too many child expressions to verify its complete observed state safely." };
        // Keep the actual full value for stale-state verification, even when the
        // response can only offer Reset for an existing inspector-owned override.
        var stateInfo = info;
        if (info.EditableValue is { } editable)
        {
            if (editable.Length > editableTextRemaining)
                info = info with
                {
                    EditableValue = null, CanEdit = false,
                    Reason = "The editable-text budget for this property snapshot was reached. The full value was omitted; it was not truncated. Existing temporary overrides can still be reset."
                };
            else editableTextRemaining -= editable.Length;
        }
        string id = _propertyIdentities.GetValue(property, _ => new(Guid.NewGuid().ToString("N"))).Id;
        string? token = null;
        if (!_editingDisposed && (info.CanEdit || info.IsOverridden))
        {
            var state = PropertyState.Capture(target, property, stateInfo);
            if (!state.Bindings.All(binding => binding.Observable))
                info = info with { CanEdit = false, Reason = "The binding's cached source state cannot be verified safely with this WPF implementation. Refresh after the binding becomes available." };
            if (info.CanEdit || info.IsOverridden)
            {
                var old = _propertyObservations.GetOrCreateValue(target).Properties.GetValueOrDefault(id);
                token = old is not null && old.State.SameAs(state)
                    ? old.Token : Guid.NewGuid().ToString("N");
                observations[id] = new(property, token, _revision, state, info.CanEdit, info.Reason);
            }
        }
        return display with
        {
            PropertyId = id, EditToken = token, CanEdit = !_editingDisposed && info.CanEdit,
            EditableValue = info.EditableValue, IsNull = info.IsNull, IsOverridden = info.IsOverridden,
            EditUnavailableReason = _editingDisposed ? "The inspection session has ended." : info.Reason
        };
    }

    private static bool CanObserveBindings(BindingExpressionBase? expression, ref int remaining)
    {
        if (expression is null) return true;
        if (--remaining < 0) return false;
        if (expression is MultiBindingExpression multi)
        {
            if (multi.BindingExpressions.Count > remaining) return false;
            foreach (var child in multi.BindingExpressions)
                if (!CanObserveBindings(child, ref remaining)) return false;
        }
        else if (expression is PriorityBindingExpression priority)
        {
            if (priority.BindingExpressions.Count > remaining) return false;
            foreach (var child in priority.BindingExpressions)
                if (!CanObserveBindings(child, ref remaining)) return false;
        }
        return true;
    }

    public async Task<InspectionPropertyValidation> ValidatePropertyAsync(InspectionPropertyEdit request, CancellationToken cancellationToken = default)
    {
        if (InvalidRequest(request) is { } invalid) return new(false, invalid);
        if (!TryEditTarget(request, out var target, out _, out var error)) return new(false, error);
        try
        {
            return await OnEditDispatcherAsync<InspectionPropertyValidation>(target.Dispatcher, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryObservedProperty(request, out var currentTarget, out var property, out var failure)) return new(false, failure);
                if (request.Reset)
                {
                    bool owned = _propertyEdits.Describe(currentTarget, property).IsOverridden;
                    return new(owned, owned ? null : "The inspector no longer owns an override on this property.");
                }
                var result = _propertyEdits.Validate(currentTarget, property, request.Value, request.IsNull);
                return new(result.Success, result.Error);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { return new(false, Limit(exception.GetBaseException().Message, 1500)); }
    }

    public Task<InspectionPropertyEditResult> GetEditStatusAsync(InspectionEditStatusRequest request)
    {
        lock (_editGate)
            return Task.FromResult(!string.IsNullOrEmpty(request.OperationId) && _editOperations.TryGetValue(request.OperationId, out var operation)
                ? operation.Result
                : new InspectionPropertyEditResult(request.OperationId, "Rejected", Error: "This operation ID has not been accepted by this inspection session."));
    }

    public async Task<InspectionPropertyEditResult> SetPropertyAsync(InspectionPropertyEdit request, CancellationToken cancellationToken = default)
    {
        if (InvalidRequest(request) is { } invalid) return new(request.OperationId, "Rejected", Error: invalid);
        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, InspectionProtocol.JsonOptions)));
        lock (_editGate)
        {
            if (_editOperations.TryGetValue(request.OperationId, out var previous))
                return previous.Fingerprint == fingerprint ? previous.Result
                    : new(request.OperationId, "Rejected", Error: "An operation ID cannot be reused for a different request.");
            if (_editingDisposed) return new(request.OperationId, "Rejected", Error: "The inspection session has ended.");
            if (_editOperations.Count >= MaximumEditOperations)
                return new(request.OperationId, "Rejected", Error: "The session operation limit was reached. Disconnect to restore overrides and begin a new inspection session.");
            _editOperations.Add(request.OperationId, new(fingerprint, request.OperationId));
        }
        if (!TryEditTarget(request, out var target, out _, out var error))
            return CompleteEdit(new(request.OperationId, "Conflict", Error: error));
        if (cancellationToken.IsCancellationRequested)
            return CompleteEdit(new(request.OperationId, "Rejected", Error: "The edit was cancelled before it started."));

        // 0 = queued, 1 = executing, -1 = cancelled before execution. The gate
        // remains authoritative even if DispatcherOperation.Abort races its start.
        int execution = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        DispatcherOperation<InspectionPropertyEditResult>? queued = null;
        try
        {
            if (target.Dispatcher.HasShutdownStarted || target.Dispatcher.HasShutdownFinished)
                return CompleteEdit(new(request.OperationId, "Rejected", Error: "The owning dispatcher has shut down."));
            queued = target.Dispatcher.InvokeAsync(() =>
            {
                if (Interlocked.CompareExchange(ref execution, 1, 0) != 0 || timeout.IsCancellationRequested)
                    return CompleteEdit(new(request.OperationId, "Rejected", Error: "The queued edit was cancelled before it started."));
                return ExecutePropertyEdit(request, cancellationToken);
            }, DispatcherPriority.Background, timeout.Token);
            return await queued.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (queued?.Task.IsCompletedSuccessfully == true) return queued.Task.Result;
            if (Interlocked.CompareExchange(ref execution, -1, 0) == 0)
            {
                queued?.Abort();
                return CompleteEdit(new(request.OperationId, "Rejected", Error: "The queued edit was cancelled before it started."));
            }
            // The running callback owns final completion; querying its ID can
            // obtain that result later. Reusing the ID never runs the edit twice.
            return new(request.OperationId, "Unknown", Error: "The property callback started but has not confirmed its outcome. Check the operation status; do not repeat the mutation.");
        }
        catch (Exception exception)
        {
            return CompleteEdit(new(request.OperationId, Volatile.Read(ref execution) == 0 ? "Rejected" : "Unknown",
                Error: Limit(exception.GetBaseException().Message, 1500)));
        }
    }

    private InspectionPropertyEditResult ExecutePropertyEdit(InspectionPropertyEdit request, CancellationToken cancellationToken)
    {
        DependencyObject? target = null;
        bool mutationStarted = false;
        try
        {
            if (cancellationToken.IsCancellationRequested || _editingDisposed)
                return CompleteEdit(new(request.OperationId, "Rejected", Error: "The edit was cancelled before mutation."));
            if (!TryObservedProperty(request, out target, out var property, out var error))
                return CompleteEdit(new(request.OperationId, "Conflict", Error: error));
            if (!request.Reset)
            {
                var validation = _propertyEdits.Validate(target, property, request.Value, request.IsNull);
                if (!validation.Success)
                    return CompleteEdit(new(request.OperationId, validation.Conflict ? "Conflict" : "Rejected", Error: validation.Error));
            }
            cancellationToken.ThrowIfCancellationRequested();
            mutationStarted = true;
            var result = request.Reset ? _propertyEdits.Reset(target, property)
                : _propertyEdits.Apply(target, property, request.Value, request.IsNull);
            if (!result.Success)
                return CompleteEdit(new(request.OperationId, result.Conflict ? "Conflict" : "Unknown", Error: result.Error));
            InspectionElement? element = null;
            string? refreshError = null;
            try { element = ReadElement(new(request.Revision, request.NodeId), target, CancellationToken.None); }
            catch (Exception exception) { refreshError = "The edit completed, but refreshing its properties failed: " + Limit(exception.GetBaseException().Message, 1000); }
            return CompleteEdit(new(request.OperationId, request.Reset ? "Reset" : "Applied", element, refreshError));
        }
        catch (Exception exception)
        {
            return CompleteEdit(new(request.OperationId, mutationStarted ? "Unknown" : "Rejected",
                Error: Limit(exception.GetBaseException().Message, 1500)));
        }
        finally
        {
            // EOF can arrive while user coercion/change callbacks are executing.
            // Restore after they finish even if disposal's weak-index pass ran
            // before the shared engine registered this newly applied override.
            if (_editingDisposed && target is not null)
                try { _propertyEdits.ResetAll(target); } catch (Exception) { }
        }
    }

    private InspectionPropertyEditResult CompleteEdit(InspectionPropertyEditResult result)
    {
        lock (_editGate)
            if (_editOperations.TryGetValue(result.OperationId, out var operation))
                operation.Result = result with { Element = null }; // Do not retain snapshots/model graphs for every operation.
        return result;
    }

    private bool TryEditTarget(InspectionPropertyEdit request, out DependencyObject target, out Entry entry, out string? error)
    {
        target = null!;
        entry = null!;
        error = null;
        if (_editingDisposed) error = "The inspection session has ended.";
        else if (request.Revision != _revision) error = "The tree revision changed. Refresh the element before editing.";
        else if (!_entries.TryGetValue(request.NodeId, out entry!) || !entry.Target.TryGetTarget(out target!))
            error = "The selected element is no longer available.";
        return error is null;
    }

    private bool TryObservedProperty(InspectionPropertyEdit request, out DependencyObject target, out DependencyProperty property, out string? error)
    {
        property = null!;
        if (!TryEditTarget(request, out target, out var entry, out error)) return false;
        target.VerifyAccess();
        if (!entry.Source.TryGetTarget(out var source) || source.IsDisposed || !entry.Root.TryGetTarget(out var root)
            || source.RootVisual != root || !IsAttached(target, root))
            error = "The selected element has left its observed presentation tree.";
        else if (!_propertyObservations.TryGetValue(target, out var bag)
            || !bag.Properties.TryGetValue(request.PropertyId, out var observed)
            || observed.Revision != request.Revision || observed.Token != request.EditToken)
            error = "The observed property token is stale or does not belong to this element. Refresh its properties.";
        else
        {
            property = observed.Property;
            var info = _propertyEdits.Describe(target, property);
            if (!observed.State.SameAs(PropertyState.Capture(target, property, info)))
                error = "The property value, binding, source, or override ownership changed since it was inspected. Refresh before editing.";
            else if (!request.Reset && (!observed.CanApply || !info.CanEdit))
                error = observed.UnavailableReason ?? info.Reason ?? "This property cannot be edited safely.";
        }
        return error is null;
    }

    private static string? InvalidRequest(InspectionPropertyEdit request) =>
        string.IsNullOrEmpty(request.OperationId) || request.OperationId.Length > 128
        || string.IsNullOrEmpty(request.NodeId) || request.NodeId.Length > 128
        || string.IsNullOrEmpty(request.PropertyId) || request.PropertyId.Length > 128
        || string.IsNullOrEmpty(request.EditToken) || request.EditToken.Length > 128
            ? "The request does not contain valid observed property and operation identifiers."
            : request.Value?.Length > 1_000_000 ? "The requested property value is too large." : null;

    private static async Task<T> OnEditDispatcherAsync<T>(Dispatcher dispatcher, Func<T> action, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var operation = dispatcher.InvokeAsync(() => { timeout.Token.ThrowIfCancellationRequested(); return action(); }, DispatcherPriority.Background, timeout.Token);
        try { return await operation.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch { operation.Abort(); throw; }
    }

    private async Task RestorePropertyEditsAsync()
    {
        _editingDisposed = true;
        var engine = _propertyEdits;
        var pending = new List<Task>();
        foreach (var weak in engine.GetEditedTargets())
        {
            if (!weak.TryGetTarget(out var target)) continue;
            var dispatcher = target.Dispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) continue;
            try
            {
                var operation = dispatcher.InvokeAsync(() =>
                {
                    if (weak.TryGetTarget(out var alive))
                        try { engine.ResetAll(alive); } catch (Exception) { }
                }, DispatcherPriority.Send);
                pending.Add(WaitForRestoreAsync(operation.Task));
            }
            catch (Exception) { }
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private static async Task WaitForRestoreAsync(Task operation)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { } // Keep weak cleanup queued for a suspended dispatcher.
    }

    private sealed record PropertyState(ValueStamp Local, ValueStamp Effective, ValueSource Source, string? EditableValue, bool IsNull,
        bool IsOverridden, ValueStamp DataContext, ValueStamp Parent, ValueStamp Style, IReadOnlyList<ExpressionState> Bindings)
    {
        public static PropertyState Capture(DependencyObject target, DependencyProperty property, PropertyEditInfo info)
        {
            var bindings = new List<ExpressionState>();
            ReadExpressions(BindingOperations.GetBindingExpressionBase(target, property), bindings);
            return new(ValueStamp.Create(target.ReadLocalValue(property)), ValueStamp.Create(target.GetValue(property)), DependencyPropertyHelper.GetValueSource(target, property),
                info.EditableValue, info.IsNull, info.IsOverridden,
                ValueStamp.Create(target is FrameworkElement element ? element.DataContext : target is FrameworkContentElement content ? content.DataContext : null),
                ValueStamp.Create(LogicalTreeHelper.GetParent(target)),
                ValueStamp.Create(target is FrameworkElement styled ? styled.Style : target is FrameworkContentElement styledContent ? styledContent.Style : null), bindings);
        }

        public bool SameAs(PropertyState other) => Local.SameAs(other.Local) && Effective.SameAs(other.Effective) && Source.Equals(other.Source)
            && string.Equals(EditableValue, other.EditableValue, StringComparison.Ordinal) && IsNull == other.IsNull
            && IsOverridden == other.IsOverridden && DataContext.SameAs(other.DataContext) && Parent.SameAs(other.Parent)
            && Style.SameAs(other.Style) && Bindings.Count == other.Bindings.Count
            && Bindings.Zip(other.Bindings).All(pair => pair.First.SameAs(pair.Second));

        private static void ReadExpressions(BindingExpressionBase? expression, List<ExpressionState> result)
        {
            if (expression is null || result.Count >= 65) return;
            var single = expression as BindingExpression;
            result.Add(new(ValueStamp.Create(expression), expression.Status, ValueStamp.Create(single?.DataItem),
                ValueStamp.Create(single?.ResolvedSource), single is null, single is null ? null : BindingPathStateReader.CaptureIdentity(single)));
            if (expression is MultiBindingExpression multi)
                foreach (var child in multi.BindingExpressions.Take(64)) ReadExpressions(child, result);
            else if (expression is PriorityBindingExpression priority)
                foreach (var child in priority.BindingExpressions.Take(64)) ReadExpressions(child, result);
        }
    }

    private sealed record ExpressionState(ValueStamp Identity, BindingStatus Status, ValueStamp Source, ValueStamp ResolvedSource,
        bool Composite, BindingPathStateReader.CachedIdentity? PathIdentity)
    {
        public bool Observable => Composite || PathIdentity is not null;
        public bool SameAs(ExpressionState other) => Identity.SameAs(other.Identity) && Status == other.Status
            && Source.SameAs(other.Source) && ResolvedSource.SameAs(other.ResolvedSource) && Composite == other.Composite
            && (Composite || PathIdentity?.Matches(other.PathIdentity) == true);
    }

    private sealed class ValueStamp
    {
        private object? _scalar;
        private WeakReference<object>? _reference;
        public static ValueStamp Create(object? value) => value switch
        {
            null => new(),
            string or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                or DateTime or DateTimeOffset or TimeSpan or Guid or Enum or Thickness or CornerRadius or GridLength or Point or Size or Rect or Color => new() { _scalar = value },
            _ => new() { _reference = new(value) }
        };
        public bool SameAs(ValueStamp other)
        {
            if (_reference is not null || other._reference is not null)
                return _reference is not null && other._reference is not null && _reference.TryGetTarget(out var left)
                    && other._reference.TryGetTarget(out var right) && ReferenceEquals(left, right);
            return _scalar is null ? other._scalar is null : _scalar.GetType() == other._scalar?.GetType() && _scalar.Equals(other._scalar);
        }
    }
}
