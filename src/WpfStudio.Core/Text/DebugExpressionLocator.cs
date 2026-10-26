namespace WpfStudio.Core.Text;

/// <summary>
/// Finds the C# expression a debugger DataTip should evaluate under the mouse: the hovered identifier together
/// with the member-access chain to its left, such as <c>this.Customer.Name</c>. Invocations, indexers, comments
/// and strings are excluded, so hovering never runs a method.
/// </summary>
public static class DebugExpressionLocator
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float",
        "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "partial", "private", "protected", "public", "readonly", "record", "ref",
        "return", "sbyte", "sealed", "set", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "value", "var", "virtual", "void", "volatile", "when", "where",
        "while", "yield"
    };

    public readonly record struct Match(int Start, int Length, string Expression);

    public static Match? Find(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return null;
        // Hovering the last character of a word reports the offset after it.
        if (offset == text.Length || !IsIdentifierPart(text[offset])) offset--;
        if (offset < 0 || !IsIdentifierPart(text[offset])) return null;
        var start = offset;
        while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
        var verbatim = text[start] == '@';
        var end = offset + 1;
        while (end < text.Length && IsIdentifierPart(text[end])) end++;
        if (start + (verbatim ? 1 : 0) >= end || char.IsDigit(text[start])) return null;
        var word = text[start..end];
        if (!verbatim && word != "this" && Keywords.Contains(word)) return null;
        if (InCommentOrString(text, start)) return null;

        // Walk left across "." and "?." to include the receivers.
        var chainStart = start;
        while (true)
        {
            var dot = chainStart - 1;
            while (dot >= 0 && text[dot] is ' ' or '\t') dot--;
            if (dot < 0 || text[dot] != '.') break;
            var receiverEnd = dot;
            if (receiverEnd > 0 && text[receiverEnd - 1] == '?') receiverEnd--;
            while (receiverEnd > 0 && text[receiverEnd - 1] is ' ' or '\t' or '\r' or '\n') receiverEnd--;
            var receiverStart = receiverEnd;
            while (receiverStart > 0 && IsIdentifierPart(text[receiverStart - 1])) receiverStart--;
            if (receiverStart == receiverEnd || char.IsDigit(text[receiverStart])) break; // e.g. a call result: Load().Name
            var receiver = text[receiverStart..receiverEnd];
            if (receiver != "this" && receiver != "base" && Keywords.Contains(receiver)) break;
            chainStart = receiverStart;
        }
        var expression = string.Concat(text[chainStart..end].Where(c => !char.IsWhiteSpace(c)));
        // Base access cannot be evaluated alone; "base.Name" evaluates as "this.Name" for non-virtual members.
        if (expression == "base") return null;
        return new(chainStart, end - chainStart, expression);
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '@';

    /// <summary>A single-line approximation: line comments and string or character literals before the word.</summary>
    private static bool InCommentOrString(string text, int position)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        if (position == 0) lineStart = 0;
        var trimmed = text.AsSpan(lineStart, position - lineStart).TrimStart();
        if (trimmed.StartsWith("*") || trimmed.StartsWith("/*")) return true;
        bool inString = false, verbatim = false, inChar = false;
        for (var i = lineStart; i < position; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (verbatim) { if (c == '"' && (i + 1 >= position || text[i + 1] != '"')) inString = false; else if (c == '"') i++; }
                else if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (inChar) { if (c == '\\') i++; else if (c == '\'') inChar = false; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') return true;
            if (c == '"') { inString = true; verbatim = i > lineStart && text[i - 1] == '@'; }
            else if (c == '\'') inChar = true;
        }
        return inString || inChar;
    }
}
