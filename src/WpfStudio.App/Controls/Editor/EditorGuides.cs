using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using WpfStudio.App.Services;

namespace WpfStudio.App.Controls.Editor;

/// <summary>
/// What the editor draws behind the text to help read it: the bracket pair at the caret, the other occurrences of the
/// selected word, a line down each indentation level, and the underline under a name Ctrl is about to follow.
/// Each can be turned off in the editor settings; nothing here changes the text.
/// </summary>
internal sealed class EditorGuides : IBackgroundRenderer
{
    // Looking for a partner further away than this is not worth a pause at every caret move.
    private const int BracketSearch = 100_000;
    private const int MaximumOccurrences = 400;
    private readonly EditorSurface _owner;
    private (int First, int Second)? _brackets;
    private string? _word;
    private (int Start, int Length)? _link;
    private Pen? _guidePen, _bracketPen, _linkPen;

    public EditorGuides(EditorSurface owner)
    {
        _owner = owner;
        owner.TextArea.TextView.BackgroundRenderers.Add(this);
        owner.TextArea.Caret.PositionChanged += (_, _) => UpdateBrackets();
        // An edit that leaves the caret where it is, such as Delete or a replacement elsewhere, still moves the brackets after it.
        owner.TextChanged += (_, _) => UpdateBrackets();
        owner.TextArea.SelectionChanged += (_, _) => UpdateWord();
    }

    public KnownLayer Layer => KnownLayer.Selection;

    private EditorPreferences? Preferences => _owner.ViewModel?.Preferences;

