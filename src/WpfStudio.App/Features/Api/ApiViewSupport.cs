using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using WpfStudio.App.Controls;

namespace WpfStudio.App.Features.Api;

/// <summary>
/// Binds an AvalonEdit editor's text, which is not a dependency property, and picks its highlighting from a language
/// name (json, xml, csharp or text) using the themed definitions the code editors use.
/// </summary>
public static class ApiEditor
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(ApiEditor),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TextChanged));
    public static readonly DependencyProperty LanguageProperty = DependencyProperty.RegisterAttached("Language", typeof(string), typeof(ApiEditor), new PropertyMetadata("text", LanguageChanged));
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(ApiEditor));

    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetLanguage(DependencyObject target) => (string)target.GetValue(LanguageProperty);
    public static void SetLanguage(DependencyObject target, string value) => target.SetValue(LanguageProperty, value);

    private static void TextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextEditor editor) return;
        if (!(bool)editor.GetValue(AttachedProperty))
        {
            editor.SetValue(AttachedProperty, true);
            editor.TextChanged += (_, _) => { if (editor.Text != GetText(editor)) SetText(editor, editor.Text); };
        }
        var text = args.NewValue as string ?? "";
        if (editor.Text != text)
        {
            editor.Text = text;
            if (editor.IsReadOnly) editor.ScrollToHome();
        }
    }

    private static void LanguageChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is TextEditor editor)
            editor.SyntaxHighlighting = SyntaxHighlightingSelector.For(args.NewValue switch { "json" => "x.json", "xml" => "x.xml", "csharp" => "x.cs", _ => null });
    }
}

/// <summary>Binds a password box's password, which WPF keeps out of bindings so it is not held in clear longer than needed; these are API credentials the user chose to save.</summary>
public static class ApiPassword
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached("Value", typeof(string), typeof(ApiPassword),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ValueChanged));
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(ApiPassword));
    public static string GetValue(DependencyObject target) => (string)target.GetValue(ValueProperty);
    public static void SetValue(DependencyObject target, string value) => target.SetValue(ValueProperty, value);

    private static void ValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not System.Windows.Controls.PasswordBox box) return;
        if (!(bool)box.GetValue(AttachedProperty))
        {
            box.SetValue(AttachedProperty, true);
            box.PasswordChanged += (_, _) => { if (box.Password != GetValue(box)) SetValue(box, box.Password); };
        }
        var value = args.NewValue as string ?? "";
        if (box.Password != value) box.Password = value;
    }
}

/// <summary>Checks a radio button when the bound value equals its parameter, and sets the value to the parameter when it is checked.</summary>
public sealed class ChoiceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string text ? targetType.IsEnum ? Enum.Parse(targetType, text) : text : Binding.DoNothing;
}

/// <summary>Collapses an element while a flag is true, or while a value is present: the empty states of lists and panels.</summary>
public sealed class HiddenWhenTrueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A colour per HTTP method, as API tools conventionally show them.</summary>
public sealed class MethodBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string)?.ToUpperInvariant() switch
        {
            "GET" => "SuccessBrush",
            "POST" => "WarningBrush",
            "PUT" => "Chart1Brush",
            "PATCH" => "Chart7Brush",
            "DELETE" => "DangerBrush",
            _ => "MutedBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>The colour of a response's status: green for success, blue for redirects, amber for client errors, red for server errors.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "Success" => "SuccessBrush",
            "Redirect" => "Chart1Brush",
            "ClientError" => "WarningBrush",
            _ => "DangerBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
