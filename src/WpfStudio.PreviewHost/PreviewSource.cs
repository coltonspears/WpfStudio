using System.Windows;

namespace WpfStudio.PreviewHost;

/// <summary>Non-inherited source identity carried through template instantiation.</summary>
public static class PreviewSource
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(PreviewSource), new PropertyMetadata(null));
    public static string? GetId(DependencyObject target) => (string?)target.GetValue(IdProperty);
    public static void SetId(DependencyObject target, string? value) => target.SetValue(IdProperty, value);
}