    /// <summary>The document or a setting changed: what was found for the old state no longer applies.</summary>
    public void Refresh()
    {
        UpdateBrackets();
        UpdateWord();
        _owner.TextArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>Underlines the name Ctrl+Click would follow, or clears the underline.</summary>
    public void SetLink((int Start, int Length)? link)
    {
        if (_link == link) return;
        _link = link;
        _owner.TextArea.TextView.InvalidateLayer(Layer);
    }

    public bool HasLink => _link is not null;

    private void UpdateBrackets()
    {
        var found = Preferences?.HighlightMatchingBrackets == false ? null : FindBrackets(_owner.Document, _owner.CaretOffset);
        if (found == _brackets) return;
        _brackets = found;
        _owner.TextArea.TextView.InvalidateLayer(Layer);
    }

    private void UpdateWord()
    {
        string? word = null;
        if (Preferences?.HighlightOccurrences != false && _owner.SelectionLength is >= 2 and <= 100)
        {
            var selected = _owner.Document.GetText(_owner.SelectionStart, _owner.SelectionLength);
            if (selected.All(IsWordPart) && IsWholeWord(_owner.Document, _owner.SelectionStart, selected.Length)) word = selected;
        }
        if (word == _word) return;
        _word = word;
        _owner.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        var document = _owner.Document;
        if (Preferences?.ShowIndentationGuides != false) DrawIndentationGuides(textView, context, document);
        if (_word is { } word) DrawOccurrences(textView, context, document, word);
        if (_brackets is { } brackets && brackets.Second < document.TextLength)
        {
            var fill = Brush("BracketMatchBrush", Brushes.Transparent);
            var pen = EditorSurface.ThemedPen(Brush("BracketMatchBorderBrush", Brushes.Gray), 1, ref _bracketPen);
            foreach (var offset in new[] { brackets.First, brackets.Second })
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = offset, Length = 1 }))
                context.DrawRoundedRectangle(fill, pen, new Rect(Math.Round(rect.X) + 0.5, Math.Round(rect.Y) + 0.5, Math.Max(1, Math.Round(rect.Width) - 1), Math.Max(1, Math.Round(rect.Height) - 1)), 2, 2);
        }
        if (_link is { } link && link.Start + link.Length <= document.TextLength)
        {
            var pen = EditorSurface.ThemedPen(Brush("AccentBrush", Brushes.SteelBlue), 1, ref _linkPen);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = link.Start, Length = link.Length }))
                context.DrawLine(pen, new Point(rect.Left, Math.Round(rect.Bottom) - 0.5), new Point(rect.Right, Math.Round(rect.Bottom) - 0.5));
        }
    }

    // A thin line at each indentation level a line sits inside. Blank lines continue the block around them.
    private void DrawIndentationGuides(TextView textView, DrawingContext context, TextDocument document)
    {
        int size = Math.Max(1, _owner.Options.IndentationSize);
        var pen = EditorSurface.ThemedPen(Brush("IndentGuideBrush", Brushes.Gray), 1, ref _guidePen);
        double unit = textView.WideSpaceWidth * size;
        foreach (var visual in textView.VisualLines)
        {
            int levels = (IndentationOf(document, visual.FirstDocumentLine, size) - 1) / size;
            if (levels < 1) continue;
            double top = visual.VisualTop - textView.VerticalOffset, bottom = top + visual.Height;
            // Level 0 is the left edge, where a line would only repeat the gutter.
            for (int level = 1; level <= levels; level++)
            {
                double x = Math.Round(level * unit - textView.HorizontalOffset) + 0.5;
                if (x > 0) context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            }
        }
    }

    /// <summary>The columns a line is indented by. A blank line takes the smaller of its nearest neighbours with text, so a guide runs through it.</summary>
    private static int IndentationOf(TextDocument document, DocumentLine line, int size)
    {
        if (!IsBlankLine(document, line)) return Indentation(document, line, size);
        DocumentLine? above = line.PreviousLine, below = line.NextLine;
        for (int looked = 0; above is not null && looked < 200 && IsBlankLine(document, above); looked++) above = above.PreviousLine;
        for (int looked = 0; below is not null && looked < 200 && IsBlankLine(document, below); looked++) below = below.NextLine;
        if (above is null || below is null || IsBlankLine(document, above) || IsBlankLine(document, below)) return 0;
        return Math.Min(Indentation(document, above, size), Indentation(document, below, size));
    }

    private static int Indentation(TextDocument document, DocumentLine line, int size)
    {
        var whitespace = TextUtilities.GetLeadingWhitespace(document, line);
        return SmartEditing.Width(document.GetText(whitespace).AsSpan(), size);
    }

    private static bool IsBlankLine(TextDocument document, DocumentLine line) => TextUtilities.GetLeadingWhitespace(document, line).Length == line.Length;

    private void DrawOccurrences(TextView textView, DrawingContext context, TextDocument document, string word)
    {
        var fill = Brush("OccurrenceBrush", Brushes.Transparent);
        int first = textView.VisualLines[0].FirstDocumentLine.Offset, last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var visible = document.GetText(first, last - first);
        int selected = _owner.SelectionStart, drawn = 0;
        for (int at = visible.IndexOf(word, StringComparison.Ordinal); at >= 0 && drawn < MaximumOccurrences; at = visible.IndexOf(word, at + word.Length, StringComparison.Ordinal))
        {
            int offset = first + at;
            if (offset == selected || !IsWholeWord(document, offset, word.Length)) continue;
            drawn++;
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = offset, Length = word.Length }))
                context.DrawRoundedRectangle(fill, null, rect, 2, 2);
        }
    }

    /// <summary>The bracket beside the caret and its partner, earlier offset first. The one before the caret wins, as it is the one just typed or passed.</summary>
    internal static (int First, int Second)? FindBrackets(TextDocument document, int caret)
    {
        foreach (int offset in new[] { caret - 1, caret })
        {
            if (offset < 0 || offset >= document.TextLength) continue;
            char bracket = document.GetCharAt(offset);
            int direction = bracket is '(' or '[' or '{' ? 1 : bracket is ')' or ']' or '}' ? -1 : 0;
            if (direction == 0) continue;
            char partner = bracket switch { '(' => ')', '[' => ']', '{' => '}', ')' => '(', ']' => '[', _ => '{' };
            int depth = 0;
            for (int i = offset, steps = 0; i >= 0 && i < document.TextLength && steps < BracketSearch; i += direction, steps++)
            {
                char c = document.GetCharAt(i);
                if (c == bracket) depth++;
                else if (c == partner && --depth == 0) return direction > 0 ? (offset, i) : (i, offset);
            }
        }
        return null;
    }

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsWholeWord(TextDocument document, int start, int length) =>
        (start == 0 || !IsWordPart(document.GetCharAt(start - 1))) && (start + length >= document.TextLength || !IsWordPart(document.GetCharAt(start + length)));

    private Brush Brush(string key, Brush fallback) => _owner.TryFindResource(key) as Brush ?? fallback;
}
