using System.ComponentModel;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Markup;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.Inspection.Agent;

/// <summary>Reads CLR/XAML metadata without TypeDescriptor providers or application attribute constructors.</summary>
internal static class SourcePropertyMetadata
{
    private sealed record MetadataResult(InspectionSourcePropertyIdentity? Identity, string? Error);
    private static readonly ConditionalWeakTable<Type, ConcurrentDictionary<DependencyProperty, MetadataResult>> Cache = new();

    public static InspectionSourcePropertyIdentity? Read(Type target, DependencyProperty property, out string? error)
    {
        var cache = Cache.GetOrCreateValue(target);
        var result = cache.GetOrAdd(property, candidate =>
        {
            try { var identity = ReadCore(target, candidate, out var reason); return new(identity, reason); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return new(null, "The property's CLR/XAML metadata could not be safely resolved (" + RunningInspector.TypeName(exception.GetType()) + ")."); }
        });
        error = result.Error;
        return result.Identity;
    }

    private static InspectionSourcePropertyIdentity? ReadCore(Type target, DependencyProperty property, out string? error)
    {
        error = null;
        if (property.ReadOnly || !ScalarPropertyValues.Supports(property.PropertyType))
        { error = "This dependency property does not expose a writable scalar source value."; return null; }
        if (!SafeConverterAttributes(property.PropertyType.CustomAttributes)
            || Nullable.GetUnderlyingType(property.PropertyType) is { } underlying && !SafeConverterAttributes(underlying.CustomAttributes))
        { error = "An application-defined type converter prevents safe scalar source validation."; return null; }
        for (Type? current = target; current is not null; current = current.BaseType)
            if (!FrameworkType(current) && current.CustomAttributes.Any(attribute => attribute.AttributeType == typeof(TypeDescriptionProviderAttribute)))
            { error = "Application type-description providers cannot be evaluated for source validation."; return null; }
        if (!TryMember(target, property, out var owner, out bool attached))
        { error = "A canonical writable CLR wrapper or attached accessor could not be verified for this exact dependency property."; return null; }
        var candidates = new List<InspectionSourcePropertyTarget>();
        int examined = 0;
        for (Type? current = target; current is not null && ++examined <= 64; current = current.BaseType)
        {
            bool applicable = attached ? AttachedAccessor(owner, property, current)
                : Wrapper(current, property) is not null;
            if (!applicable || !PublicType(current) || !TryTypeIdentity(current, out string type, out string assembly, out Guid module)) continue;
            candidates.Add(new(type, assembly, module, XmlNamespaces(current), ContentProperty(current)));
        }
        if (candidates.Count == 0 || !TryTypeIdentity(target, out string targetType, out string targetAssembly, out Guid targetModule)
            || !TryTypeIdentity(owner, out string ownerType, out string ownerAssembly, out Guid ownerModule))
        { error = "The writable property has no unambiguous public authored target type."; return null; }
        return new(property.Name, ownerType, ownerAssembly, attached, ContentProperty(target), targetType, targetAssembly,
            targetModule, ownerModule, candidates);
    }

    private static bool TryMember(Type target, DependencyProperty property, out Type owner, out bool attached)
    {
        if (Wrapper(target, property) is { DeclaringType: { } declaring }) { owner = declaring; attached = false; return true; }
        owner = property.OwnerType; attached = true;
        return PublicType(owner) && AttachedAccessor(owner, property, target);
    }

