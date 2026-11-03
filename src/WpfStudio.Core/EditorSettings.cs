namespace WpfStudio.Core;

/// <summary>How code editors look and behave. Defaults match WpfStudio before these were configurable.</summary>
public sealed record EditorSettings
{
    public const string DefaultFontFamily = "Cascadia Code, Consolas";
    public string FontFamily { get; init; } = DefaultFontFamily;
    public double FontSize { get; init; } = 13;
    public int TabSize { get; init; } = 4;
    public bool InsertSpaces { get; init; } = true;
    public bool ShowLineNumbers { get; init; } = true;
    public bool HighlightCurrentLine { get; init; } = true;
    public bool WordWrap { get; init; }
    public bool ShowWhitespace { get; init; }
    public bool ShowLineEndings { get; init; }
    public bool ShowColumnRuler { get; init; }
    public int ColumnRulerPosition { get; init; } = 120;
    public bool ShowFolding { get; init; } = true;
    public bool ScrollBelowDocument { get; init; }
    public bool CutCopyWholeLine { get; init; } = true;
    public bool EnableTextDragDrop { get; init; } = true;
    public bool HideCursorWhileTyping { get; init; }
    public bool TrimTrailingWhitespace { get; init; }
    public bool EnsureFinalNewline { get; init; }
    public bool AutoSave { get; init; }
    public int AutoSaveDelaySeconds { get; init; } = 2;
}
