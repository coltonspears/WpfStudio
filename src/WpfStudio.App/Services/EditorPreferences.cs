using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core;

namespace WpfStudio.App.Services;

/// <summary>
/// Editor settings every code editor follows live: font, indentation, what is drawn and how text behaves. One
/// instance is shared by the settings page and all editors; <see cref="Load"/> and <see cref="ToSettings"/> map it
/// to the saved settings file.
/// </summary>
public sealed partial class EditorPreferences : ObservableObject
{
    public static EditorPreferences Current { get; } = new();
    public const double MinimumFontSize = 8, MaximumFontSize = 32;

    [ObservableProperty] public partial string FontFamily { get; set; } = EditorSettings.DefaultFontFamily;
    [ObservableProperty] public partial double FontSize { get; set; } = 13;
    [ObservableProperty] public partial int TabSize { get; set; } = 4;
    [ObservableProperty] public partial bool InsertSpaces { get; set; } = true;
    [ObservableProperty] public partial bool ShowLineNumbers { get; set; } = true;
    [ObservableProperty] public partial bool HighlightCurrentLine { get; set; } = true;
    [ObservableProperty] public partial bool WordWrap { get; set; }
    [ObservableProperty] public partial bool ShowWhitespace { get; set; }
    [ObservableProperty] public partial bool ShowLineEndings { get; set; }
    [ObservableProperty] public partial bool ShowColumnRuler { get; set; }
    [ObservableProperty] public partial int ColumnRulerPosition { get; set; } = 120;
    [ObservableProperty] public partial bool ShowFolding { get; set; } = true;
    [ObservableProperty] public partial bool ScrollBelowDocument { get; set; }
    [ObservableProperty] public partial bool CutCopyWholeLine { get; set; } = true;
    [ObservableProperty] public partial bool EnableTextDragDrop { get; set; } = true;
    [ObservableProperty] public partial bool HideCursorWhileTyping { get; set; }
    [ObservableProperty] public partial bool TrimTrailingWhitespace { get; set; }
    [ObservableProperty] public partial bool EnsureFinalNewline { get; set; }
    [ObservableProperty] public partial bool AutoSave { get; set; }
    [ObservableProperty] public partial int AutoSaveDelaySeconds { get; set; } = 2;

    // Values typed into the settings page are kept inside ranges the editor can draw.
    partial void OnFontSizeChanged(double value) { if (!double.IsFinite(value) || value < MinimumFontSize || value > MaximumFontSize) FontSize = Math.Clamp(double.IsFinite(value) ? value : 13, MinimumFontSize, MaximumFontSize); }
    partial void OnTabSizeChanged(int value) { if (value is < 1 or > 16) TabSize = Math.Clamp(value, 1, 16); }
    partial void OnColumnRulerPositionChanged(int value) { if (value is < 1 or > 400) ColumnRulerPosition = Math.Clamp(value, 1, 400); }
    partial void OnAutoSaveDelaySecondsChanged(int value) { if (value is < 1 or > 600) AutoSaveDelaySeconds = Math.Clamp(value, 1, 600); }
    partial void OnFontFamilyChanged(string value) { if (string.IsNullOrWhiteSpace(value)) FontFamily = EditorSettings.DefaultFontFamily; OnPropertyChanged(nameof(EditorFont)); }

    /// <summary>The configured font; a family that is not installed falls back to the next one in the list, then to Consolas.</summary>
    public FontFamily EditorFont => new(FontFamily + ", Consolas");

    public static IReadOnlyList<int> TabSizes { get; } = [2, 3, 4, 8];
    public static IReadOnlyList<int> AutoSaveDelays { get; } = [1, 2, 5, 10, 30, 60];
    public static IReadOnlyList<double> FontSizes { get; } = [9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];

    private IReadOnlyList<string>? _families;
    private bool _listingFamilies;
    /// <summary>
    /// Installed font families, fixed-width ones first since code is aligned in columns. Measuring every installed
    /// font takes a moment, so the list fills in from a background thread the first time it is shown.
    /// </summary>
    public IReadOnlyList<string> FontFamilies
    {
        get
        {
            if (_families is null && !_listingFamilies)
            {
                _listingFamilies = true;
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                _ = Task.Run(ListFamilies).ContinueWith(listed => dispatcher.BeginInvoke(() =>
                {
                    _families = listed.Status == TaskStatus.RanToCompletion ? listed.Result : [EditorSettings.DefaultFontFamily];
                    OnPropertyChanged(nameof(FontFamilies));
                }), TaskScheduler.Default);
            }
            return _families ?? [];
        }
    }