    private static PropertyInfo? Wrapper(Type target, DependencyProperty property)
    {
        var wrapper = target.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
        if (wrapper?.DeclaringType is not { } declaring || wrapper.GetMethod?.IsPublic != true || wrapper.SetMethod?.IsPublic != true
            || wrapper.GetIndexParameters().Length != 0 || wrapper.PropertyType != property.PropertyType || !PublicType(declaring)
            || !SafeConverterAttributes(wrapper.CustomAttributes)) return null;
        var field = declaring.GetField(property.Name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (field?.FieldType != typeof(DependencyProperty) || !ReferenceEquals(field.GetValue(null), property)) return null;
        // Framework wrappers are authoritative WPF APIs. For application wrappers,
        // verify their simple accessor IL instead of invoking them or a descriptor.
        return FrameworkType(declaring) || SimpleAccessor(wrapper.GetMethod!, field, "GetValue") && SimpleAccessor(wrapper.SetMethod!, field, "SetValue") ? wrapper : null;
    }

    private static bool AttachedAccessor(Type owner, DependencyProperty property, Type target)
    {
        var field = owner.GetField(property.Name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (field?.FieldType != typeof(DependencyProperty) || !ReferenceEquals(field.GetValue(null), property)) return false;
        var methods = owner.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        var getters = methods.Where(method => method.Name == "Get" + property.Name && method.ReturnType == property.PropertyType
            && method.GetParameters() is [{ ParameterType: var receiver }] && receiver.IsAssignableFrom(target)).ToArray();
        var setters = methods.Where(method => method.Name == "Set" + property.Name && method.ReturnType == typeof(void)
            && method.GetParameters() is [{ ParameterType: var receiver }, { ParameterType: var value }]
            && receiver.IsAssignableFrom(target) && value == property.PropertyType).ToArray();
        return getters.Length == 1 && setters.Length == 1 && SafeConverterAttributes(getters[0].CustomAttributes)
            && SafeConverterAttributes(setters[0].CustomAttributes) && (FrameworkType(owner)
                || SimpleAccessor(getters[0], field, "GetValue") && SimpleAccessor(setters[0], field, "SetValue"));
    }

    private static bool SimpleAccessor(MethodInfo method, FieldInfo field, string operation)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null || bytes.Length > 128 || method.ContainsGenericParameters) return false;
        bool readsField = false, callsValue = false;
        // A closed instruction set covers normal Release/Debug DP wrappers. Any
        // additional call or field access makes the member unsupported, not guessed.
        for (int index = 0; index < bytes.Length;)
        {
            byte code = bytes[index++];
            if (code == 0x7e) // ldsfld
            {
                if (readsField || index + 4 > bytes.Length) return false;
                var resolved = method.Module.ResolveField(BitConverter.ToInt32(bytes, index)); index += 4;
                if (resolved is null || resolved.Module != field.Module || resolved.MetadataToken != field.MetadataToken) return false;
                readsField = true;
            }
            else if (code is 0x28 or 0x6f) // call/callvirt
            {
                if (callsValue || index + 4 > bytes.Length) return false;
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, index)); index += 4;
                if (called is null || called.DeclaringType != typeof(DependencyObject) || called.Name != operation) return false;
                callsValue = true;
            }
            else if (code is 0x74 or 0x79 or 0x8c or 0xa5) // castclass/unbox/box/unbox.any
            { if (index + 4 > bytes.Length) return false; index += 4; }
            else if (code is 0x2b or 0x11 or 0x13) // br.s/ldloc.s/stloc.s
            { if (index >= bytes.Length) return false; index++; }
            else if (code is 0x38) // br
            { if (index + 4 > bytes.Length) return false; index += 4; }
            else if (code is not (0x00 or 0x02 or 0x03 or 0x04 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0a or 0x0b or 0x0c or 0x0d or 0x2a)) return false;
        }
        return readsField && callsValue;
    }

    private static bool SafeConverterAttributes(IEnumerable<CustomAttributeData> attributes)
    {
        foreach (var attribute in attributes.Where(attribute => attribute.AttributeType == typeof(TypeConverterAttribute)))
        {
            if (attribute.ConstructorArguments.Count != 1 || attribute.ConstructorArguments[0].Value is not Type type || !FrameworkType(type)) return false;
            if (type.FullName is not ("System.Windows.LengthConverter" or "System.Windows.FontSizeConverter"
                or "System.Windows.ThicknessConverter" or "System.Windows.CornerRadiusConverter" or "System.Windows.GridLengthConverter"
                or "System.Windows.PointConverter" or "System.Windows.SizeConverter" or "System.Windows.RectConverter"
                or "System.Windows.Media.ColorConverter" or "System.Windows.Media.BrushConverter" or "System.Windows.Media.FontFamilyConverter"
                or "System.Windows.FontWeightConverter" or "System.Windows.FontStyleConverter" or "System.Windows.FontStretchConverter")) return false;
        }
        return true;
    }

    private static bool FrameworkType(Type type) => type.Assembly == typeof(FrameworkElement).Assembly
        || type.Assembly == typeof(DependencyObject).Assembly || type.Assembly == typeof(System.Windows.Media.Brush).Assembly;
    private static bool PublicType(Type type) => type.IsVisible && !type.ContainsGenericParameters && !type.IsGenericType;
    private static bool TryTypeIdentity(Type type, out string name, out string assembly, out Guid module)
    {
        name = type.FullName ?? ""; assembly = type.Assembly.GetName().Name ?? ""; module = type.Module.ModuleVersionId;
        return name.Length is > 0 and <= 1024 && assembly.Length is > 0 and <= 256 && module != Guid.Empty && !type.Assembly.IsDynamic;
    }

    private static string? ContentProperty(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            var attributes = current.CustomAttributes.Where(attribute => attribute.AttributeType == typeof(ContentPropertyAttribute)).ToArray();
            if (attributes.Length > 1) return null;
            if (attributes.Length == 1) return attributes[0].ConstructorArguments is [{ Value: string name }] && name.Length <= 512 ? name : null;
        }
        return null;
    }

    private static IReadOnlyList<string> XmlNamespaces(Type type)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in type.Assembly.CustomAttributes)
        {
            if (attribute.AttributeType != typeof(XmlnsDefinitionAttribute) || attribute.ConstructorArguments is not [{ Value: string uri }, { Value: string clr }]
                || clr != type.Namespace || uri.Length is 0 or > 512) continue;
            var assembly = attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == "AssemblyName").TypedValue.Value as string;
            if (assembly is not null && assembly != type.Assembly.GetName().Name) continue;
            if (result.Count < 16) result.Add(uri);
        }
        return result.ToArray();
    }
}
