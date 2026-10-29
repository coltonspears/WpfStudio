namespace WpfStudio.Core.Text;

/// <summary>A replacement of <see cref="Length"/> characters at <see cref="Start"/>.</summary>
public readonly record struct TextReplacement(int Start, int Length, string Text);

/// <summary>Save-time tidying: trailing spaces and tabs removed, and a final line break added.</summary>
public static class WhitespaceCleanup
{
    /// <summary>
    /// The edits that tidy <paramref name="text"/>, in document order. Markdown keeps trailing spaces, which mean a
    /// line break there. The final line break matches the file's own line endings.
    /// </summary>
    public static IReadOnlyList<TextReplacement> Edits(string text, string extension, bool trimTrailingWhitespace, bool ensureFinalNewline)
    {
        var edits = new List<TextReplacement>();
        if (trimTrailingWhitespace && !extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            for (var lineStart = 0; lineStart <= text.Length;)
            {
                var lineEnd = text.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = text.Length;
                var contentEnd = lineEnd > lineStart && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                var trimmed = contentEnd;
                while (trimmed > lineStart && text[trimmed - 1] is ' ' or '\t') trimmed--;
                if (trimmed < contentEnd) edits.Add(new(trimmed, contentEnd - trimmed, ""));
                lineStart = lineEnd + 1;
            }
        }
        if (ensureFinalNewline && text.Length > 0 && text[^1] != '\n')
            edits.Add(new(text.Length, 0, text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n"));
        return edits;
    }

    public static string Apply(string text, IReadOnlyList<TextReplacement> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        return text;
    }
}