    private static IReadOnlyList<string> ListFamilies()
    {
        var language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        var families = new List<(string Name, bool Monospace)>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var name = family.FamilyNames.TryGetValue(language, out var localized) ? localized : family.Source;
            families.Add((name, IsMonospace(family)));
        }
        return families.DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Monospace ? 0 : 1).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Select(item => item.Name).ToArray();
    }

    private static bool IsMonospace(FontFamily family)
    {
        try
        {
            var typeface = family.GetTypefaces().FirstOrDefault(face => face.Style == FontStyles.Normal && face.Weight == FontWeights.Normal) ?? family.GetTypefaces().FirstOrDefault();
            if (typeface is null || !typeface.TryGetGlyphTypeface(out var glyphs)) return false;
            double Width(char character) => glyphs.CharacterToGlyphMap.TryGetValue(character, out var glyph) ? glyphs.AdvanceWidths[glyph] : -1;
            var narrow = Width('i');
            return narrow > 0 && Math.Abs(narrow - Width('W')) < 0.001 && Math.Abs(narrow - Width('.')) < 0.001;
        }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException or UnauthorizedAccessException) { return false; }
    }

    public void Load(EditorSettings settings)
    {
        FontFamily = settings.FontFamily; FontSize = settings.FontSize; TabSize = settings.TabSize; InsertSpaces = settings.InsertSpaces;
        ShowLineNumbers = settings.ShowLineNumbers; HighlightCurrentLine = settings.HighlightCurrentLine; WordWrap = settings.WordWrap;
        ShowWhitespace = settings.ShowWhitespace; ShowLineEndings = settings.ShowLineEndings; ShowColumnRuler = settings.ShowColumnRuler;
        ColumnRulerPosition = settings.ColumnRulerPosition; ShowFolding = settings.ShowFolding; ScrollBelowDocument = settings.ScrollBelowDocument;
        CutCopyWholeLine = settings.CutCopyWholeLine; EnableTextDragDrop = settings.EnableTextDragDrop; HideCursorWhileTyping = settings.HideCursorWhileTyping;
        TrimTrailingWhitespace = settings.TrimTrailingWhitespace; EnsureFinalNewline = settings.EnsureFinalNewline;
        AutoSave = settings.AutoSave; AutoSaveDelaySeconds = settings.AutoSaveDelaySeconds;
    }

    public EditorSettings ToSettings() => new()
    {
        FontFamily = FontFamily, FontSize = FontSize, TabSize = TabSize, InsertSpaces = InsertSpaces, ShowLineNumbers = ShowLineNumbers,
        HighlightCurrentLine = HighlightCurrentLine, WordWrap = WordWrap, ShowWhitespace = ShowWhitespace, ShowLineEndings = ShowLineEndings,
        ShowColumnRuler = ShowColumnRuler, ColumnRulerPosition = ColumnRulerPosition, ShowFolding = ShowFolding, ScrollBelowDocument = ScrollBelowDocument,
        CutCopyWholeLine = CutCopyWholeLine, EnableTextDragDrop = EnableTextDragDrop, HideCursorWhileTyping = HideCursorWhileTyping,
        TrimTrailingWhitespace = TrimTrailingWhitespace, EnsureFinalNewline = EnsureFinalNewline, AutoSave = AutoSave, AutoSaveDelaySeconds = AutoSaveDelaySeconds
    };

    [RelayCommand] private void Reset() => Load(new EditorSettings());
    [RelayCommand] private void ZoomIn() => FontSize = Math.Min(MaximumFontSize, FontSize + 1);
    [RelayCommand] private void ZoomOut() => FontSize = Math.Max(MinimumFontSize, FontSize - 1);
    [RelayCommand] private void ResetZoom() => FontSize = new EditorSettings().FontSize;
}
