using System.Numerics;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.Wpf.Diagnostics;

/// <summary>
/// Reads only an expression's own cached validation entries. WPF's composite
/// ValidationErrors/HasError getters can traverse every child, and ValidationErrors
/// can construct a collection from null when only a child has an error.
/// </summary>
internal static class BindingValidationStateReader
{
    internal sealed record Result(IReadOnlyList<ValidationError> Errors, bool HasError, bool Truncated, bool Available);
    private static readonly Lazy<Shape?> Metadata = new(Shape.Create);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

    internal static Result Capture(BindingExpressionBase expression)
    {
        try
        {
            if (Metadata.Value is not { } shape || shape.Values.GetValue(expression) is not { } values)
                return new([], false, true, false);
            uint mask = (uint)shape.Mask.GetValue(values)!;
            var table = shape.Table.GetValue(values) as object[];
            if ((table?.Length ?? 0) != BitOperations.PopCount(mask)) return new([], false, true, false);
            object? Entry(int feature)
            {
                uint bit = 1u << feature;
                return (mask & bit) == 0 ? null : table![BitOperations.PopCount(mask & (bit - 1))];
            }
            object? single = Entry(shape.Error), notifications = Entry(shape.Notifications);
            if (single is not null and not ValidationError || notifications is not null and not List<ValidationError>)
                return new([], false, true, false);
            var errors = new List<ValidationError>(4);
            if (single is ValidationError error) errors.Add(error);
            var list = notifications as List<ValidationError>;
            int count = list?.Count ?? 0;
            for (int i = 0; i < count && errors.Count < 4; i++) errors.Add(list![i]);
            return new(errors.ToArray(), (mask & ((1u << shape.Error) | (1u << shape.Notifications))) != 0,
                count + (single is null ? 0 : 1) > errors.Count, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return new([], false, true, false); }
    }

    private sealed record Shape(FieldInfo Values, FieldInfo Mask, FieldInfo Table, int Error, int Notifications)
    {
        internal static Shape? Create()
        {
            try
            {
                var assembly = typeof(BindingExpressionBase).Assembly;
                if (assembly.GetName().Version?.Major is not (>= 8 and <= 10)) return null;
                var values = typeof(BindingExpressionBase).GetField("_values", Fields);
                var type = values?.FieldType;
                if (type?.Assembly != assembly || type.FullName != "MS.Internal.UncommonValueTable" || !type.IsValueType) return null;
                var mask = type.GetField("_bitmask", Fields);
                var table = type.GetField("_table", Fields);
                var features = typeof(BindingExpressionBase).GetNestedType("Feature", BindingFlags.NonPublic);
                if (mask?.FieldType != typeof(uint) || table?.FieldType != typeof(object[]) || features?.IsEnum != true) return null;
                int error = Convert.ToInt32(features.GetField("ValidationError", Fields)!.GetRawConstantValue());
                int notifications = Convert.ToInt32(features.GetField("NotifyDataErrors", Fields)!.GetRawConstantValue());
                if (error is < 0 or >= 32 || notifications is < 0 or >= 32 || error == notifications) return null;
                return new(values!, mask, table, error, notifications);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
        }
    }
}
