using System.Windows.Media;

namespace WpfStudio.App.Services;

/// <summary>
/// One theme's colours. The table below is the only place their values are written; StudioTheme.xaml repeats the dark
/// values only as the brushes' starting colours.
/// </summary>
public sealed record Palette
{
    /// <summary>Application resource holding the current palette, for views that XAML creates out of the container's reach.</summary>
    public const string ResourceKey = "StudioPalette";

    // Graphite: quiet neutral chrome, one indigo accent, and restrained status colours.
    // Keys are shared by every pane (including Database and Runtime views) through DynamicResource.
    private static readonly (string Key, string Dark, string Light)[] Table =
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
        ("WindowFrameBrush",      "#2E3037", "#CBCED6"), // the border Windows draws around each window
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
        // Semantic colouring from the language worker, layered over the syntax colours above.
        ("SyntaxInterface",       "#8FDDA8", "#227A47"),
        ("SyntaxEnum",            "#B8D98A", "#5C7A12"),
        ("SyntaxTypeParameter",   "#7FC4BA", "#2A6F67"),
        ("SyntaxMethod",          "#E8D48F", "#7D5B00"),
        ("SyntaxMember",          "#D7A8E8", "#8A3D9E"), // properties, fields, events, constants, enum members
        ("SyntaxParameter",       "#8FC6EA", "#1E6894"),
        ("InlayHintBrush",        "#8A8F9B", "#767B86"),
        ("InlayHintBackgroundBrush","#26282F", "#EFF0F3"),
        // Quiet editor guides: the bracket pair at the caret, other uses of the selected word, and indentation levels.
        ("BracketMatchBrush",     "#3A4072", "#D2D6F8"),
        ("BracketMatchBorderBrush","#5E66BE", "#9BA2E6"),
        ("OccurrenceBrush",       "#31343F", "#E4E6EE"),
        // What Find in file matched: warmer than the selection and the occurrences, and dim enough for the text to read.
        ("FindMatchBrush",        "#5C4A1A", "#FBE2A0"),
        ("IndentGuideBrush",      "#2A2C33", "#E6E7EC"),
        // Code folding: gutter chevrons, the band over a region about to fold, and the folded-block pill.
        ("FoldMarkerBrush",       "#4A4E58", "#B4B7BF"),
        ("FoldMarkerHotBrush",    "#9397A1", "#5B5F69"),
        ("FoldRegionBrush",       "#168C94FF", "#124A52D4"),
        ("FoldPlaceholderBrush",  "#A9AEBA", "#4E525C"),
        ("FoldPlaceholderBackgroundBrush", "#272A35", "#EEEFF5"),
        ("FoldPlaceholderBorderBrush", "#363A48", "#D6D8E2"),
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

    public static Palette Dark { get; } = new(isLight: false);
    public static Palette Light { get; } = new(isLight: true);
    public static Palette For(string themeName) => themeName.Equals("Light", StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    private Palette(bool isLight)
    {
        IsLight = isLight;
        Colors = Table.ToDictionary(row => row.Key, row => (Color)ColorConverter.ConvertFromString(isLight ? row.Light : row.Dark));
    }

    public bool IsLight { get; }
    /// <summary>Every colour by its application resource key.</summary>
    public IReadOnlyDictionary<string, Color> Colors { get; }

    public Color Window => Colors["WindowBrush"];
    public Color Surface => Colors["SurfaceBrush"];
    public Color Editor => Colors["EditorBrush"];
    public Color Hover => Colors["HoverBrush"];
    public Color Pressed => Colors["PressedBrush"];
    public Color Border => Colors["BorderBrush"];
    public Color StrongBorder => Colors["StrongBorderBrush"];
    public Color Text => Colors["TextBrush"];
    public Color Muted => Colors["MutedBrush"];
    public Color Accent => Colors["AccentBrush"];
    public Color AccentFill => Colors["AccentFillBrush"];
    public Color OnAccent => Colors["OnAccentBrush"];
    public Color WindowFrame => Colors["WindowFrameBrush"];
}
