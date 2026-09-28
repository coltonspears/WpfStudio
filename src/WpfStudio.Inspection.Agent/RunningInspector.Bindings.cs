using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Inspection.Agent;

internal sealed partial class RunningInspector
{
    private const int MaximumBindingObservations = 1000;
    private const int MaximumPropertiesPerNode = 512;
    private sealed record BindingScan(List<InspectionBindingObservation> Observations, List<string> ScannedNodes, bool Truncated);
    private sealed record PropertyCandidates(DependencyProperty[] Properties, bool Complete);
    private sealed class ScanCursor { public int Offset; }
    private readonly ConditionalWeakTable<BindingExpressionBase, Identity> _bindingIdentities = new();
    private readonly ConditionalWeakTable<DependencyObject, HashSet<DependencyProperty>> _knownBindingProperties = new();
    private readonly ConditionalWeakTable<Type, PropertyCandidates> _declaredProperties = new();
    private readonly ConditionalWeakTable<PresentationSource, ScanCursor> _bindingNodeCursors = new();
    private readonly ConditionalWeakTable<DependencyObject, ScanCursor> _bindingPropertyCursors = new();

    private BindingScan ScanBindings(PresentationSource source, Dictionary<string, Entry> entries, int maximumBindings, CancellationToken token)
    {
        var observations = new List<InspectionBindingObservation>();
        var detailBudget = new BindingDetailBudget(maximumBindings * 256);
        var scanned = new List<string>();
        var budget = Stopwatch.StartNew();
        bool truncated = false;
        var candidatesByNode = entries.ToArray();
        var nodeCursor = _bindingNodeCursors.GetOrCreateValue(source);
        int start = candidatesByNode.Length == 0 ? 0 : nodeCursor.Offset % candidatesByNode.Length;
        int visited = 0;
        for (; visited < candidatesByNode.Length; visited++)
        {
            token.ThrowIfCancellationRequested();
            if (budget.ElapsedMilliseconds >= 120 || observations.Count >= maximumBindings)
            {
                truncated = true;
                break;
            }
            var pair = candidatesByNode[(start + visited) % candidatesByNode.Length];
            if (!pair.Value.Target.TryGetTarget(out var target) || !target.CheckAccess()) { truncated = true; continue; }
            bool complete = true;
            try
            {
                var candidates = BindingProperties(target, budget, token);
                complete = candidates.Complete;
                var known = _knownBindingProperties.GetOrCreateValue(target);
                var propertyCursor = _bindingPropertyCursors.GetOrCreateValue(target);
                int propertyStart = candidates.Properties.Length == 0 ? 0 : propertyCursor.Offset % candidates.Properties.Length;
                int checkedProperties = 0;
                for (; checkedProperties < candidates.Properties.Length; checkedProperties++)
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.ElapsedMilliseconds >= 120 || observations.Count >= maximumBindings) { complete = false; break; }
                    var property = candidates.Properties[(propertyStart + checkedProperties) % candidates.Properties.Length];
                    try
                    {
                        // This public API reads WPF's expression store, including style and
                        // template expressions. We do not separately evaluate source paths;
                        // normal WPF lookup can realize a dormant style expression internally.
                        if (BindingOperations.GetBindingExpressionBase(target, property) is not { } expression) continue;
                        if (Wpf.PropertyEditing.TemporaryPropertyEdits.IsOverrideBinding(expression)) continue;
                        if (known.Count < MaximumPropertiesPerNode) known.Add(property);
                        else if (!known.Contains(property)) complete = false;
                        string id = _bindingIdentities.GetValue(expression, _ => new(Guid.NewGuid().ToString("N"))).Id;
                        observations.Add(new(id, pair.Key, PropertyName(target, property), TypeName(property.OwnerType),
                            property.OwnerType.Assembly.GetName().Name ?? "", detailBudget.Take(BindingReader.Read(expression, traces.Evidence))));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException) { complete = false; }
                }
                // A large node does not monopolize the next poll, nor does its first
                // property monopolize every future visit to that node.
                propertyCursor.Offset = candidates.Properties.Length == 0 ? 0
                    : (propertyStart + checkedProperties) % candidates.Properties.Length;
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { complete = false; }
            if (complete) scanned.Add(pair.Key);
            else truncated = true;
        }
        // Each source receives its own output share, so later dispatchers are never
        // scanned and then permanently discarded by an earlier source's bindings.
        nodeCursor.Offset = candidatesByNode.Length == 0 ? 0
            : (start + Math.Max(1, visited)) % candidatesByNode.Length;
        return new(observations, scanned, truncated);
    }

    private PropertyCandidates BindingProperties(DependencyObject target, Stopwatch budget, CancellationToken token)
    {
        var properties = new HashSet<DependencyProperty>();
        bool complete = true;
        bool Add(DependencyProperty? property)
        {
            token.ThrowIfCancellationRequested();
            if (budget.ElapsedMilliseconds >= 120 || properties.Count >= MaximumPropertiesPerNode && property is not null && !properties.Contains(property))
            { complete = false; return false; }
            if (property is not null) properties.Add(property);
            return true;
        }

        // Revisit every previously seen bound DP even if a style/template was replaced.
        // Thus a complete node scan can prove that an old expression is no longer there.
        if (_knownBindingProperties.TryGetValue(target, out var known))
            foreach (var property in known) if (!Add(property)) return new(properties.ToArray(), false);
        var local = target.GetLocalValueEnumerator();
        while (local.MoveNext()) if (!Add(local.Current.Property)) return new(properties.ToArray(), false);
        var declared = _declaredProperties.GetValue(target.GetType(), DeclaredProperties);
        complete &= declared.Complete;
        foreach (var property in declared.Properties) if (!Add(property)) return new(properties.ToArray(), false);

        var styles = new HashSet<Style>(ReferenceEqualityComparer.Instance);
        bool AddSetters(SetterBaseCollection setters)
        {
            int count = 0;
            foreach (var setter in setters)
            {
                if (++count > 2048 || budget.ElapsedMilliseconds >= 120) { complete = false; return false; }
                if (setter is Setter propertySetter && !Add(propertySetter.Property)) return false;
            }
            return true;
        }
        bool AddTriggers(TriggerCollection triggers)
        {
            int count = 0;
            foreach (var trigger in triggers)
            {
                if (++count > 512 || budget.ElapsedMilliseconds >= 120) { complete = false; return false; }
                var setters = trigger switch
                {
                    Trigger value => value.Setters,
                    MultiTrigger value => value.Setters,
                    DataTrigger value => value.Setters,
                    MultiDataTrigger value => value.Setters,
                    _ => null
                };
                if (setters is not null && !AddSetters(setters)) return false;
            }
            return true;
        }
        bool AddStyle(Style? style)
        {
            while (style is not null && styles.Add(style))
            {
                if (styles.Count > 64 || !AddSetters(style.Setters) || !AddTriggers(style.Triggers)) { complete = false; return false; }
                style = style.BasedOn;
            }
            return true;
        }
        bool AddTemplate(FrameworkTemplate? template) => template switch
        {
            ControlTemplate control => AddTriggers(control.Triggers),
            DataTemplate data => AddTriggers(data.Triggers),
            _ => true
        };
        if (target is FrameworkElement element)
        {
            if (!AddStyle(element.Style)) return new(properties.ToArray(), false);
            if (element.TemplatedParent is FrameworkElement parent)
            {
                if (!AddStyle(parent.Style)) return new(properties.ToArray(), false);
                if (parent is Control parentControl && !AddTemplate(parentControl.Template)) return new(properties.ToArray(), false);
                if (parent is ContentPresenter parentPresenter && !AddTemplate(parentPresenter.ContentTemplate)) return new(properties.ToArray(), false);
            }
        }
        else if (target is FrameworkContentElement content && !AddStyle(content.Style)) return new(properties.ToArray(), false);
        if (target is Control control && !AddTemplate(control.Template)) return new(properties.ToArray(), false);
        if (target is ContentPresenter presenter && !AddTemplate(presenter.ContentTemplate)) return new(properties.ToArray(), false);
        return new(properties.ToArray(), complete);
    }

    private static PropertyCandidates DeclaredProperties(Type type)
    {
        var properties = new HashSet<DependencyProperty>();
        bool complete = true;
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(DependencyProperty)) continue;
                try
                {
                    if (field.GetValue(null) is DependencyProperty property) properties.Add(property);
                }
                catch (Exception) { complete = false; }
                if (properties.Count >= MaximumPropertiesPerNode) return new(properties.ToArray(), false);
            }
        }
        return new(properties.ToArray(), complete);
    }
}
