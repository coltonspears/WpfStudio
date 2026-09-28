using System.Globalization;
using System.Windows;
using System.Windows.Diagnostics;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

internal static class AppearanceText
{
    internal static string Limit(string? value, int maximum = 512) =>
        value is null ? "" : value.Length <= maximum ? value : value[..maximum] + "…";

    // Never use an application's ToString, IFormattable, type converter, or key equality.
    internal static string Value(object? value) => value switch
    {
        null => "(null)",
        string text => "\"" + Limit(text, 256) + "\"",
        bool boolean => boolean ? "True" : "False",
        char character => "'" + character + "'",
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        Type type when type.GetType().Assembly == typeof(Type).Assembly => Limit(type.FullName ?? type.Name),
        _ => "(" + TypeName(value.GetType()) + ")"
    };

    internal static string TypeName(Type type) => type.GetType().Assembly == typeof(Type).Assembly
        ? Limit(type.FullName ?? type.Name)
        : "(" + Limit(type.GetType().FullName ?? type.GetType().Name) + ")";
    internal static string Property(DependencyProperty property) => TypeName(property.OwnerType) + "." + Limit(property.Name);
    internal static string? UriText(Uri? uri) => uri is null ? null : Limit(uri.OriginalString, 2048);

    internal static InspectionSourceHint? Source(object target)
    {
        try
        {
            var source = VisualDiagnostics.GetXamlSourceInfo(target);
            if (source?.SourceUri?.OriginalString is { Length: > 0 and <= 2048 } uri && source.LineNumber > 0 && source.LinePosition > 0)
                return new(uri, source.LineNumber, source.LinePosition);
        }
        catch (Exception) { }
        return null;
    }
}
