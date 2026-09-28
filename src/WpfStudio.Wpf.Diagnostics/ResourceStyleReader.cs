using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

/// <summary>Observed property state, declarations, and available resource scopes; never a simulated lookup trace.</summary>
public static class ResourceStyleReader
{
    public static AppearanceSnapshot Capture(DependencyObject target, DependencyProperty property,
        StaticResourceEvidenceCollector? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        if (!target.CheckAccess()) return AppearanceSnapshot.Unavailable("Appearance must be read on the target's owning dispatcher.");
        return new CaptureState(target, property, evidence).Read();
    }

    private sealed class CaptureState(DependencyObject target, DependencyProperty property, StaticResourceEvidenceCollector? evidence)
    {
        private const int MaximumDeclarations = 256, MaximumScopes = 64, MaximumDictionaryKeys = 1024;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly List<AppearanceFact> _facts = [];
        private readonly List<AppearanceDeclaration> _declarations = [];
        private readonly List<AppearanceResourceScope> _scopes = [];
        private readonly List<string> _notices = [];
        private readonly List<(object Target, object Member)> _associations = [(target, property)];
        private readonly HashSet<ResourceDictionary> _dictionaries = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Style> _styles = new(ReferenceEqualityComparer.Instance);
        private int _keysRemaining = 2048, _textRemaining = 65_536;
        private bool _truncated;

        internal AppearanceSnapshot Read()
        {
            try
            {
                object? value = target.GetValue(property);
                var source = DependencyPropertyHelper.GetValueSource(target, property);
                _facts.Add(new("Effective value", AppearanceText.Value(value), "Current dependency-property value; complex objects are shown by type without invoking application formatting."));
                _facts.Add(new("Base value source", source.BaseValueSource.ToString(), "WPF's precedence category does not identify a particular setter or active trigger."));
                _facts.Add(new("Property", AppearanceText.Property(property)));
                _facts.Add(new("Expression", source.IsExpression.ToString()));
                _facts.Add(new("Animated", source.IsAnimated.ToString()));
                _facts.Add(new("Coerced", source.IsCoerced.ToString()));
                ReadDynamicResource();

                var style = target is FrameworkElement fe ? fe.Style : target is FrameworkContentElement fce ? fce.Style : null;
                ReadStyleChain(style);
                ReadTemplate();
                ReadAncestorScopes();
                _notices.Add("Setters and triggers are declarations, not proof that they currently supply this property. Setter values and resource values have not been evaluated.");
                _notices.Add("Resource scopes are available dictionaries and keys, not WPF's complete lookup order. Theme-style origins and active trigger state are not reconstructed.");
                _notices.Add("Static-resource notifications are historical. Later property, style, or dictionary changes do not change what that earlier lookup reported. Source locations are unverified hints.");
                var history = evidence?.Snapshot(_associations);
                _truncated |= history?.Truncated == true;
                if (history is null || history.Value.Events.Count == 0)
                    Notice("No matching static-resource notification was captured; this does not prove that no resource was used. Diagnostics must be enabled before loading XAML.");
                return new(true, _facts, _declarations, _scopes, history?.Events ?? [], _notices,
                    _truncated, _truncated ? "Some appearance details were omitted because a capture limit was reached." : null);
            }
            catch (Exception exception)
            {
                return new(true, _facts, _declarations, _scopes, [], _notices, true,
                    "Appearance capture was incomplete: " + AppearanceText.Limit(exception.GetBaseException().Message));
            }
        }

        private void ReadDynamicResource()
        {
            object local = target.ReadLocalValue(property);
            if (ReferenceEquals(local, DependencyProperty.UnsetValue) || BindingOperations.IsDataBound(target, property)) return;
            try
            {
                // Instantiate the exact public WPF converter. TypeDescriptor could
                // dispatch to application-provided converters or providers.
                var converted = new ResourceReferenceExpressionConverter().ConvertTo(null, CultureInfo.InvariantCulture, local, typeof(MarkupExtension));
                if (converted is DynamicResourceExtension dynamic)
                    _facts.Add(new("Local DynamicResource key", AppearanceText.Value(dynamic.ResourceKey),
                        "Read from the current local WPF expression. Its resolving dictionary is not exposed by this API."));
            }
            catch (ArgumentException) { } // A literal or another expression is not a resource reference.
            catch (NotSupportedException) { }
        }

