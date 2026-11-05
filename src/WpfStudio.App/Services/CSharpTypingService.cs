namespace WpfStudio.App.Services;

/// <summary>
/// What typing one character in C# does beyond inserting it, planned from the text alone.
/// <para>
/// Enter between braces opens an indented line between them; after a brace, a braceless <c>if</c>/<c>for</c> header, a
/// <c>case</c> label or a line that ends in an operator it indents one level; after a statement that began on an earlier
/// line it returns to where that statement began; and inside a <c>///</c> comment it continues the comment. A <c>{</c> or
/// <c>}</c> typed on an otherwise blank line snaps to the statement or block it belongs to.
/// </para>
/// <para>
/// With pairing on, an opening bracket or quote also types its closing one, typing that closing one moves past it, an
/// opener typed over a selection wraps it, and Backspace between an empty pair removes both.
/// </para>
/// Returns null to leave the keystroke to the editor's default handling.
/// </summary>
internal static class CSharpTypingService
{
    internal const int MaximumCharacters = 1_000_000;
    private static readonly string[] BracelessHeaders = ["if", "else", "for", "foreach", "while", "using", "lock", "fixed"];
    private static readonly string[] ContinuationEndings = ["=>", "=", "(", "[", "&&", "||", "??", "+"];

    public static XamlTypingEdit? GetEdit(string text, int caret, int selectionLength, string input, string indentation = "    ", string newLine = "\r\n", bool closePairs = true)
    {
        if (text.Length > MaximumCharacters || caret < 0 || caret > text.Length || selectionLength != 0) return null;
        bool enter = input is "\n" or "\r" or "\r\n";
        if (!enter && input is not ("{" or "}" or "(" or ")" or "[" or "]" or "\"" or "'")) return null;
        var scan = Scan(text, caret);
        int lineStart = LineStart(text, caret), lineEnd = LineEnd(text, caret);
        var before = text[lineStart..caret];
        var after = text[caret..lineEnd];
        char next = caret < text.Length ? text[caret] : '\0';
        char previous = caret > 0 ? text[caret - 1] : '\0';
        if (enter)
            return scan.Context switch
            {
                Context.Code => Enter(text, lineStart, caret, before, after, LeadingWhitespace(before), indentation, newLine, scan),
                Context.Comment => ContinueDocumentation(lineStart, caret, before, newLine),
                _ => null
            };
        if (input is "\"" or "'") return closePairs ? Quote(text, caret, input[0], scan, previous, next) : null;
        if (!scan.InCode) return null;
        return input switch
        {
            "{" => SnapOpeningBrace(text, lineStart, caret, before, closePairs && after.Trim().Length == 0) ?? (closePairs ? Open(caret, '{', '}', next) : null),
            "}" => SnapClosingBrace(text, lineStart, caret, before, scan) ?? (closePairs ? Close(text, lineStart, lineEnd, caret, '{', '}', next) : null),
            "(" => closePairs ? Open(caret, '(', ')', next) : null,
            "[" => closePairs ? Open(caret, '[', ']', next) : null,
            ")" => closePairs ? Close(text, lineStart, lineEnd, caret, '(', ')', next) : null,
            "]" => closePairs ? Close(text, lineStart, lineEnd, caret, '[', ']', next) : null,
            _ => null
        };
    }

    /// <summary>An opening bracket or quote typed over a selection in code wraps it instead of replacing it.</summary>
    public static XamlTypingEdit? Surround(string text, int start, int length, string input)
    {
        if (text.Length > MaximumCharacters || start < 0 || length <= 0 || start + length > text.Length) return null;
        string? close = input switch { "(" => ")", "[" => "]", "{" => "}", "\"" => "\"", "'" => "'", _ => null };
        if (close is null || !Scan(text, start).InCode) return null;
        var wrapped = string.Concat(input, text.AsSpan(start, length), close);
        return new(new(start, length, wrapped), start + wrapped.Length);
    }

    /// <summary>Backspace between a pair with nothing in it, such as <c>(|)</c> or <c>"|"</c>, removes both characters.</summary>
    public static XamlTypingEdit? Backspace(string text, int caret)
    {
        if (text.Length > MaximumCharacters || caret <= 0 || caret >= text.Length) return null;
        char previous = text[caret - 1], next = text[caret];
        bool brackets = (previous, next) is ('(', ')') or ('[', ']') or ('{', '}');
        bool quotes = previous == next && previous is '"' or '\'';
        if (!brackets && !quotes) return null;
        // The opener must be code and, for a quote, must be what opened the literal the caret is in: "a"|"b" is two literals, not a pair.
        if (!Scan(text, caret - 1).InCode) return null;
        var inside = Scan(text, caret).Context;
        if (brackets ? inside != Context.Code : inside != (previous == '"' ? Context.String : Context.Character)) return null;
        return new(new(caret - 1, 2, ""), caret - 1);
    }

