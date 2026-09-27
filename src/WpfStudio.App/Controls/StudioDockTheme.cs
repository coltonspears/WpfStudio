using System.Reflection;
using System.Windows;
using System.Windows.Media;
using AvalonDock.Themes;
using AvalonDock.Themes.VS2013.Themes;
using AvalonDock.Themes.VS2013.Themes.Menu;

namespace WpfStudio.App.Controls;

/// <summary>
/// Graphite docking theme. It reuses the VS2013 theme's behaviour-heavy templates, replaces the
/// document tabs, tool captions and tool tabs with the studio's own templates, and recolours
/// every remaining VS2013 brush key so no stock blue survives in floating or auto-hide windows.
/// </summary>
public sealed class StudioDockTheme(bool light) : DictionaryTheme(Build(light))
{
    public bool IsLight { get; } = light;

    private static ResourceDictionary Build(bool light)
    {
        DictionaryTheme baseline = light ? new Vs2013LightTheme() : new Vs2013DarkTheme();
        var dictionary = new ResourceDictionary();
        dictionary.MergedDictionaries.Add(baseline.ThemeResourceDictionary);
        dictionary.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WpfStudio;component/Resources/DockStyles.xaml", UriKind.Relative) });
        var colors = light ? Light : Dark;
        foreach (var field in typeof(ResourceKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not { } key) continue;
            var color = Resolve(field.Name, colors);
            if (color is null) continue;
            if (field.Name.EndsWith("ColorKey", StringComparison.Ordinal)) dictionary[key] = color.Value;
            else dictionary[key] = Freeze(color.Value);
        }
        foreach (var field in typeof(MenuKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not { } key || field.Name.Contains("Shadow", StringComparison.Ordinal)) continue;
            if (MenuColor(field.Name, colors) is { } color) dictionary[key] = Freeze(color);
        }
        return dictionary;
    }

    private static SolidColorBrush Freeze(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private sealed record Tones(Color Window, Color Surface, Color Editor, Color Hover, Color Pressed, Color Border, Color StrongBorder, Color Text, Color Muted, Color Accent, Color AccentFill, Color OnAccent);
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static readonly Tones Dark = new(C("#131417"), C("#18191D"), C("#1C1D22"), C("#2A2C33"), C("#32353D"), C("#26282E"), C("#34373F"), C("#E4E5E9"), C("#9397A1"), C("#8C94FF"), C("#5B63E6"), C("#FFFFFF"));
    private static readonly Tones Light = new(C("#EDEEF1"), C("#F6F7F9"), C("#FFFFFF"), C("#E6E7EB"), C("#DADCE2"), C("#E0E1E6"), C("#CBCED6"), C("#1C1D21"), C("#5B5F69"), C("#4A52D4"), C("#4A52D4"), C("#FFFFFF"));

    private static Color? Resolve(string name, Tones t)
    {
        if (name is "DockingButtonWidthKey" or "DockingButtonHeightKey") return null;
        if (name.Contains("Grip", StringComparison.Ordinal)) return Colors.Transparent;
        if (name is "ControlAccentColorKey" or "ControlAccentBrushKey" or "DockingButtonForegroundBrushKey" or "PreviewBoxBorderBrushKey") return t.AccentFill;
        if (name == "PreviewBoxBackgroundBrushKey") return Color.FromArgb(0x40, t.AccentFill.R, t.AccentFill.G, t.AccentFill.B);
        if (name == "DockingButtonForegroundArrowBrushKey") return t.OnAccent;
        if (name is "DockingButtonBackgroundBrushKey" or "DockingButtonStarBackgroundBrushKey") return Color.FromArgb(0x30, 0, 0, 0);
        if (name == "DockingButtonStarBorderBrushKey") return t.StrongBorder;
        if (name is "Background") return t.Window;
        if (name is "PanelBorderBrush") return t.Border;
        if (name is "TabBackground") return t.Surface;
        if (name.StartsWith("NavigatorWindow", StringComparison.Ordinal))
            return name.EndsWith("SelectedBackground", StringComparison.Ordinal) ? t.AccentFill : name.EndsWith("SelectedText", StringComparison.Ordinal) ? t.OnAccent : name.EndsWith("Foreground", StringComparison.Ordinal) ? t.Muted : t.Surface;
        if (name.StartsWith("Floating", StringComparison.Ordinal)) return name.EndsWith("Border", StringComparison.Ordinal) ? t.StrongBorder : t.Surface;
        if (name.StartsWith("AutoHideTab", StringComparison.Ordinal))
            return name.EndsWith("HoveredBorder", StringComparison.Ordinal) ? t.Accent : name.EndsWith("Border", StringComparison.Ordinal) ? t.Border : name.EndsWith("HoveredText", StringComparison.Ordinal) ? t.Text : name.EndsWith("Text", StringComparison.Ordinal) ? t.Muted : t.Window;
        // Remaining caption, tab and button keys: quiet surfaces, text for emphasis, hover fills.
        if (name.EndsWith("Glyph", StringComparison.Ordinal)) return name.Contains("Hovered", StringComparison.Ordinal) || name.Contains("Pressed", StringComparison.Ordinal) || name.Contains("SelectedActive", StringComparison.Ordinal) || name.Contains("ActiveGlyph", StringComparison.Ordinal) ? t.Text : t.Muted;
        if (name.EndsWith("Text", StringComparison.Ordinal)) return name.Contains("Unselected", StringComparison.Ordinal) && !name.Contains("Hovered", StringComparison.Ordinal) || name.Contains("Inactive", StringComparison.Ordinal) ? t.Muted : t.Text;
        if (name.Contains("Pressed", StringComparison.Ordinal)) return t.Pressed;
        if (name.Contains("Hovered", StringComparison.Ordinal)) return t.Hover;
        if (name.Contains("DocumentWellTabSelected", StringComparison.Ordinal)) return t.Editor;
        return t.Surface;
    }

    private static Color? MenuColor(string name, Tones t) => name switch
    {
        "MenuSeparatorBorderBrushKey" => t.Border,
        "MenuBorderBrushKey" => t.StrongBorder,
        "CheckMarkBorderBrushKey" or "DisabledSubMenuItemBorderBrushKey" => Colors.Transparent,
        "CheckMarkForegroundBrushKey" or "TextBrushKey" => t.Text,
        "ItemTextDisabledKey" => t.Muted,
        "MenuItemHighlightedBackgroundKey" or "SubmenuItemBackgroundHighlightedKey" or "ItemBackgroundSelectedKey" or "ItemBackgroundHoverKey" or "FocusScrollButtonBrushKey" => t.Hover,
        "CheckMarkBackgroundBrushKey" or "DisabledSubMenuItemBackgroundBrushKey" => Colors.Transparent,
        _ => t.Surface
    };
}
