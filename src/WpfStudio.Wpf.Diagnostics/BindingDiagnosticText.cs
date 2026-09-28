namespace WpfStudio.Wpf.Diagnostics;

internal static class BindingDiagnosticText
{
    private static readonly Type RuntimeType = typeof(object).GetType();

    internal static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";

    // Cached PropertyPath metadata can include an application-supplied Type or
    // PropertyInfo. Do not dispatch virtual metadata getters on those objects.
    internal static string TypeName(Type type) => type.GetType() == RuntimeType
        ? Limit(type.FullName ?? type.Name, 512)
        : Limit(type.GetType().FullName ?? "Custom Type metadata", 480) + " (custom type metadata omitted)";

    internal static bool IsRuntimeType(Type type) => type.GetType() == RuntimeType;
}