    /// <summary>The offsets of the braces still open at <paramref name="offset"/>, outermost first; empty inside a string or comment.</summary>
    internal static IReadOnlyList<int> OpenBraces(string text, int offset) =>
        text.Length > MaximumCharacters || offset < 0 || offset > text.Length ? [] : Scan(text, offset) is { InCode: true } scan ? scan.OpenBraces : [];

    private static XamlTypingEdit? Enter(string text, int lineStart, int caret, string before, string after, string indent, string unit, string newLine, ScanResult scan)
    {
        var beforeTrim = before.TrimEnd();
        var afterTrim = after.TrimStart();
        if (beforeTrim.Trim().Length == 0) return null;
        int start = lineStart + beforeTrim.Length, end = caret + (after.Length - afterTrim.Length);
        XamlTypingEdit NewLine(string indentation) => new(new(start, end - start, newLine + indentation), start + newLine.Length + indentation.Length);
        if (beforeTrim.EndsWith('{'))
        {
            var inner = indent + unit;
            // {|} → the closing brace moves to its own line under the opening line, with the caret on an indented line between.
            if (afterTrim.StartsWith('}')) return new(new(start, end - start, newLine + inner + newLine + indent), start + newLine.Length + inner.Length);
            return NewLine(inner);
        }
        if (afterTrim.StartsWith('}') && scan.OpenBraces.Count > 0) return NewLine(LeadingWhitespace(text[LineStart(text, scan.OpenBraces[^1])..]));
        var code = beforeTrim.TrimStart();
        bool restIsClosers = afterTrim.All(c => c is ')' or ']' or ';');
        if (afterTrim.Length == 0 && (IsBracelessHeader(code) || IsCaseLabel(code))) return NewLine(indent + unit);
        // The line stops at an operator or an opening bracket, so the statement goes on: its first continuation line is indented.
        if (restIsClosers && scan.StatementStart >= lineStart && ContinuationEndings.Any(ending => code.EndsWith(ending, StringComparison.Ordinal)))
            return NewLine(indent + unit);
        // A statement ended here that began on an earlier line: a braceless body, a wrapped call or a chain. The next one lines up with where it began.
        if (afterTrim.Length == 0 && code.EndsWith(';') && scan.CompletedStatementStart >= 0 && scan.CompletedStatementStart < lineStart)
        {
            var target = LeadingWhitespace(text[LineStart(text, scan.CompletedStatementStart)..]);
            if (target != indent) return NewLine(target);
        }
        return null;
    }

    /// <summary>Enter inside a <c>///</c> comment starts the next line as one, with what followed the caret moved onto it.</summary>
    private static XamlTypingEdit? ContinueDocumentation(int lineStart, int caret, string before, string newLine)
    {
        var indent = LeadingWhitespace(before);
        if (!before.AsSpan(indent.Length).StartsWith("///") || before.AsSpan(indent.Length).StartsWith("////")) return null;
        var prefix = newLine + indent + "/// ";
        return new(new(caret, 0, prefix), caret + prefix.Length);
    }

    private static XamlTypingEdit? Open(int caret, char open, char close, char next) =>
        next == '\0' || char.IsWhiteSpace(next) || next is ')' or ']' or '}' or ';' or ','
            ? new(new(caret, 0, new string([open, close])), caret + 1) : null;

    /// <summary>
    /// Typing a closing bracket that is already there moves past it, when the line's brackets balance. When one is
    /// missing, as in <c>Run(Read(x|)</c>, the typed one is the missing one and is inserted.
    /// </summary>
    private static XamlTypingEdit? Close(string text, int lineStart, int lineEnd, int caret, char open, char close, char next)
    {
        if (next != close) return null;
        int opens = 0, closes = 0;
        for (int i = lineStart; i < lineEnd; i++)
        {
            if (text[i] == open) opens++;
            else if (text[i] == close) closes++;
        }
        return closes >= opens ? new(new(caret, 0, ""), caret + 1) : null;
    }

    private static XamlTypingEdit? Quote(string text, int caret, char quote, ScanResult scan, char previous, char next)
    {
        // Inside the literal, its own closing quote directly ahead: typing it moves past it, unless a backslash makes it an escaped one.
        if (scan.Context == (quote == '"' ? Context.String : Context.Character))
        {
            bool escapes = scan.LiteralStart < 0 || !(text[scan.LiteralStart] == '@' || text.AsSpan(scan.LiteralStart, Math.Min(2, text.Length - scan.LiteralStart)).SequenceEqual("$@"));
            return next == quote && !(escapes && previous == '\\') ? new(new(caret, 0, ""), caret + 1) : null;
        }
        if (!scan.InCode) return null;
        if (!(next == '\0' || char.IsWhiteSpace(next) || next is ')' or ']' or '}' or ';' or ',')) return null;
        // After a name or another quote the typed one closes or continues something: the third quote of """, or a stray one.
        if (char.IsLetterOrDigit(previous) || previous is '_' or '"' or '\'' or '\\') return null;
        return new(new(caret, 0, new string(quote, 2)), caret + 1);
    }

