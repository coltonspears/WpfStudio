using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfStudio.PreviewHost;

/// <summary>
/// Builds sample objects of the project's own types for the designer. A view model is allocated without running any
/// constructor, so no service is resolved and no data is fetched, and the fields behind its public properties are then
/// filled with values chosen from each property's name and type. Computed properties work from those fields as they
/// do at run time. Collections come with a few items, to a bounded depth.
/// </summary>
internal sealed class DesignSampleFactory(Assembly? projectAssembly)
{
    private const int MaximumDepth = 4;
    private const int MaximumObjects = 600;
    private static readonly Dictionary<Assembly, bool> FrameworkAssemblies = [];
    private readonly Dictionary<Type, Type?> _implementations = [];
    private int _objects;

    /// <summary>A populated instance of <paramref name="type"/>, or of a class that implements it. Null when the type has no shape to fill.</summary>
    public object? Create(Type type, int index = 0)
    {
        _objects = 0;
        return Model(type, index, 0);
    }

    /// <summary>A list of populated instances, typed to the item so item templates chosen by type still apply.</summary>
    public IList CreateList(Type itemType, int count)
    {
        _objects = 0;
        IList list;
        try { list = (IList)Activator.CreateInstance(typeof(ObservableCollection<>).MakeGenericType(itemType))!; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or TargetInvocationException) { list = new ObservableCollection<object>(); }
        for (int index = 0; index < count; index++)
            if (Value(itemType, itemType.Name, null, index, 0) is { } item && (list is ObservableCollection<object> || itemType.IsInstanceOfType(item))) list.Add(item);
        return list;
    }

    /// <summary>Whether <paramref name="type"/> is a sequence other than text, which a design declaration fills with items instead of properties.</summary>
    public static bool IsSequence(Type type) => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    /// <summary>A populated collection of <paramref name="type"/>, or null when it cannot be created or filled.</summary>
    public object? CreateSequence(Type type)
    {
        _objects = 0;
        return Sequence(type, type.Name, 0, 0);
    }

    private object? Model(Type type, int index, int depth)
    {
        if (depth > MaximumDepth || _objects >= MaximumObjects || Implementation(type) is not { } concrete) return null;
        object instance;
        try { instance = Uninitialized(concrete); }
        // Allocation runs the type's static constructor, which is project code and may need the running application.
        catch (Exception) { return null; }
        // The object never ran a constructor, so a finalizer would see fields no code initialized.
        if (!concrete.IsValueType) GC.SuppressFinalize(instance);
        _objects++;
        Populate(instance, concrete, index, depth);
        return instance;
    }

#if NETFRAMEWORK
    private static object Uninitialized(Type type) => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
#else
    private static object Uninitialized(Type type) => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
#endif

