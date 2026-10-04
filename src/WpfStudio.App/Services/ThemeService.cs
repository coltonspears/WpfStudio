using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace WpfStudio.App.Services;

/// <summary>Application palette and dynamic syntax brushes shared by source and SQL editors.</summary>
public static class ThemeService
{
    private static bool syntaxConfigured;

    /// <summary>Raised after brushes have been updated so views can refresh non-brush theme state.</summary>
    public static event Action<bool>? Applied;
    public static bool IsLight { get; private set; }

    // Graphite: quiet neutral chrome, one indigo accent, and restrained status colours.
    // Keys are shared by every pane (including Database and Runtime views) through DynamicResource.
    private static readonly (string Key, string Dark, string Light)[] Palette =
    [
        ("WindowBrush",           "#131417", "#EDEEF1"), // title bar, toolbar, status bar, dock gutters
        ("SurfaceBrush",          "#18191D", "#F6F7F9"), // tool panes
        ("EditorBrush",           "#1C1D22", "#FFFFFF"), // documents and text inputs
        ("RaisedBrush",           "#23252B", "#FFFFFF"), // buttons, cards
        ("HoverBrush",            "#2A2C33", "#E6E7EB"),
        ("PressedBrush",          "#32353D", "#DADCE2"),
        ("BorderBrush",           "#26282E", "#E0E1E6"), // hairline dividers
        ("StrongBorderBrush",     "#34373F", "#CBCED6"), // input and card outlines
        ("TextBrush",             "#E4E5E9", "#1C1D21"),
        ("MutedBrush",            "#9397A1", "#5B5F69"),
        ("SubtleBrush",           "#6C707B", "#8A8E98"),
        ("AccentBrush",           "#8C94FF", "#4A52D4"), // accent text, icons, focus
        ("AccentFillBrush",       "#5B63E6", "#4A52D4"), // primary buttons
        ("AccentHoverBrush",      "#6B73F0", "#3C44C4"),
        ("OnAccentBrush",         "#FFFFFF", "#FFFFFF"),
        ("AccentSoftBrush",       "#272B4D", "#E4E6FB"), // badges, soft highlights
        ("SelectionBrush",        "#2B2F54", "#DCDFFA"),
        ("InactiveSelectionBrush","#25272E", "#E5E6EB"),
        ("SuccessBrush",          "#5CC896", "#1D8656"),
        ("WarningBrush",          "#E3B458", "#946300"),
        ("DangerBrush",           "#F0736B", "#C53B33"),
        ("DebugBrush",            "#B4531F", "#B4531F"), // status bar while debugging
        ("OverlayBrush",          "#A60A0B0D", "#660F1115"),
        ("EditorLineBrush",       "#22242A", "#F4F5F8"),
        ("EditorSelectionBrush",  "#33386A", "#CFD4F8"),
        ("GutterTextBrush",       "#555A64", "#A3A7B0"),
        ("CanvasBrush",           "#111215", "#E8E9ED"), // designer pasteboard around the artboard
        ("CanvasDotBrush",        "#24262D", "#D2D4DB"), // designer pasteboard grid dots
        ("DangerSoftBrush",       "#3A2327", "#FCEBEA"), // error banners
        ("WarningSoftBrush",      "#3A3223", "#FBF2DE"),
        ("SyntaxKeyword",         "#A3AAFF", "#4B3DC9"),
        ("SyntaxString",          "#E2B883", "#955A12"),
        ("SyntaxComment",         "#6E7681", "#6B7280"),
        ("SyntaxNumber",          "#EFA07C", "#B2481A"),
        ("SyntaxType",            "#5FCDBE", "#0B7A6E"),
        ("SyntaxAttribute",       "#9CC7FF", "#1F5FB8"),
        // Categorical data colours (profiler treemaps, generation bars). Fixed order, validated for colour-vision
        // deficiency on both surfaces; use them for identity only and never for status.
        ("Chart1Brush",           "#3987E5", "#2A78D6"),
        ("Chart2Brush",           "#D95926", "#EB6834"),
        ("Chart3Brush",           "#199E70", "#1BAF7A"),
        ("Chart4Brush",           "#C98500", "#EDA100"),
        ("Chart5Brush",           "#D55181", "#E87BA4"),
        ("Chart6Brush",           "#008300", "#008300"),
        ("Chart7Brush",           "#9085E9", "#4A3AA7"),
        ("Chart8Brush",           "#E66767", "#E34948"),
    ];

    public static void Apply(string name)
    {
        if (Application.Current is not { } app) return;
        bool light = name.Equals("Light", StringComparison.OrdinalIgnoreCase);
        IsLight = light;
        foreach (var (key, dark, pale) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(light ? pale : dark);
            if (app.TryFindResource(key) is SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
            else app.Resources[key] = new SolidColorBrush(color);
        }
        app.Resources[SystemColors.ControlBrushKey] = app.FindResource("SurfaceBrush");
        app.Resources[SystemColors.ControlTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.WindowBrushKey] = app.FindResource("EditorBrush");
        app.Resources[SystemColors.WindowTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.MenuBrushKey] = app.FindResource("SurfaceBrush");
        app.Resources[SystemColors.MenuTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.HighlightBrushKey] = app.FindResource("SelectionBrush");
        app.Resources[SystemColors.HighlightTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = app.FindResource("InactiveSelectionBrush");
        app.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = app.FindResource("TextBrush");
        app.Resources[SystemColors.GrayTextBrushKey] = app.FindResource("SubtleBrush");
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
        foreach (Window window in app.Windows) { window.InvalidateVisual(); WindowChromeService.ApplyFrame(window, light); }
        Applied?.Invoke(light);
    }
    private sealed class ResourceHighlightingBrush(string key) : HighlightingBrush
    {
        public override Brush GetBrush(ITextRunConstructionContext context) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.SteelBlue;
    }
}
