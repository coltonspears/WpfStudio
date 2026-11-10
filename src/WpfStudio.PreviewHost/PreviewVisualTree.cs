using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfStudio.Contracts;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.PreviewHost;

/// <summary>Ids for the render's visual and logical objects, and the reflection that finds their dependency properties.</summary>
internal sealed class PreviewVisualTree(PreviewScene scene, PreviewDiagnosticLog log)
{
    private readonly Dictionary<string, DependencyObject> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<DependencyObject, string> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, (DependencyProperty[] Properties, HashSet<DependencyProperty> Known)> _propertyCache = [];

    public IEnumerable<KeyValuePair<string, DependencyObject>> Objects => _objects;
    public bool TryGet(string nodeId, [NotNullWhen(true)] out DependencyObject? target) => _objects.TryGetValue(nodeId, out target);
    public bool Contains(DependencyObject target) => _ids.ContainsKey(target);
    public string? IdOf(DependencyObject target) => _ids.GetValueOrDefault(target);
    public void Clear() { _objects.Clear(); _ids.Clear(); }

    public IReadOnlyList<PreviewNode> CollectNodes()
    {
        if (scene.Root is null) return [];
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var ordered = new List<DependencyObject>();
        var parents = new Dictionary<DependencyObject, DependencyObject?>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(DependencyObject Node, DependencyObject? Parent)>();
        stack.Push((scene.Root, null));
        while (stack.Count > 0 && visited.Count < 10_000)
        {
            var current = stack.Pop();
            if (!visited.Add(current.Node)) continue;
            ordered.Add(current.Node);
            parents[current.Node] = current.Parent;
            GetId(current.Node);
            var children = new List<DependencyObject>();
            if (current.Node is Visual or Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current.Node); i++) children.Add(VisualTreeHelper.GetChild(current.Node, i));
            foreach (var child in LogicalTreeHelper.GetChildren(current.Node).OfType<DependencyObject>())
                if (!children.Contains(child)) children.Add(child);
            for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], current.Node));
        }
        if (stack.Count > 0)
            log.Add(new PreviewDiagnostic("The inspector tree is limited to 10,000 elements. The preview image still renders the complete view.", "Warning"));
        // Remove template instances and items no longer present after live edits.
        foreach (var stale in _ids.Keys.Where(o => !visited.Contains(o)).ToArray())
        {
            _objects.Remove(_ids[stale]);
            _ids.Remove(stale);
        }
        return ordered.Select(o => Node(o, parents[o])).ToArray();
    }

    public string GetId(DependencyObject target)
    {
        if (_ids.TryGetValue(target, out var existing)) return existing;
        string id = Guid.NewGuid().ToString("N");
        _ids[target] = id;
        _objects[id] = target;
        return id;
    }

    public PreviewNode Node(DependencyObject target, DependencyObject? parent = null)
    {
        string? name = target is FrameworkElement element ? element.Name : target is FrameworkContentElement content ? content.Name : null;
        parent ??= VisualParent(target) ?? LogicalTreeHelper.GetParent(target);
        DependencyObject? logicalParent = LogicalTreeHelper.GetParent(target);
        PreviewBounds? bounds = null;
        if (target is Visual visual && scene.Viewport is not null)
        {
            try
            {
                Rect rectangle = target is UIElement ui ? new Rect(ui.RenderSize) : VisualTreeHelper.GetDescendantBounds(visual);
                rectangle = visual.TransformToAncestor(scene.Viewport).TransformBounds(rectangle);
                if (!rectangle.IsEmpty && double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height))
                    bounds = new PreviewBounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            }
            catch (InvalidOperationException) { }
        }
        return new PreviewNode(GetId(target), parent is not null ? _ids.GetValueOrDefault(parent) : null,
            logicalParent is not null ? _ids.GetValueOrDefault(logicalParent) : null, target.GetType().FullName ?? target.GetType().Name,
            string.IsNullOrEmpty(name) ? null : name, bounds, scene.Document?.FindSource(target, ReferenceEquals(target, scene.Root)), target is Visual or Visual3D);
    }

    public IReadOnlyList<DependencyProperty> Properties(DependencyObject target)
    {
        Type type = target.GetType();
        if (!_propertyCache.TryGetValue(type, out var cached))
        {
            var properties = new HashSet<DependencyProperty>();
            for (Type? current = type; current is not null; current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (field.FieldType == typeof(DependencyProperty) && field.GetValue(null) is DependencyProperty property) properties.Add(property);
            foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(target))
                if (DependencyPropertyDescriptor.FromProperty(descriptor)?.DependencyProperty is { } property) properties.Add(property);
            properties.Remove(PreviewSource.IdProperty);
            cached = (properties.ToArray(), properties);
            _propertyCache[type] = cached;
        }
        // The type's own list serves as it is unless the object holds a value for a property outside it, an attached one such as
        // Grid.Row. Every snapshot asks this of every node, so copying the whole set for each was most of what a snapshot allocated.
        List<DependencyProperty>? attached = null;
        var local = target.GetLocalValueEnumerator();
        while (local.MoveNext())
        {
            var property = local.Current.Property;
            if (property != PreviewSource.IdProperty && !cached.Known.Contains(property) && attached?.Contains(property) != true) (attached ??= []).Add(property);
        }
        return attached is null ? cached.Properties : [.. cached.Properties, .. attached];
    }

    public static string PropertyName(DependencyProperty property, DependencyObject target) =>
        !IsAttached(property, target) ? property.Name : property.OwnerType.Name + "." + property.Name;

    public DependencyProperty FindProperty(DependencyObject target, PreviewPropertyEdit request)
    {
        var candidates = Properties(target).Where(p => PropertyName(p, target) == request.Property);
        if (request.OwnerType is not null || request.OwnerAssembly is not null)
        {
            if (string.IsNullOrWhiteSpace(request.OwnerType) || string.IsNullOrWhiteSpace(request.OwnerAssembly))
                throw new InvalidOperationException("Both property owner type and assembly are required for an exact property identity.");
            candidates = candidates.Where(p => p.OwnerType.FullName == request.OwnerType && p.OwnerType.Assembly.GetName().Name == request.OwnerAssembly);
        }
        var matches = candidates.Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            > 1 => throw new InvalidOperationException("The property name is ambiguous. Select a property using its full owner type and assembly."),
            _ => throw new InvalidOperationException("The dependency property was not found.")
        };
    }

    public static bool IsAttached(DependencyProperty property, DependencyObject target)
    {
        var wrapper = PropertyWrappers.Find(target.GetType(), property.Name);
        if (wrapper?.SetMethod?.IsPublic != true || wrapper.PropertyType != property.PropertyType) return true;
        var descriptor = TypeDescriptor.GetProperties(target)[property.Name];
        return descriptor is null || DependencyPropertyDescriptor.FromProperty(descriptor)?.DependencyProperty != property;
    }

    public static DependencyObject? VisualParent(DependencyObject target) => target is Visual or Visual3D ? VisualTreeHelper.GetParent(target) : null;

    public bool IsInCurrentPreview(DependencyObject target)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        for (DependencyObject? current = target; current is not null && visited.Count < 512 && visited.Add(current);)
        {
            if (ReferenceEquals(current, scene.Viewport)) return true;
            current = VisualParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
