using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.App.Controls;

/// <summary>Attached presentation hints consumed by the studio control templates.</summary>
public static class Ui
{
    /// <summary>Glyph kind shown by menu items and toolbar buttons (see <see cref="Glyph"/>).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static string? GetIcon(DependencyObject target) => (string?)target.GetValue(IconProperty);
    public static void SetIcon(DependencyObject target, string? value) => target.SetValue(IconProperty, value);

    /// <summary>Placeholder text shown by text inputs while they are empty.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static string? GetPlaceholder(DependencyObject target) => (string?)target.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject target, string? value) => target.SetValue(PlaceholderProperty, value);

    /// <summary>Hover state for caption buttons whose hit-testing is owned by the non-client area.</summary>
    public static readonly DependencyProperty IsChromeHoverProperty = DependencyProperty.RegisterAttached("IsChromeHover", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
    public static bool GetIsChromeHover(DependencyObject target) => (bool)target.GetValue(IsChromeHoverProperty);
    public static void SetIsChromeHover(DependencyObject target, bool value) => target.SetValue(IsChromeHoverProperty, value);

    public static readonly DependencyProperty IsChromePressedProperty = DependencyProperty.RegisterAttached("IsChromePressed", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
    public static bool GetIsChromePressed(DependencyObject target) => (bool)target.GetValue(IsChromePressedProperty);
    public static void SetIsChromePressed(DependencyObject target, bool value) => target.SetValue(IsChromePressedProperty, value);
}

/// <summary>Left indentation for a tree row so selection can span the full width of the tree.</summary>
public sealed class TreeIndentConverter : IValueConverter
{
    public double Indent { get; set; } = 14;
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = 0;
        if (value is TreeViewItem item)
            for (var parent = ItemsControl.ItemsControlFromItemContainer(item); parent is TreeViewItem ancestor; parent = ItemsControl.ItemsControlFromItemContainer(ancestor)) depth++;
        return new Thickness(4 + depth * Indent, 0, 4, 0);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
