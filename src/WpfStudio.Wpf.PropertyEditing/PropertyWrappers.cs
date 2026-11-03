using System.Reflection;

namespace WpfStudio.Wpf.PropertyEditing;

public static class PropertyWrappers
{
    /// <summary>The wrapper XAML and C# resolve for this name. <c>Type.GetProperty</c> throws instead when a <c>new</c> wrapper changes the property's type.</summary>
    public static PropertyInfo? Find(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, null, Type.EmptyTypes, null) is { } wrapper) return wrapper;
        return null;
    }
}
