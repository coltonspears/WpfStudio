using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace WpfStudio.App.Services;

/// <summary>Application palette and dynamic syntax brushes shared by source and SQL editors.</summary>
public static class ThemeService
{
    private static bool syntaxConfigured;
    public static void Apply(string name)
    {
        if (Application.Current is not { } app) return;
        bool light = name.Equals("Light", StringComparison.OrdinalIgnoreCase);
        string[] keys = ["WindowBrush", "SurfaceBrush", "RaisedBrush", "EditorBrush", "BorderBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentFillBrush", "SelectionBrush", "SuccessBrush", "WarningBrush", "OverlayBrush", "SyntaxKeyword", "SyntaxString", "SyntaxComment", "SyntaxNumber", "SyntaxType", "SyntaxAttribute"];
        string[] dark = ["#151B23", "#1C2430", "#243040", "#121922", "#354355", "#E2E9F2", "#9AAAC0", "#69A8ED", "#244C77", "#304D70", "#76D7B1", "#F0C982", "#A6101720", "#9CBCFF", "#D7B984", "#8BA58F", "#A8D8BC", "#79C8CF", "#BED7F5"];
        string[] pale = ["#E9EEF4", "#F7F9FC", "#E4EBF4", "#FFFFFF", "#C6D1DF", "#243044", "#52647E", "#245F9E", "#D9E9FA", "#C9DCF2", "#197552", "#865600", "#80616D7D", "#3155A5", "#8E5919", "#527345", "#246C4E", "#087C85", "#665398"];
        var palette = light ? pale : dark;
        for (var i = 0; i < keys.Length; i++)
        {
            var color = (Color)ColorConverter.ConvertFromString(palette[i]);
            if (app.TryFindResource(keys[i]) is SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
            else app.Resources[keys[i]] = new SolidColorBrush(color);
        }
        app.Resources[SystemColors.ControlBrushKey] = app.Resources["SurfaceBrush"] ?? app.FindResource("SurfaceBrush");
        app.Resources[SystemColors.ControlTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.WindowBrushKey] = app.FindResource("EditorBrush");
        app.Resources[SystemColors.WindowTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.MenuBrushKey] = app.FindResource("SurfaceBrush");
        app.Resources[SystemColors.MenuTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.HighlightBrushKey] = app.FindResource("SelectionBrush");
        app.Resources[SystemColors.HighlightTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = app.FindResource("SelectionBrush");
        if (!syntaxConfigured)
        {
            foreach (var language in new[] { "C#", "XML", "JavaScript", "SQL" })
            {
                var definition = HighlightingManager.Instance.GetDefinition(language);
                if (definition is null) continue;
                foreach (var color in definition.NamedHighlightingColors.Where(c => !c.IsFrozen))
                {
                    var label = (color.Name ?? "").ToLowerInvariant();
                    var key = label.Contains("comment") ? "SyntaxComment" : label.Contains("string") || label.Contains("char") || label.Contains("value") ? "SyntaxString" : label.Contains("number") || label.Contains("digit") ? "SyntaxNumber" : label.Contains("type") || label.Contains("tag") ? "SyntaxType" : label.Contains("attribute") ? "SyntaxAttribute" : "SyntaxKeyword";
                    color.Foreground = new ResourceHighlightingBrush(key);
                }
            }
            syntaxConfigured = true;
        }
        foreach (Window window in app.Windows) window.InvalidateVisual();
    }
    private sealed class ResourceHighlightingBrush(string key) : HighlightingBrush
    {
        public override Brush GetBrush(ITextRunConstructionContext context) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.SteelBlue;
    }
}
