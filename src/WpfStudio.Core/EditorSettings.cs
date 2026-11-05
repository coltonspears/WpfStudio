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
    /// <summary>Typing an opening bracket or quote in C# also types its closing one, and typing over that one moves past it.</summary>
    public bool AutoCloseBrackets { get; init; } = true;
    /// <summary>Backspace in a line's indentation removes a whole level, and on a line of nothing but indentation removes the line.</summary>
    public bool SmartBackspace { get; init; } = true;
    /// <summary>Pasted lines of C# and XAML are moved to the indentation of the line they are pasted on.</summary>
    public bool IndentPastedLines { get; init; } = true;
    public bool HighlightMatchingBrackets { get; init; } = true;
    public bool ShowIndentationGuides { get; init; } = true;
    /// <summary>Other occurrences of the selected word are marked.</summary>
    public bool HighlightOccurrences { get; init; } = true;
    /// <summary>Ctrl+Click on a name goes to its definition.</summary>
    public bool ControlClickNavigation { get; init; } = true;
    /// <summary>The completion list opens as a name is typed, not only on Ctrl+Space or after a dot.</summary>
    public bool CompleteWhileTyping { get; init; } = true;
    /// <summary>A change to project files on disk refreshes the project model without asking.</summary>
    public bool AutoRefreshProject { get; init; } = true;
}