    private void Populate(object instance, Type type, int index, int depth)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var firstItems = new List<object>();
        var selections = new List<(PropertyInfo Property, FieldInfo? Field)>();
        for (Type? current = type; current is not null && !IsFramework(current.Assembly); current = current.BaseType)
        {
            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod is not { IsPublic: true } || !seen.Add(property.Name)) continue;
                var field = BackingField(current, property);
                // A computed property has nothing to seed: it reads the fields filled here.
                if (field is null && property.SetMethod is not { IsPublic: true }) continue;
                if (IsSelection(property)) { selections.Add((property, field)); continue; }
                object? value = Assign(instance, property, field, () => Value(property.PropertyType, property.Name, type, index, depth));
                if (value is IEnumerable sequence and not string && sequence.Cast<object?>().FirstOrDefault() is { } first) firstItems.Add(first);
            }
        }
        // "SelectedOrder" beside "Orders" is one of those orders, so a list shows its selection and the details match it.
        foreach (var (property, field) in selections)
            Assign(instance, property, field, () => firstItems.FirstOrDefault(property.PropertyType.IsInstanceOfType) ?? Value(property.PropertyType, property.Name, type, index, depth));
    }

    private static object? Assign(object instance, PropertyInfo property, FieldInfo? field, Func<object?> sample)
    {
        try
        {
            object? value = sample();
            if (value is null) return null;
            if (field is not null) field.SetValue(instance, value);
            // No field follows a naming convention, so the property stores its value some other way. Its setter is the only way in.
            else property.SetValue(instance, value);
            return value;
        }
        // A setter or a nested type's static constructor is project code and can fail in any way, most often for
        // want of what the constructor would have set up. That member keeps its default and the rest stay usable.
        catch (Exception) { return null; }
    }

    /// <summary>A member that names the chosen one of a list: SelectedOrder, CurrentPage, ActiveDocument.</summary>
    private static bool IsSelection(PropertyInfo property)
    {
        Type type = property.PropertyType;
        return (type.IsClass || type.IsInterface) && type != typeof(string) && !IsSequence(type) && !typeof(Delegate).IsAssignableFrom(type)
            && DesignSampleValues.Words(property.Name) is { Length: > 1 } words && words[0] is "selected" or "current" or "active" or "highlighted";
    }

    /// <summary>The field a property stores its value in: the compiler's for an auto-property, or one that follows a common naming convention.</summary>
    private static FieldInfo? BackingField(Type type, PropertyInfo property)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string name = property.Name;
        if (type.GetField("<" + name + ">k__BackingField", Flags) is { } generated) return generated;
        string camel = char.ToLowerInvariant(name[0]) + name.Substring(1);
        foreach (string candidate in new[] { "_" + camel, camel, "m_" + camel, "_" + name, "m_" + name, name + "@" })
            if (candidate != name && type.GetField(candidate, Flags) is { IsLiteral: false, IsStatic: false } field && field.FieldType == property.PropertyType) return field;
        return null;
    }

    /// <summary>A sample for one member, or null to leave the member at its default.</summary>
    private object? Value(Type type, string member, Type? owner, int index, int depth)
    {
        Type core = Nullable.GetUnderlyingType(type) ?? type;
        if (core == typeof(string)) return DesignSampleValues.Text(member, owner is null ? null : ModelName(owner), index);
        if (core == typeof(bool)) return DesignSampleValues.Flag(member, index);
        if (core == typeof(char)) return (char)('A' + index % 26);
        // Cycling through an element's visibility would hide every second item of a list.
        if (core == typeof(System.Windows.Visibility)) return System.Windows.Visibility.Visible;
        if (core.IsEnum) return EnumValue(core, index);
        if (core.IsPrimitive || core == typeof(decimal)) return Number(core, DesignSampleValues.Number(member, index));
        if (core == typeof(DateTime)) return DesignSampleValues.Date(member, index);
        if (core == typeof(DateTimeOffset)) return new DateTimeOffset(DesignSampleValues.Date(member, index));
        if (core == typeof(TimeSpan)) return TimeSpan.FromMinutes(DesignSampleValues.Number(member, index));
#if !NETFRAMEWORK
        if (core == typeof(DateOnly)) return DateOnly.FromDateTime(DesignSampleValues.Date(member, index));
        if (core == typeof(TimeOnly)) return TimeOnly.FromDateTime(DesignSampleValues.Date(member, index));
#endif
        if (core == typeof(Guid)) return DesignSampleValues.Guid(member, index);
        if (core == typeof(Uri))
            return Uri.TryCreate(DesignSampleValues.Text(DesignSampleValues.Words(member).Any(word => word is "url" or "uri" or "link") ? member : member + "Url", null, index),
                UriKind.Absolute, out var location) ? location : null;
        if (core == typeof(Color)) return DesignSampleValues.Color(index);
        if (core == typeof(Brush) || core == typeof(SolidColorBrush)) return DesignSampleValues.Brush(index);
        if (core == typeof(ImageSource) || core == typeof(DrawingImage)) return DesignSamplePictures.Image(index);
        if (core == typeof(ICommand)) return DesignCommand.Instance;
        // Anything could be meant by an object, a delegate or a UI object; a guess would mislead more than an empty value.
        if (core == typeof(object) || typeof(Delegate).IsAssignableFrom(core) || typeof(DispatcherObject).IsAssignableFrom(core)) return null;
        if (IsSequence(core)) return Sequence(core, member, index, depth);
        return IsFramework(core.Assembly) ? null : Model(core, index, depth + 1);
    }

    /// <summary>"OrderViewModel" names an order, which is what decides whether its Name is a person's.</summary>
    private static string ModelName(Type type)
    {
        string name = type.Name;
        foreach (string suffix in new[] { "ViewModel", "Model", "Dto", "Entity", "Record", "Item", "VM" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)) return name.Substring(0, name.Length - suffix.Length);
        return name;
    }

    private static object EnumValue(Type type, int index)
    {
        var values = Enum.GetValues(type);
        if (values.Length == 0) return Enum.ToObject(type, 0);
        // "None" and "Unknown" say nothing in a preview when the enum has real states to show.
        var named = values.Cast<object>().Where(value => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0
            || !(Enum.GetName(type, value) is "None" or "Unknown" or "Default" or "Undefined" or "Unspecified" or "Invalid")).ToArray();
        return named.Length == 0 ? values.GetValue(0)! : named[index % named.Length];
    }

    private static object? Number(Type type, double value)
    {
        try
        {
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            if (type == typeof(IntPtr) || type == typeof(UIntPtr)) return null;
            double whole = Math.Round(Math.Abs(value));
            double maximum = type == typeof(byte) ? byte.MaxValue : type == typeof(sbyte) ? sbyte.MaxValue : type == typeof(short) ? short.MaxValue : type == typeof(ushort) ? ushort.MaxValue : int.MaxValue;
            return Convert.ChangeType(Math.Min(whole, maximum), type, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException) { return null; }
    }

    private object? Sequence(Type type, string member, int index, int depth)
    {
        _ = index;
        // Fewer items the deeper a list sits, so a tree of lists stays small. A list counts against the budget like
        // any other object, so lists of lists run out of it too.
        int count = _objects >= MaximumObjects ? 0 : depth switch { 0 => 5, 1 => 3, 2 => 2, _ => 0 };
        _objects++;
        if (type.IsArray)
        {
            // Bytes are a file or a picture, never a list to show; made-up ones would only fail to decode.
            if (type.GetArrayRank() != 1 || type.GetElementType() is not { } element || element == typeof(byte)) return null;
            var items = Items(element, member, count, depth);
            var array = Array.CreateInstance(element, items.Count);
            for (int item = 0; item < items.Count; item++) array.SetValue(items[item], item);
            return array;
        }
        if (IsDictionary(type)) return Construct(type);
        if (ItemType(type) is not { } itemType)
        {
            // An untyped list has no shape to follow, so its items answer whatever the item template asks for.
            var untyped = new ObservableCollection<object>();
            for (int item = 0; item < count; item++) untyped.Add(new DesignSampleItem(item));
            if (type.IsAssignableFrom(typeof(ObservableCollection<object>))) return untyped;
            return typeof(ICollectionView).IsAssignableFrom(type) && type.IsAssignableFrom(typeof(ListCollectionView)) ? new ListCollectionView(untyped) : null;
        }
        var values = Items(itemType, member, count, depth);
        Type observable = typeof(ObservableCollection<>).MakeGenericType(itemType), list = typeof(List<>).MakeGenericType(itemType), set = typeof(HashSet<>).MakeGenericType(itemType);
        if (type.IsInterface || type.IsAbstract)
        {
            foreach (Type candidate in new[] { observable, list, set })
                if (type.IsAssignableFrom(candidate)) return Fill(Activator.CreateInstance(candidate)!, itemType, values);
            return type.Namespace == "System.Collections.Immutable" ? Immutable(type, itemType, values, list) : null;
        }
        if (type.Namespace == "System.Collections.Immutable") return Immutable(type, itemType, values, list);
        // A collection is the one kind of project class a sample constructs: left unconstructed it has no storage to add to.
        if (type.GetConstructor(Type.EmptyTypes) is not null && (typeof(IList).IsAssignableFrom(type) || typeof(ICollection<>).MakeGenericType(itemType).IsAssignableFrom(type)))
            return Fill(Activator.CreateInstance(type)!, itemType, values);
        // ReadOnlyCollection<T>, ReadOnlyObservableCollection<T> and the like wrap a list handed to their constructor.
        foreach (Type inner in new[] { observable, list })
            if (type.GetConstructors().Any(constructor => constructor.GetParameters() is [{ ParameterType: var parameter }] && parameter.IsAssignableFrom(inner)))
                return Activator.CreateInstance(type, Fill(Activator.CreateInstance(inner)!, itemType, values));
        return null;
    }

    private List<object> Items(Type itemType, string member, int count, int depth)
    {
        string singular = member.EndsWith("ies", StringComparison.Ordinal) ? member.Remove(member.Length - 3) + "y"
            : member.Length > 1 && member.EndsWith('s') && !member.EndsWith("ss", StringComparison.Ordinal) ? member.Substring(0, member.Length - 1) : member;
        // A list inside a list sits one level deeper, as a model inside a list does. Without this a collection type
        // that holds itself, such as class Nodes : List<Nodes>, would nest without end.
        int itemDepth = IsSequence(Nullable.GetUnderlyingType(itemType) ?? itemType) ? depth + 1 : depth;
        var items = new List<object>();
        for (int item = 0; item < count; item++)
            if (Value(itemType, singular, null, item, itemDepth) is { } value && itemType.IsInstanceOfType(value)) items.Add(value);
        return items;
    }

    private static object Fill(object collection, Type itemType, List<object> values)
    {
        if (collection is IList list) { foreach (object value in values) list.Add(value); return collection; }
        var add = typeof(ICollection<>).MakeGenericType(itemType).GetMethod("Add")!;
        foreach (object value in values) add.Invoke(collection, [value]);
        return collection;
    }

    /// <summary>The immutable collections have no public constructor; each has a static companion with CreateRange.</summary>
    private static object? Immutable(Type type, Type itemType, List<object> values, Type list)
    {
        int arity = type.Name.IndexOf('`');
        string name = arity > 0 ? type.Name.Substring(0, arity) : type.Name;
        if (name.StartsWith('I') && name.Length > 1 && char.IsUpper(name[1])) name = name.Substring(1);
        var create = type.Assembly.GetType("System.Collections.Immutable." + name)?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "CreateRange" && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 1);
        object? created = create?.MakeGenericMethod(itemType).Invoke(null, [Fill(Activator.CreateInstance(list)!, itemType, values)]);
        return created is not null && type.IsInstanceOfType(created) ? created : null;
    }

    private static object? Construct(Type type) =>
        type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(type);

    private static bool IsDictionary(Type type) => typeof(IDictionary).IsAssignableFrom(type) || type.GetInterfaces().Append(type).Any(candidate =>
        candidate.IsGenericType && candidate.GetGenericTypeDefinition() is { } definition && (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)));

    /// <summary>The T of the one IEnumerable&lt;T&gt; a collection implements, or null for an untyped or ambiguous sequence.</summary>
    internal static Type? ItemType(Type type)
    {
        var sequences = type.GetInterfaces().Append(type).Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(candidate => candidate.GetGenericArguments()[0]).Distinct().ToArray();
        return sequences.Length == 1 && !sequences[0].IsGenericParameter && !sequences[0].IsPointer && !sequences[0].IsByRef ? sequences[0] : null;
    }

    /// <summary>The class to allocate for a declared type: itself when it can be, otherwise the project's implementation of the interface or abstract class.</summary>
    private Type? Implementation(Type type)
    {
        if (!IsModel(type)) return null;
        if (!type.IsAbstract && !type.IsInterface) return type;
        if (_implementations.TryGetValue(type, out var known)) return known;
        string expected = type.IsInterface && type.Name.Length > 1 && type.Name[0] == 'I' && char.IsUpper(type.Name[1]) ? type.Name.Substring(1) : type.Name;
        var found = new[] { type.Assembly, projectAssembly }.Where(assembly => assembly is not null && !IsFramework(assembly)).Distinct()
            .SelectMany(assembly => LoadableTypes(assembly!))
            .Where(candidate => candidate.IsClass && !candidate.IsAbstract && !candidate.ContainsGenericParameters && type.IsAssignableFrom(candidate) && IsModel(candidate))
            .OrderBy(candidate => candidate.Name == expected ? 0 : 1).ThenBy(candidate => candidate.FullName, StringComparer.Ordinal).FirstOrDefault();
        return _implementations[type] = found;
    }

    /// <summary>Whether a type is plain application data: a project class or struct with public properties, and none of the kinds that only work once constructed.</summary>
    private static bool IsModel(Type type) =>
        (type.IsClass || type.IsInterface || type.IsValueType && !type.IsPrimitive && !type.IsEnum) && !type.IsArray && !type.IsPointer && !type.IsByRef
        && !type.ContainsGenericParameters && !type.IsCOMObject && type != typeof(string) && !IsFramework(type.Assembly)
        && !typeof(Delegate).IsAssignableFrom(type) && !typeof(ICommand).IsAssignableFrom(type) && !typeof(DispatcherObject).IsAssignableFrom(type)
        && !typeof(Exception).IsAssignableFrom(type) && !typeof(Attribute).IsAssignableFrom(type) && !typeof(IEnumerable).IsAssignableFrom(type);

    internal static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        // Types whose dependencies are missing do not stop the rest from being considered.
        catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type is not null)!; }
    }

    /// <summary>The runtime and its libraries. Their types hold no application data and many break when allocated without a constructor.</summary>
    internal static bool IsFramework(Assembly assembly)
    {
        lock (FrameworkAssemblies)
        {
            if (FrameworkAssemblies.TryGetValue(assembly, out bool known)) return known;
            string name = assembly.GetName().Name ?? "";
            return FrameworkAssemblies[assembly] = name is "mscorlib" or "netstandard" or "System" or "WindowsBase" or "PresentationCore" or "PresentationFramework"
                    or "ReachFramework" or "UIAutomationTypes" or "UIAutomationProvider" or "UIAutomationClient" or "Accessibility" or "WindowsFormsIntegration"
                || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("PresentationFramework.", StringComparison.Ordinal) || name.StartsWith("Windows.", StringComparison.Ordinal);
        }
    }
}

/// <summary>A command for sample data: always available, and doing nothing when run. A button bound to it draws enabled, as it usually does at run time.</summary>
internal sealed class DesignCommand : ICommand
{
    public static readonly DesignCommand Instance = new();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { }
}