    /// <summary>A <c>{</c> typed on the indented line after <c>if (x)</c> lines up with the <c>if</c>, as when moving a brace to its own line.</summary>
    private static XamlTypingEdit? SnapOpeningBrace(string text, int lineStart, int caret, string before, bool close)
    {
        if (before.Trim().Length != 0 || lineStart == 0) return null;
        var previous = PreviousNonBlankLine(text, lineStart);
        if (previous == null || !IsBracelessHeader(previous.Trim())) return null;
        var target = LeadingWhitespace(previous);
        if (target == before) return null;
        return new(new(lineStart, caret - lineStart, target + (close ? "{}" : "{")), lineStart + target.Length + 1);
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

    /// <summary><c>case 1:</c> or <c>default:</c> with nothing after it: the next line is the section's first statement.</summary>
    private static bool IsCaseLabel(string line) =>
        line.EndsWith(':') && !line.EndsWith("::", StringComparison.Ordinal) && (line.StartsWith("case ", StringComparison.Ordinal) || line == "default:");

    private enum Context { Code, String, Character, Comment }

    /// <summary>
    /// What the scan knows at its end. <see cref="StatementStart"/> is where the unfinished statement there began, or -1
    /// between statements; <see cref="CompletedStatementStart"/> is where the statement that has just ended began.
    /// </summary>
    private sealed record ScanResult(Context Context, List<int> OpenBraces, int StatementStart = -1, int CompletedStatementStart = -1)
    {
        public bool InCode => Context == Context.Code;
        /// <summary>Where the string or character literal the scan ended in began, prefix included.</summary>
        public int LiteralStart { get; init; } = -1;
    }

    /// <summary>Unclosed <c>{</c> offsets before <paramref name="end"/> and the statement in progress there, skipping strings, characters and comments.</summary>
    private static ScanResult Scan(string text, int end)
    {
        var open = new List<int>();
        // A block starts its own statements; what encloses it resumes when the block closes.
        var enclosing = new Stack<(int Brackets, int Statement)>();
        int i = 0, brackets = 0, statement = -1, completed = -1;
        while (i < end)
        {
            char c = text[i], next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/') { i = LineEnd(text, i); if (i >= end) return new(Context.Comment, open); continue; }
            if (c == '/' && next == '*') { var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (close < 0 || close + 2 > end) return new(Context.Comment, open); i = close + 2; continue; }
            if (c == '"' || ((c is '@' or '$') && (next == '"' || (next is '@' or '$' && i + 2 < text.Length && text[i + 2] == '"'))))
            {
                var stringEnd = SkipString(text, i);
                if (stringEnd > end) return new(Context.String, open) { LiteralStart = i };
                if (statement < 0) statement = i;
                i = stringEnd; continue;
            }
            if (c == '\'')
            {
                int j = i + 1;
                while (j < text.Length && text[j] != '\'' && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                if (j + 1 > end) return new(Context.Character, open) { LiteralStart = i };
                if (statement < 0) statement = i;
                i = j + 1; continue;
            }
            // A preprocessor line is not part of any statement.
            if (c == '#' && text.AsSpan(LineStart(text, i), i - LineStart(text, i)).Trim().IsEmpty) { i = LineEnd(text, i); continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '{':
                    open.Add(i);
                    enclosing.Push((brackets, statement));
                    brackets = 0; statement = -1; completed = -1;
                    break;
                case '}':
                    if (open.Count > 0) open.RemoveAt(open.Count - 1);
                    // A block inside brackets, such as a lambda's, is part of the statement around it. Any other ends one.
                    if (enclosing.TryPop(out var outer) && outer.Brackets > 0) (brackets, statement) = outer;
                    else { brackets = 0; statement = -1; }
                    completed = -1;
                    break;
                case '(' or '[':
                    if (statement < 0) statement = i;
                    brackets++;
                    break;
                case ')' or ']':
                    if (brackets > 0) brackets--;
                    break;
                case ';' when brackets == 0:
                    completed = statement;
                    statement = -1;
                    break;
                case ':' when brackets == 0 && statement >= 0 && next != ':' && IsCaseLabel(text[statement..(i + 1)]):
                    statement = -1;
                    break;
                default:
                    if (statement < 0) { statement = i; completed = -1; }
                    break;
            }
            i++;
        }
        return new(Context.Code, open, statement, statement < 0 ? completed : -1);
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