        private void ReadStyleChain(Style? style)
        {
            int index = 0;
            while (style is not null && Budget() && index < 32 && _styles.Add(style))
            {
                if (!style.CheckAccess()) { Notice("A style belongs to another dispatcher and was omitted."); break; }
                string label = index == 0 ? "Applied Style" : $"BasedOn {index}";
                Declaration(index == 0 ? "Style" : "BasedOn", label + " · " + AppearanceText.TypeName(style.TargetType), style);
                Association(style, typeof(Style).GetProperty(nameof(Style.BasedOn))!);
                ReadSetters(style.Setters, label, null, false);
                ReadTriggers(style.Triggers, label, null, false);
                ReadResources(style, label);
                style = style.BasedOn;
                index++;
            }
            if (style is not null) _truncated = true;
        }

        private void ReadTemplate()
        {
            if (target is Control control) ReadTemplateContext(control, null, false);
            // A named Control inside another control's template has two relevant
            // contexts: its own template and the outer template targeting it.
            if (target is FrameworkElement { TemplatedParent: Control parent } framework && !ReferenceEquals(parent, target))
                ReadTemplateContext(parent, framework.Name, true);
        }

        private void ReadTemplateContext(Control owner, string? name, bool child)
        {
            if (!owner.CheckAccess() || !Budget()) return;
            if (owner.Template is not { } template || !template.CheckAccess()) return;
            Declaration("ControlTemplate", child ? "Templated parent's ControlTemplate" : "Current ControlTemplate", template);
            if (!child || !string.IsNullOrEmpty(name)) ReadTriggers(template.Triggers, "ControlTemplate", name, child);
            ReadResources(template, child ? "Templated parent's ControlTemplate" : "ControlTemplate");
        }

        private void ReadSetters(SetterBaseCollection setters, string context, string? targetName, bool child)
        {
            int count = Math.Min(setters.Count, MaximumDeclarations);
            if (count != setters.Count) _truncated = true;
            for (int i = 0; i < count && Budget(); i++)
            {
                if (setters[i] is not Setter setter || setter.Property != property ||
                    (child ? setter.TargetName != targetName : !string.IsNullOrEmpty(setter.TargetName))) continue;
                Declaration("Setter", $"{context} · Setter {i + 1} · value not evaluated", setter, setter.TargetName);
                Association(setter, typeof(Setter).GetProperty(nameof(Setter.Value))!);
            }
        }

        private void ReadTriggers(TriggerCollection triggers, string context, string? targetName, bool child)
        {
            int count = Math.Min(triggers.Count, MaximumDeclarations);
            if (count != triggers.Count) _truncated = true;
            for (int i = 0; i < count && Budget(); i++)
            {
                TriggerBase trigger = triggers[i];
                var setters = trigger switch
                {
                    Trigger t => t.Setters,
                    MultiTrigger t => t.Setters,
                    DataTrigger t => t.Setters,
                    MultiDataTrigger t => t.Setters,
                    _ => null
                };
                if (setters is null) continue;
                bool relevant = false;
                int setterCount = Math.Min(setters.Count, MaximumDeclarations);
                if (setterCount != setters.Count) _truncated = true;
                for (int s = 0; s < setterCount && Budget(); s++)
                    if (setters[s] is Setter setter && setter.Property == property &&
                        (child ? setter.TargetName == targetName : string.IsNullOrEmpty(setter.TargetName))) { relevant = true; break; }
                if (!relevant) continue;
                string kind = trigger switch { Trigger => "Trigger", MultiTrigger => "MultiTrigger", DataTrigger => "DataTrigger", MultiDataTrigger => "MultiDataTrigger", _ => "Trigger" };
                string condition = trigger switch
                {
                    Trigger t => AppearanceText.Property(t.Property) + " equals " + AppearanceText.Value(t.Value) + (t.SourceName is null ? "" : " on " + AppearanceText.Limit(t.SourceName)),
                    DataTrigger t => BindingText(t.Binding) + " equals " + AppearanceText.Value(t.Value),
                    MultiTrigger t => ConditionsText(t.Conditions),
                    MultiDataTrigger t => ConditionsText(t.Conditions),
                    _ => "Condition not evaluated"
                };
                Declaration(kind, $"{context} · {kind} {i + 1} · {condition} · active state not evaluated", trigger);
                ReadSetters(setters, $"{context} / {kind} {i + 1}", targetName, child);
            }
        }

        private string ConditionsText(ConditionCollection conditions)
        {
            var values = new List<string>();
            int count = Math.Min(conditions.Count, 16);
            if (count != conditions.Count) _truncated = true;
            for (int i = 0; i < count && Budget(); i++)
            {
                var condition = conditions[i];
                values.Add((condition.Property is { } dp ? AppearanceText.Property(dp) : BindingText(condition.Binding)) +
                    " equals " + AppearanceText.Value(condition.Value));
            }
            return AppearanceText.Limit(string.Join(" and ", values), 1024);
        }

