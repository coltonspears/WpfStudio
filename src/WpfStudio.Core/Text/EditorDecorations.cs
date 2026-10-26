namespace WpfStudio.Core.Text;

/// <summary>
/// Moves decorations computed for an earlier version of a document through a later text change, so semantic
/// colours and inline hints stay in place while the language service catches up. Decorations the change touches
/// are dropped rather than guessed.
/// </summary>
public static class DecorationOffsets
{
    /// <summary>The range after replacing <paramref name="removed"/> characters at <paramref name="offset"/> with <paramref name="inserted"/> characters.</summary>
    public static (int Start, int Length)? MapRange(int start, int length, int offset, int removed, int inserted)
    {
        if (start + length <= offset) return (start, length);
        if (start >= offset + removed) return (start + inserted - removed, length);
        return null;
    }

    /// <summary>A point keeps to the text after it: an insertion exactly at the point moves it along.</summary>
    public static int? MapPoint(int position, int offset, int removed, int inserted)
    {
        if (position < offset) return position;
        if (position >= offset + removed) return position + inserted - removed;
        return null;
    }
}

/// <summary>A collapsible region: <see cref="Start"/> and <see cref="End"/> are document offsets, <see cref="Title"/> its collapsed text.</summary>
public readonly record struct FoldRegion(int Start, int End, string Title);

/// <summary>
/// Lexical C# folding: multi-line brace blocks, #region/#endregion, using groups, multi-line comments and
/// documentation comments. Braces inside strings, characters and comments are ignored.
/// </summary>
public static class CSharpFolding
{
    public static IReadOnlyList<FoldRegion> Find(string text)
    {
        var regions = new List<FoldRegion>();
        var braces = new Stack<int>();
        var directives = new Stack<(int Start, string Name)>();
        int index = 0;
        int usingStart = -1, usingEnd = -1, usingCount = 0;
        int docStart = -1, docEnd = -1, docCount = 0; string docTitle = "";
        while (index < text.Length)
        {
            // Line-level constructs are recognized at the first non-blank character of a line.
            if (index == 0 || text[index - 1] == '\n')
            {
                var lineStart = index;
                var first = index;
                while (first < text.Length && text[first] is ' ' or '\t') first++;
                var lineEnd = text.IndexOf('\n', first); if (lineEnd < 0) lineEnd = text.Length;
                var contentEnd = lineEnd > first && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                var line = text.AsSpan(first, contentEnd - first);
                if (line.StartsWith("///"))
                {
                    if (docCount == 0) { docStart = first; docTitle = DocTitle(line.ToString()); }
                    else if (docTitle.Length == 0) docTitle = DocTitle(line.ToString());
                    docEnd = contentEnd; docCount++;
                    index = lineEnd; continue;
                }
                CloseGroup(regions, ref docStart, ref docEnd, ref docCount, "/// " + (docTitle.Length > 0 ? docTitle + " …" : "…"));
                if (line.StartsWith("using ") && !line.Contains('(') && line.EndsWith(";"))
                {
                    if (usingCount == 0) usingStart = first;
                    usingEnd = contentEnd; usingCount++;
                    index = lineEnd; continue;
                }
                if (line.Length > 0) CloseGroup(regions, ref usingStart, ref usingEnd, ref usingCount, "using …");
                if (line.StartsWith("#region"))
                {
                    directives.Push((first, line[7..].Trim().ToString()));
                    index = lineEnd; continue;
                }
                if (line.StartsWith("#endregion"))
                {
                    if (directives.TryPop(out var region)) regions.Add(new(region.Start, contentEnd, region.Name.Length > 0 ? region.Name : "#region"));
                    index = lineEnd; continue;
                }
                if (line.StartsWith("#")) { index = lineEnd; continue; } // Other directives contain no braces to match.
                index = first;
                if (index >= text.Length) break;
            }
            var c = text[index];
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/') { index = LineEnd(text, index); continue; }
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 2;
                if (text.AsSpan(index, end - index).Contains('\n')) regions.Add(new(index, end, "/* … */"));
                index = end; continue;
            }
            if (c == '"' || c == '\'') { index = SkipLiteral(text, index); continue; }
            if (c == '@' && index + 1 < text.Length && text[index + 1] == '"') { index = SkipVerbatim(text, index + 1); continue; }
            if (c == '$')
            {
                var quote = index + 1;
                while (quote < text.Length && text[quote] is '$' or '@') quote++;
                if (quote < text.Length && text[quote] == '"')
                { index = text[(index + 1)..quote].Contains('@') ? SkipVerbatim(text, quote) : SkipLiteral(text, quote); continue; }
            }
            if (c == '{') braces.Push(index);
            else if (c == '}' && braces.TryPop(out var open) && text.AsSpan(open, index - open).Contains('\n'))
                regions.Add(new(open, index + 1, "{ … }"));
            index++;
        }
        CloseGroup(regions, ref docStart, ref docEnd, ref docCount, "/// " + (docTitle.Length > 0 ? docTitle + " …" : "…"));
        CloseGroup(regions, ref usingStart, ref usingEnd, ref usingCount, "using …");
        regions.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        return regions;
    }

    private static void CloseGroup(List<FoldRegion> regions, ref int start, ref int end, ref int count, string title)
    {
        if (count >= 2) regions.Add(new(start, end, title));
        start = end = -1; count = 0;
    }

    private static string DocTitle(string line)
    {
        var content = line.TrimStart('/').Trim();
        content = System.Text.RegularExpressions.Regex.Replace(content, "<[^>]+>", "").Trim();
        return content.Length > 60 ? content[..60] : content;
    }

    private static int LineEnd(string text, int index)
    {
        var end = text.IndexOf('\n', index);
        return end < 0 ? text.Length : end;
    }

    // Raw string literals ("""…""") end at a run of at least as many quotes as opened them.
    private static int SkipLiteral(string text, int index)
    {
        var quote = text[index];
        if (quote == '"')
        {
            var run = 0;
            while (index + run < text.Length && text[index + run] == '"') run++;
            if (run >= 3)
            {
                var close = text.IndexOf(new string('"', run), index + run, StringComparison.Ordinal);
                return close < 0 ? text.Length : close + run;
            }
            if (run == 2) return index + 2; // An empty string.
        }
        for (var i = index + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == quote || text[i] == '\n') return i + 1;
        }
        return text.Length;
    }

    private static int SkipVerbatim(string text, int quote)
    {
        for (var i = quote + 1; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
            return i + 1;
        }
        return text.Length;
    }
}
