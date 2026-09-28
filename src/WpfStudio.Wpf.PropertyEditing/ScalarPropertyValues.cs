using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WpfStudio.Wpf.PropertyEditing;

/// <summary>Text conversion for a closed set of scalar types, without application type-description providers.</summary>
public static class ScalarPropertyValues
{
    public const int MaximumTextLength = 65536;

    public static bool Supports(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(string) || type == typeof(object) || type == typeof(bool) || type == typeof(char) ||
            type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
            type == typeof(float) || type == typeof(double) || type == typeof(decimal) || Converter(type) is not null || type == typeof(Brush);
    }

    public static bool TryConvert(Type propertyType, string? text, bool isNull, out object? value, out string? error)
    {
        value = null; error = null;
        try
        {
            if (!Supports(propertyType)) throw new InvalidOperationException("This property type does not support scalar editing.");
            if (isNull)
            {
                if (propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) is null)
                    throw new InvalidOperationException("This property does not accept null.");
                return true;
            }
            text ??= "";
            if (text.Length > MaximumTextLength) throw new InvalidOperationException($"Scalar text exceeds {MaximumTextLength} characters.");
            Type type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (type == typeof(string) || type == typeof(object)) value = text;
            else if (type == typeof(bool)) value = bool.Parse(text);
            else if (type == typeof(char)) value = text.Length == 1 ? text[0] : throw new FormatException("Enter exactly one character.");
            else if (type.IsEnum) value = Enum.Parse(type, text, ignoreCase: true);
            else if (type == typeof(byte)) value = byte.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(sbyte)) value = sbyte.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(short)) value = short.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(ushort)) value = ushort.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(int)) value = int.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(uint)) value = uint.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(long)) value = long.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(ulong)) value = ulong.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(float)) value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            else if (type == typeof(double)) value = text.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? double.NaN : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            else if (type == typeof(decimal)) value = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            else if (type == typeof(Brush))
            {
                var brush = new SolidColorBrush((Color)new ColorConverter().ConvertFrom(null, CultureInfo.InvariantCulture, text)!);
                brush.Freeze();
                value = brush;
            }
            else
            {
                if (type == typeof(FontFamily) && text.IndexOfAny([':', '/', '\\', '#']) >= 0)
                    throw new FormatException("Use a font family name without a resource URI.");
                value = Converter(type)!.ConvertFrom(null, CultureInfo.InvariantCulture, text);
            }
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>Returns complete editable text, or null when no faithful scalar representation exists.</summary>
    public static string? ToEditableText(Type propertyType, object? value)
    {
        if (value is null || !Supports(propertyType)) return null;
        if (value is string text) return text.Length <= MaximumTextLength ? text : null;
        Type type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(object)) return null;
        if (type == typeof(Brush))
            return value is SolidColorBrush brush && brush.Opacity == 1 && brush.Transform.Value.IsIdentity && brush.RelativeTransform.Value.IsIdentity
                ? brush.Color.ToString(CultureInfo.InvariantCulture) : null;
        // Custom subclasses and application-supplied converters are never used.
        if (value.GetType() != type) return null;
        try
        {
            if (type.IsEnum)
            {
                string names = Enum.Format(type, value, "G");
                return names.Length <= MaximumTextLength ? names : null;
            }
            if (value is bool boolean) return boolean ? "True" : "False";
            if (value is char character) return character.ToString();
            if (value is double number) return number.ToString("R", CultureInfo.InvariantCulture);
            if (value is float single) return single.ToString("R", CultureInfo.InvariantCulture);
            if (value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal)
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            string? literal = Converter(type)?.ConvertToInvariantString(value);
            return literal is not null && literal.Length <= MaximumTextLength && TryConvert(type, literal, false, out var roundTrip, out _) && Equals(value, roundTrip) ? literal : null;
        }
        catch (Exception) { return null; }
    }

    private static TypeConverter? Converter(Type type) =>
        type == typeof(Thickness) ? new ThicknessConverter() :
        type == typeof(CornerRadius) ? new CornerRadiusConverter() :
        type == typeof(GridLength) ? new GridLengthConverter() :
        type == typeof(Point) ? new PointConverter() :
        type == typeof(Size) ? new SizeConverter() :
        type == typeof(Rect) ? new RectConverter() :
        type == typeof(Color) ? new ColorConverter() :
        type == typeof(FontFamily) ? new FontFamilyConverter() :
        type == typeof(FontWeight) ? new FontWeightConverter() :
        type == typeof(FontStyle) ? new FontStyleConverter() :
        type == typeof(FontStretch) ? new FontStretchConverter() : null;
}
