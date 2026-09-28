using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace WpfStudio.Wpf.PropertyEditing;

public sealed record PropertyEditInfo(bool CanEdit, string? EditableValue, bool IsNull, bool IsOverridden, string? Reason = null);
public sealed record PropertyEditResult(bool Success, string? Error = null, bool IsOverridden = false, bool Conflict = false);

/// <summary>
/// Owns temporary property values, not the target objects. Target operations must
/// run on their owning dispatcher. Reset restores property configuration; it
/// cannot undo arbitrary application callbacks caused by a property change.
/// </summary>
public sealed class TemporaryPropertyEdits
{
    private static readonly ConditionalWeakTable<BindingBase, object> Markers = new();
    private readonly ConditionalWeakTable<DependencyObject, TargetEdits> _targets = new();
    private readonly object _indexGate = new();
    private readonly List<IndexEntry> _index = [];
    private readonly int _maximumOverrides;
    private int _count;

    public TemporaryPropertyEdits(int maximumOverrides = 256)
    {
        if (maximumOverrides < 1) throw new ArgumentOutOfRangeException(nameof(maximumOverrides));
        _maximumOverrides = maximumOverrides;
    }

    public static bool IsOverrideBinding(BindingExpressionBase? expression) =>
        expression is not null && Markers.TryGetValue(expression.ParentBindingBase, out _);

    public PropertyEditInfo Describe(DependencyObject target, DependencyProperty property)
    {
        target.VerifyAccess();
        var edit = Find(target, property);
        bool owned = edit is not null && Owns(target, property, edit);
        string? reason = edit is not null && !owned ? "The application replaced this temporary value. Its current value is preserved." : Reason(target, property);
        object? value = target.GetValue(property);
        return new(reason is null, ScalarPropertyValues.ToEditableText(property.PropertyType, value), value is null, owned, reason);
    }

    public PropertyEditResult Validate(DependencyObject target, DependencyProperty property, string? text, bool isNull = false)
    {
        target.VerifyAccess();
        try
        {
            var edit = Find(target, property);
            if (edit is not null && !Owns(target, property, edit)) return Conflict();
            string? reason = Reason(target, property);
            if (reason is not null) return new(false, reason, edit is not null);
            if (!ScalarPropertyValues.TryConvert(property.PropertyType, text, isNull, out var value, out var error))
                return new(false, error, edit is not null);
            return property.IsValidValue(value) ? new(true, IsOverridden: edit is not null) : new(false, "The value is not valid for this property.", edit is not null);
        }
        catch (Exception exception) { return new(false, exception.GetBaseException().Message, Find(target, property) is { } current && Owns(target, property, current)); }
    }

    public PropertyEditResult Apply(DependencyObject target, DependencyProperty property, string? text, bool isNull = false)
    {
        target.VerifyAccess();
        var validation = Validate(target, property, text, isNull);
        if (!validation.Success) return validation;
        if (!ScalarPropertyValues.TryConvert(property.PropertyType, text, isNull, out var value, out var error)) return new(false, error);
        var bag = _targets.GetValue(target, key => new TargetEdits(new IndexEntry(new WeakReference<DependencyObject>(key))));
        bool created = !bag.Properties.TryGetValue(property, out var edit);
        if (created)
        {
            // Capture before reserving capacity; reading an application-owned
            // deferred value can fail and must not leak a reserved slot.
            try { edit = new(target.ReadLocalValue(property), BindingOperations.GetBindingBase(target, property)); }
            catch (Exception exception) { return new(false, exception.GetBaseException().Message); }
            if (!Reserve(bag.Index)) return new(false, $"The session limit of {_maximumOverrides} temporary properties has been reached. Reset an existing edit first.");
            bag.Properties.Add(property, edit);
        }
        var previousMarker = edit!.Marker;
        var marker = new Binding(nameof(ConstantValue.Value)) { Source = new ConstantValue(value), Mode = BindingMode.OneWay };
        Markers.Add(marker, new object());
        edit.Marker = marker;
        try
        {
            // Replacing an expression with another expression bypasses the old
            // BindingExpression.SetValue path, which can write a TwoWay source.
            BindingOperations.SetBinding(target, property, marker);
            if (!Owns(target, property, edit)) return Conflict();
            return new(true, IsOverridden: true);
        }
        catch (Exception exception)
        {
            if (previousMarker is not null && ReferenceEquals(BindingOperations.GetBindingBase(target, property), previousMarker))
                edit.Marker = previousMarker;
            else if (created && OriginalStillPresent(target, property, edit)) Remove(bag, property, edit);
            return new(false, exception.GetBaseException().Message, Owns(target, property, edit));
        }
    }

    public PropertyEditResult Reset(DependencyObject target, DependencyProperty property)
    {
        target.VerifyAccess();
        if (!_targets.TryGetValue(target, out var bag) || !bag.Properties.TryGetValue(property, out var edit)) return new(true);
        if (!Owns(target, property, edit)) return Conflict();
        try
        {
            // Style/template/inherited values must regain their original
            // precedence. A style binding must never become a local binding.
            if (ReferenceEquals(edit.OriginalLocalValue, DependencyProperty.UnsetValue)) target.ClearValue(property);
            else if (edit.OriginalBinding is not null) BindingOperations.SetBinding(target, property, edit.OriginalBinding);
            else target.SetValue(property, edit.OriginalLocalValue);
            if (!OriginalStillPresent(target, property, edit)) return Conflict();
            Remove(bag, property, edit);
            return new(true);
        }
        catch (Exception exception)
        {
            // Retain the original record on failure so a partial restoration is
            // never reported as a successfully completed reset.
            return new(false, exception.GetBaseException().Message, Owns(target, property, edit));
        }
    }

