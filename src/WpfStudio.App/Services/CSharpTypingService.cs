using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

/// <summary>
/// Brace-aware indentation for C# typing, planned from the text alone. Enter between braces opens an indented line between
/// them, Enter after a brace or a braceless <c>if</c>/<c>for</c> header indents one level, and a <c>{</c> or <c>}</c> typed
/// on an otherwise blank line snaps to the indentation of the statement or block it belongs to. Returns null to leave the
/// keystroke to the editor's default handling, including inside strings and comments.
/// </summary>
internal static class CSharpTypingService
{
    internal const int MaximumCharacters = 1_000_000;
    private static readonly string[] BracelessHeaders = ["if", "else", "for", "foreach", "while", "using", "lock", "fixed"];

    public static XamlTypingEdit? GetEdit(string text, int caret, int selectionLength, string input, string indentation = "    ", string newLine = "\r\n")
    {
        if (text.Length > MaximumCharacters || caret < 0 || caret > text.Length || selectionLength != 0) return null;
        if (input is not ("\n" or "\r" or "\r\n" or "{" or "}")) return null;
        var scan = Scan(text, caret);
        if (!scan.InCode) return null;
        int lineStart = LineStart(text, caret), lineEnd = LineEnd(text, caret);
        var before = text[lineStart..caret];
        var after = text[caret..lineEnd];
        var indent = LeadingWhitespace(before);
        return input switch
        {
            "{" => SnapOpeningBrace(text, lineStart, caret, before),
            "}" => SnapClosingBrace(text, lineStart, caret, before, scan),
            _ => Enter(text, lineStart, caret, before, after, indent, indentation, newLine, scan),
        };
    }

    private static XamlTypingEdit? Enter(string text, int lineStart, int caret, string before, string after, string indent, string unit, string newLine, ScanResult scan)
    {
        var beforeTrim = before.TrimEnd();
        var afterTrim = after.TrimStart();
        if (beforeTrim.Trim().Length == 0) return null;
        int start = lineStart + beforeTrim.Length, end = caret + (after.Length - afterTrim.Length);
        if (beforeTrim.EndsWith('{'))
        {
            var inner = indent + unit;
            // {|} → the closing brace moves to its own line under the opening line, with the caret on an indented line between.
            if (afterTrim.StartsWith('}')) return new(new(start, end - start, newLine + inner + newLine + indent), start + newLine.Length + inner.Length);
            return new(new(start, end - start, newLine + inner), start + newLine.Length + inner.Length);
        }
        if (afterTrim.StartsWith('}') && scan.OpenBraces.Count > 0)
        {
            var match = LeadingWhitespace(text[LineStart(text, scan.OpenBraces[^1])..]);
            return new(new(start, end - start, newLine + match), start + newLine.Length + match.Length);
        }
        if (afterTrim.Length == 0 && IsBracelessHeader(beforeTrim.TrimStart()))
        {
            var inner = indent + unit;
            return new(new(start, end - start, newLine + inner), start + newLine.Length + inner.Length);
        }
        return null;
    }

    /// <summary>A <c>{</c> typed on the indented line after <c>if (x)</c> lines up with the <c>if</c>, as when moving a brace to its own line.</summary>
    private static XamlTypingEdit? SnapOpeningBrace(string text, int lineStart, int caret, string before)
    {
        if (before.Trim().Length != 0 || lineStart == 0) return null;
        var previous = PreviousNonBlankLine(text, lineStart);
        if (previous == null || !IsBracelessHeader(previous.Trim())) return null;
        var target = LeadingWhitespace(previous);
        if (target == before) return null;
        return new(new(lineStart, caret - lineStart, target + "{"), lineStart + target.Length + 1);
    }

    /// <summary>A <c>}</c> typed on a blank line lines up with the line that opened its block.</summary>
    private static XamlTypingEdit? SnapClosingBrace(string text, int lineStart, int caret, string before, ScanResult scan)
    {
        if (before.Trim().Length != 0 || scan.OpenBraces.Count == 0) return null;
        var target = LeadingWhitespace(text[LineStart(text, scan.OpenBraces[^1])..]);
        if (target == before) return null;
        return new(new(lineStart, caret - lineStart, target + "}"), lineStart + target.Length + 1);
    }