        private static string BindingText(BindingBase? binding) => binding is Binding b ?
            "Binding " + AppearanceText.Limit(b.Path?.Path ?? "(self)", 256) : binding is null ? "(no binding)" : AppearanceText.TypeName(binding.GetType());

        private void ReadAncestorScopes()
        {
            var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
            DependencyObject? current = target;
            int depth = 0;
            while (current is not null && depth < 64 && visited.Add(current) && Budget())
            {
                if (!current.CheckAccess()) { Notice("An ancestor belongs to another dispatcher and was omitted."); break; }
                ReadResources(current, depth == 0 ? "Selected element" : $"Ancestor {depth} · {AppearanceText.TypeName(current.GetType())}");
                current = current is FrameworkElement element ? element.Parent ?? element.TemplatedParent ?? VisualParent(element) :
                    current is FrameworkContentElement content ? content.Parent : VisualParent(current);
                depth++;
            }
            if (current is not null) _truncated = true;
            if (Application.Current is { } application)
            {
                if (application.CheckAccess()) ReadResources(application, "Application");
                else Notice("Application resources belong to another dispatcher and were omitted.");
            }
        }

        private static DependencyObject? VisualParent(DependencyObject value) => value is Visual visual ? VisualTreeHelper.GetParent(visual) : null;

        private void ReadResources(object owner, string label)
        {
            if (!Budget() || owner is not IQueryAmbient ambient) return;
            // The interface can be reimplemented by an application. Invoke only
            // the framework implementation, and avoid creating absent Resources.
            var map = owner.GetType().GetInterfaceMap(typeof(IQueryAmbient));
            if (map.TargetMethods.Any(method => method.DeclaringType?.Assembly != typeof(FrameworkElement).Assembly))
            { Notice("A custom ambient resource accessor was omitted."); return; }
            if (!ambient.IsAmbientPropertyAvailable("Resources")) return;
            ResourceDictionary? dictionary = owner switch
            {
                FrameworkElement element => element.Resources,
                FrameworkContentElement content => content.Resources,
                Style style => style.Resources,
                FrameworkTemplate template => template.Resources,
                Application application => application.Resources,
                _ => null
            };
            if (dictionary is not null) ReadDictionary(dictionary, null, label, 0);
        }

        private void ReadDictionary(ResourceDictionary dictionary, string? parentId, string label, int depth)
        {
            if (!Budget() || _scopes.Count >= MaximumScopes || depth >= 16) { _truncated = true; return; }
            if (!_dictionaries.Add(dictionary)) return;
            string id = "scope:" + _scopes.Count;
            var keys = new List<string>();
            bool omitted = false;
            // Keys allocates a full copy, so reject oversized dictionaries BEFORE
            // reading it. Reading entries/Values would instantiate deferred BAML.
            int count = dictionary.Count;
            if (count > MaximumDictionaryKeys || count > _keysRemaining) omitted = true;
            else
            {
                foreach (object key in dictionary.Keys)
                {
                    if (!Budget() || _keysRemaining == 0) { omitted = true; break; }
                    string text = AppearanceText.Value(key);
                    if (_textRemaining < text.Length) { omitted = true; break; }
                    _textRemaining -= text.Length;
                    _keysRemaining--;
                    keys.Add(text);
                }
            }
            _truncated |= omitted;
            _scopes.Add(new(id, parentId, AppearanceText.Limit(label), AppearanceText.UriText(dictionary.Source), keys, omitted));
            var merged = dictionary.MergedDictionaries;
            int mergedCount = Math.Min(merged.Count, MaximumScopes);
            if (mergedCount != merged.Count) _truncated = true;
            for (int i = 0; i < mergedCount && Budget(); i++)
                if (merged[i] is { } child) ReadDictionary(child, id, $"Merged dictionary {i + 1}", depth + 1);
        }

        private void Declaration(string kind, string description, object declaration, string? targetName = null)
        {
            if (_declarations.Count >= MaximumDeclarations || !Budget()) { _truncated = true; return; }
            _declarations.Add(new("declaration:" + _declarations.Count, kind, AppearanceText.Limit(description, 1536),
                kind is "Style" or "BasedOn" or "ControlTemplate" ? null : AppearanceText.Property(property),
                targetName is null ? null : AppearanceText.Limit(targetName), AppearanceText.Source(declaration)));
        }

        private void Association(object owner, object member)
        {
            if (_associations.Count < 512) _associations.Add((owner, member));
            else _truncated = true;
        }

        private bool Budget()
        {
            if (_watch.ElapsedMilliseconds < 150) return true;
            _truncated = true;
            return false;
        }

        private void Notice(string text) { if (_notices.Count < 16 && !_notices.Contains(text)) _notices.Add(text); }
    }
}