    public IReadOnlyList<PropertyEditResult> ResetAll(DependencyObject target)
    {
        target.VerifyAccess();
        return _targets.TryGetValue(target, out var bag) ? bag.Properties.Keys.ToArray().Select(property => Reset(target, property)).ToArray() : [];
    }

    public IReadOnlyList<WeakReference<DependencyObject>> GetEditedTargets()
    {
        lock (_indexGate)
        {
            Prune();
            return _index.Select(entry => entry.Target).ToArray();
        }
    }

    private OriginalValue? Find(DependencyObject target, DependencyProperty property) =>
        _targets.TryGetValue(target, out var bag) && bag.Properties.TryGetValue(property, out var edit) ? edit : null;

    private static bool Owns(DependencyObject target, DependencyProperty property, OriginalValue edit) =>
        edit.Marker is not null && ReferenceEquals(BindingOperations.GetBindingBase(target, property), edit.Marker);

    private static bool OriginalStillPresent(DependencyObject target, DependencyProperty property, OriginalValue edit) =>
        !ReferenceEquals(edit.OriginalLocalValue, DependencyProperty.UnsetValue) && edit.OriginalBinding is not null
            ? ReferenceEquals(BindingOperations.GetBindingBase(target, property), edit.OriginalBinding)
            : ReferenceEquals(target.ReadLocalValue(property), edit.OriginalLocalValue);

    private static PropertyEditResult Conflict() => new(false, "The application replaced this temporary value. Its current value was preserved.", Conflict: true);

    private static string? Reason(DependencyObject target, DependencyProperty property)
    {
        if (target.IsSealed || property.ReadOnly) return "This property is read-only.";
        if (!ScalarPropertyValues.Supports(property.PropertyType)) return "This property type does not support scalar editing.";
        if (target.GetValue(property) is string { Length: > ScalarPropertyValues.MaximumTextLength })
            return $"The current text exceeds the {ScalarPropertyValues.MaximumTextLength}-character editing limit. Its full value is omitted; temporary editing is unavailable.";
        if (property.GetMetadata(target) is FrameworkPropertyMetadata metadata && !metadata.IsDataBindingAllowed)
            return "This property does not support a reversible binding override.";
        if (target is TextBlock text && property == TextBlock.TextProperty && LogicalTreeHelper.GetChildren(text).OfType<Inline>().Any())
            return "Editing Text would replace existing inline objects. Edit the individual Run instead.";
        var source = DependencyPropertyHelper.GetValueSource(target, property);
        if (source.IsAnimated) return "Animated properties cannot be temporarily overridden.";
        var binding = BindingOperations.GetBindingBase(target, property);
        if (source.IsExpression && binding is null) return "Dynamic resources and other expressions cannot be safely restored after an override.";
        if (HasOneWayToSource(binding)) return "OneWayToSource bindings cannot be temporarily overridden because restoring them can write to the application source.";
        if (property.PropertyType == typeof(object) && target.GetValue(property) is not null and not string)
            return "Replacing complex object content is not supported by temporary scalar edits.";
        return null;
    }

    private static bool HasOneWayToSource(BindingBase? binding) => binding switch
    {
        Binding simple => simple.Mode == BindingMode.OneWayToSource,
        MultiBinding multi => multi.Mode == BindingMode.OneWayToSource || multi.Bindings.Any(HasOneWayToSource),
        PriorityBinding priority => priority.Bindings.Any(HasOneWayToSource),
        _ => false
    };

    private bool Reserve(IndexEntry entry)
    {
        lock (_indexGate)
        {
            Prune();
            if (_count >= _maximumOverrides) return false;
            if (entry.Count++ == 0) _index.Add(entry);
            _count++;
            return true;
        }
    }

    private void Remove(TargetEdits bag, DependencyProperty property, OriginalValue edit)
    {
        // An application callback may pump its dispatcher and permit cleanup or
        // another operation to run before an outer property operation returns.
        if (!bag.Properties.TryGetValue(property, out var current) || !ReferenceEquals(current, edit)) return;
        bag.Properties.Remove(property);
        lock (_indexGate)
        {
            _count--;
            if (--bag.Index.Count == 0) _index.Remove(bag.Index);
        }
    }

    private void Prune()
    {
        for (int i = _index.Count - 1; i >= 0; i--)
            if (!_index[i].Target.TryGetTarget(out _)) { _count -= _index[i].Count; _index.RemoveAt(i); }
    }

    private sealed class IndexEntry(WeakReference<DependencyObject> target)
    {
        public WeakReference<DependencyObject> Target { get; } = target;
        public int Count;
    }
    private sealed class TargetEdits(IndexEntry index)
    {
        public IndexEntry Index { get; } = index;
        public Dictionary<DependencyProperty, OriginalValue> Properties { get; } = [];
    }
    private sealed class OriginalValue(object localValue, BindingBase? binding)
    {
        public object OriginalLocalValue { get; } = localValue;
        public BindingBase? OriginalBinding { get; } = binding;
        public Binding? Marker { get; set; }
    }
    private sealed class ConstantValue(object? value)
    {
        public object? Value { get; } = value;
    }
}