    /// <summary><c>if (ready)</c>, <c>foreach (var x in xs)</c> or <c>else</c> with nothing after it: the next line is its body.</summary>
    internal static bool IsBracelessHeader(string line)
    {
        if (line.EndsWith(';') || line.EndsWith('{') || line.EndsWith('}') || line.Contains("//")) return false;
        if (line == "else") return true;
        var keyword = BracelessHeaders.FirstOrDefault(word => line.StartsWith(word, StringComparison.Ordinal) && line.Length > word.Length && (line[word.Length] == ' ' || line[word.Length] == '('));
        if (keyword == null && line.StartsWith("else if", StringComparison.Ordinal)) keyword = "else if";
        if (keyword == null || !line.EndsWith(')')) return false;
        // The parentheses opened after the keyword must close at the end of the line, so `if (a) Run(b)` is not a header.
        int depth = 0;
        for (int i = line.IndexOf('('); i >= 0 && i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            else if (line[i] == ')' && --depth == 0) return i == line.Length - 1;
        }
        return false;
    }

    private sealed record ScanResult(bool InCode, List<int> OpenBraces);

    /// <summary>Unclosed <c>{</c> offsets before <paramref name="end"/>, skipping strings, characters and comments.</summary>
    private static ScanResult Scan(string text, int end)
    {
        var open = new List<int>();
        int i = 0;
        while (i < end)
        {
            char c = text[i], next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/') { i = LineEnd(text, i); if (i >= end) return new(false, open); continue; }
            if (c == '/' && next == '*') { var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (close < 0 || close + 2 > end) return new(false, open); i = close + 2; continue; }
            if (c == '"' || ((c is '@' or '$') && (next == '"' || (next is '@' or '$' && i + 2 < text.Length && text[i + 2] == '"'))))
            {
                var stringEnd = SkipString(text, i);
                if (stringEnd > end) return new(false, open);
                i = stringEnd; continue;
            }
            if (c == '\'')
            {
                int j = i + 1;
                while (j < text.Length && text[j] != '\'' && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                if (j + 1 > end) return new(false, open);
                i = j + 1; continue;
            }
            if (c == '{') open.Add(i);
            else if (c == '}' && open.Count > 0) open.RemoveAt(open.Count - 1);
            i++;
        }
        return new(true, open);
    }

    /// <summary>
    /// The offset just after the string literal starting at <paramref name="start"/>: regular, verbatim, interpolated or raw.
    /// An unterminated literal returns one past the end of the text, so a caret at the end of the file still counts as inside it.
    /// </summary>
    private static int SkipString(string text, int start)
    {
        int i = start; bool verbatim = false;
        while (i < text.Length && text[i] is '@' or '$') { verbatim |= text[i] == '@'; i++; }
        int quotes = 0;
        while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
        if (quotes >= 3)
        {
            var close = text.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
            return close < 0 ? text.Length + 1 : close + quotes;
        }
        i++;
        while (i < text.Length)
        {
            char c = text[i];
            if (verbatim) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; } return i + 1; } }
            else if (c == '\\') { i += 2; continue; }
            else if (c == '"' || c == '\n') return i + 1;
            i++;
        }
        return text.Length + 1;
    }

    private static string? PreviousNonBlankLine(string text, int lineStart)
    {
        int end = lineStart;
        while (end > 0)
        {
            int lineEnd = end - 1;
            if (lineEnd > 0 && text[lineEnd] == '\n' && text[lineEnd - 1] == '\r') lineEnd--;
            int start = LineStart(text, lineEnd);
            var line = text[start..lineEnd];
            if (line.Trim().Length > 0) return line;
            end = start;
        }
        return null;
    }
    private static int LineStart(string text, int offset) { while (offset > 0 && text[offset - 1] is not ('\n' or '\r')) offset--; return offset; }
    private static int LineEnd(string text, int offset) { while (offset < text.Length && text[offset] is not ('\n' or '\r')) offset++; return offset; }
    private static string LeadingWhitespace(string line) { int i = 0; while (i < line.Length && line[i] is ' ' or '\t') i++; return line[..i]; }
}
