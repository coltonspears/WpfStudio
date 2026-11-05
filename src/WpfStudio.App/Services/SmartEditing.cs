using System.Text;

namespace WpfStudio.App.Services;

/// <summary>
/// Indentation-aware Backspace, Delete and paste for any language, planned from the text alone. Each returns null to
/// leave the key to the editor's default handling.
/// </summary>
internal static class SmartEditing
{
    /// <summary>
    /// Backspace in indentation. On a line of nothing but spaces and tabs it removes the line, leaving the caret at the end
    /// of the line above. In the indentation of a line with text it removes back to the previous indentation stop.
    /// </summary>
    public static XamlTypingEdit? Backspace(string text, int caret, int indentSize)
    {
        if (caret <= 0 || caret > text.Length || indentSize < 1) return null;
        int lineStart = LineStart(text, caret), lineEnd = LineEnd(text, caret);
        if (caret == lineStart || !IsBlank(text, lineStart, caret)) return null;
        if (IsBlank(text, caret, lineEnd))
        {
            // The first line has no line above to return to, so only its whitespace goes.
            if (lineStart == 0) return new(new(0, lineEnd, ""), 0);
            int breakStart = lineStart - 1;
            if (breakStart > 0 && text[breakStart] == '\n' && text[breakStart - 1] == '\r') breakStart--;
            return new(new(breakStart, lineEnd - breakStart, ""), breakStart);
        }
        // A tab is one character and one stop already.
        if (text[caret - 1] == '\t') return null;
        int column = Width(text.AsSpan(lineStart, caret - lineStart), indentSize);
        int stop = (column - 1) / indentSize * indentSize;
        int start = caret;
        while (start > lineStart && text[start - 1] == ' ' && column > stop) { start--; column--; }
        return caret - start > 1 ? new(new(start, caret - start, ""), start) : null;
    }

    /// <summary>
    /// Delete at the end of a line that has text joins the next line onto it without that line's indentation, so the two
    /// meet where the caret is instead of a run of spaces apart.
    /// </summary>
    public static XamlTypingEdit? Delete(string text, int caret)
    {
        if (caret < 0 || caret >= text.Length) return null;
        int lineStart = LineStart(text, caret), lineEnd = LineEnd(text, caret);
        if (lineEnd >= text.Length || !IsBlank(text, caret, lineEnd) || IsBlank(text, lineStart, lineEnd)) return null;
        int next = lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1);
        int content = next;
        while (content < text.Length && text[content] is ' ' or '\t') content++;
        // Nothing but the line break to remove: the default does exactly that.
        if (content == next && caret == lineEnd) return null;
        return new(new(caret, content - caret, ""), caret);
    }

    /// <summary>
    /// Pasted lines moved to the indentation of the line they are pasted on. Only when the caret stands in indentation
    /// with nothing before it on its line, which is where code is pasted as lines; a paste into the middle of a line is
    /// left exactly as copied. Null when there is nothing to change.
    /// </summary>
    public static string? ReindentPaste(string text, int caret, string pasted, int indentSize, bool insertSpaces)
    {
        if (caret < 0 || caret > text.Length || indentSize < 1 || !pasted.Contains('\n')) return null;
        int lineStart = LineStart(text, caret);
        if (caret == lineStart || !IsBlank(text, lineStart, caret)) return null;
        var lines = pasted.Split('\n');
        static bool Empty(string line) => line.AsSpan().Trim().IsEmpty;
        var rest = lines.Skip(1).Where(line => !Empty(line)).ToArray();
        if (rest.Length == 0) return null;
        int Indent(string line) => Width(line.AsSpan(0, line.Length - line.AsSpan().TrimStart(" \t").Length), indentSize);
        int origin = rest.Min(Indent);
        int first = Indent(lines[0]);
        if (first > 0) origin = Math.Min(origin, first);
        // A selection usually starts at the first line's text, so that line's own indentation is unknown. When it opens
        // something that the selection does not close, the lines after it are its contents and sit one level deeper.
        else if (OpensBlock(lines[0]) && !rest.Any(line => Indent(line) == origin && ClosesBlock(line))) origin = Math.Max(0, origin - indentSize);
        int target = Width(text.AsSpan(lineStart, caret - lineStart), indentSize);
        if (origin == target && first == 0) return null;
        var result = new StringBuilder(pasted.Length + lines.Length * 4);
        result.Append(lines[0].AsSpan().TrimStart(" \t")); // The caret's own indentation is already there.
        foreach (var line in lines.Skip(1))
        {
            result.Append('\n');
            if (Empty(line)) { result.Append(line.AsSpan().TrimStart(" \t")); continue; }
            int width = Math.Max(0, target + Indent(line) - origin);
            if (insertSpaces) result.Append(' ', width);
            else result.Append('\t', width / indentSize).Append(' ', width % indentSize);
            result.Append(line.AsSpan().TrimStart(" \t"));
        }
        var reindented = result.ToString();
        return reindented == pasted ? null : reindented;
    }

    private static bool OpensBlock(string line)
    {
        var code = line.AsSpan().TrimEnd();
        if (code.IsEmpty) return false;
        if (code[^1] is '{' or '(' or '[' or ',' or '=' || code.EndsWith("=>")) return true;
        // An XAML start tag still open at the end of the line: its attributes follow.
        return code.LastIndexOf('<') > code.LastIndexOf('>');
    }

    private static bool ClosesBlock(string line)
    {
        var code = line.AsSpan().TrimStart();
        return code.Length > 0 && (code[0] is '}' or ')' or ']' || code.StartsWith("</") || code.StartsWith("/>"));
    }

    /// <summary>The width of indentation in columns, a tab reaching to the next stop.</summary>
    internal static int Width(ReadOnlySpan<char> whitespace, int indentSize)
    {
        int column = 0;
        foreach (char c in whitespace) column = c == '\t' ? (column / indentSize + 1) * indentSize : column + 1;
        return column;
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (int i = start; i < end; i++) if (text[i] is not (' ' or '\t')) return false;
        return true;
    }
    private static int LineStart(string text, int offset) { while (offset > 0 && text[offset - 1] is not ('\n' or '\r')) offset--; return offset; }
    private static int LineEnd(string text, int offset) { while (offset < text.Length && text[offset] is not ('\n' or '\r')) offset++; return offset; }
}
