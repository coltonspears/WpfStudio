using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace WpfStudio.App.Controls.Editor;

/// <summary>Marks editor adornments (folded pills, lens rows) so hovering them does not ask for Quick Info.</summary>
internal static class EditorAdornment
{
    public static readonly object Tag = new();

    public static bool IsUnder(object? source)
    {
        for (var node = source as DependencyObject; node is not null and not TextView; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is FrameworkElement { Tag: var tag } && ReferenceEquals(tag, Tag)) return true;
        return false;
    }
}
