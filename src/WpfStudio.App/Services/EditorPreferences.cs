using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core;

namespace WpfStudio.App.Services;

/// <summary>
/// Editor settings every code editor follows live: font, indentation, what is drawn and how text behaves. One
/// instance, from the container, is shared by the settings page and all editors; <see cref="Load"/> and
/// <see cref="ToSettings"/> map it to the saved settings file.
/// </summary>
public sealed partial class EditorPreferences(FontCatalog fonts) : ObservableObject
{
    public const double MinimumFontSize = 8, MaximumFontSize = 32;

    /// <summary>The families the settings page offers.</summary>
    public FontCatalog Fonts { get; } = fonts;

    [ObservableProperty] public partial string FontFamily { get; set; } = EditorSettings.DefaultFontFamily;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(QuotedFontSize))] public partial double FontSize { get; set; } = 13;
    /// <summary>Editor text quoted in a popup, a step smaller than the editor itself.</summary>
    public double QuotedFontSize => Math.Max(MinimumFontSize, FontSize - 1);
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
