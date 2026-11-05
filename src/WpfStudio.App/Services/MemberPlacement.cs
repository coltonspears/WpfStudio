using System.Text.RegularExpressions;

namespace WpfStudio.App.Services;

/// <summary>
/// Where a new member goes when one is inserted from a button rather than typed. <see cref="Start"/> and
/// <see cref="Length"/> are replaced by <see cref="Before"/> and <see cref="After"/>, and the member is written between the two.
/// </summary>
internal sealed record MemberPlace(int Start, int Length, string Before, string After);

internal static partial class MemberPlacement
{
    private const int ClosingBraceCandidates = 400;

    /// <summary>
    /// A blank line takes the member where it is, at the indentation of the type's members. A line with code keeps its
    /// code: the member goes after the member the caret is in, set off by a blank line, so a click with the caret in the
    /// middle of a method does not write a property into that method.
    /// </summary>
    public static MemberPlace Find(string text, int caret, string unit, string newLine)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int lineStart = LineStart(text, caret), lineEnd = LineEnd(text, caret);
        var braces = CSharpTypingService.OpenBraces(text, lineStart);
        var opened = CSharpTypingService.OpenBraces(text, lineEnd);
        // The line that opens the type itself: the member goes first in its body.
        if (opened.Count > braces.Count && TypeBrace(text, opened) == opened.Count - 1)
            return new(lineEnd, 0, newLine + LeadingWhitespace(text, LineStart(text, opened[^1])) + unit, IsCrowded(text, lineEnd) ? newLine : "");
        int type = TypeBrace(text, braces);
        string memberIndent = type >= 0 ? LeadingWhitespace(text, LineStart(text, braces[type])) + unit : LeadingWhitespace(text, lineStart);
        if (IsBlank(text, lineStart, lineEnd))
        {
            // Directly inside the type the blank line is where the member belongs. Deeper, it is a blank line in some method's body.
            if (type < 0 || braces.Count == type + 1) return new(lineStart, lineEnd - lineStart, memberIndent, "");
        }
        int anchor = type >= 0 ? MemberEnd(text, lineStart, lineEnd, type) : lineEnd;
        return new(anchor, 0, newLine + newLine + memberIndent, IsCrowded(text, anchor) ? newLine : "");
    }

    /// <summary>The text with <paramref name="member"/> written at a place. Its lines are given without indentation and take the place's.</summary>
    public static string Apply(string text, MemberPlace place, string member, string newLine)
    {
        var indent = place.Before[(place.Before.LastIndexOfAny(['\r', '\n']) + 1)..];
        var lines = member.Replace("\r\n", "\n").Trim('\n').Split('\n');
        return text.Remove(place.Start, place.Length).Insert(place.Start, place.Before + string.Join(newLine + indent, lines) + place.After);
    }

    // A member wants a blank line after it, unless what follows is already blank or closes the type.
    private static bool IsCrowded(string text, int lineEnd)
    {
        int following = NextLineStart(text, lineEnd);
        return following < text.Length && !IsBlank(text, following, LineEnd(text, following))
            && text.AsSpan(following, LineEnd(text, following) - following).TrimStart()[0] != '}';
    }

    /// <summary>
    /// The using directive a member needs, placed after the file's last using, unless the file already has it or already
    /// uses that namespace's types by their short names, which shows the namespace is in scope some other way.
    /// </summary>
    public static (int Offset, string Text)? UsingInsertion(string text, string @namespace, string newLine)
    {
        if (Regex.IsMatch(text, @"^\s*(global\s+)?using\s+" + Regex.Escape(@namespace) + @"\s*;", RegexOptions.Multiline)) return null;
        string[] evidence = @namespace switch
        {
            "CommunityToolkit.Mvvm.ComponentModel" => ["[ObservableProperty", "ObservableObject", "ObservableRecipient", "ObservableValidator"],
            "CommunityToolkit.Mvvm.Input" => ["[RelayCommand", "IRelayCommand", "RelayCommand<"],
            _ => []
        };
        if (evidence.Any(marker => text.Contains(marker, StringComparison.Ordinal))) return null;
        // Directives come before the first type. A using further down is a statement inside a method.
        int firstType = TypeHeader().Match(text) is { Success: true } header ? header.Index : text.Length;
        var last = UsingDirective().Matches(text).LastOrDefault(directive => directive.Index < firstType);
        if (last is null) return (0, "using " + @namespace + ";" + newLine + newLine);
        return (NextLineStart(text, last.Index + last.Length - 1), "using " + @namespace + ";" + newLine);
    }

    /// <summary>The end of the line on which the member the caret's line belongs to ends.</summary>
    private static int MemberEnd(string text, int lineStart, int lineEnd, int type)
    {
        // The line is in a member's body, or opens one: the member ends where that body closes.
        var open = CSharpTypingService.OpenBraces(text, lineEnd);
        if (open.Count > type + 1) return ClosingBrace(text, open[type + 1]) is { } close ? LineEnd(text, close) : lineEnd;
        var line = text.AsSpan(lineStart, lineEnd - lineStart).Trim();
        if (line.EndsWith(";") || line.EndsWith("}") || line.StartsWith("//")) return lineEnd;
        // An attribute, a header or the first line of a wrapped declaration: the member ends at its semicolon or at the end of its body.
        int search = lineEnd;
        for (int looked = 0; looked < 50 && search < text.Length; looked++)
        {
            int found = text.AsSpan(search).IndexOfAny(';', '{');
            if (found < 0) break;
            int at = search + found;
            // In code directly inside the type, as opposed to inside a string or a comment.
            if (CSharpTypingService.OpenBraces(text, at).Count == type + 1)
                return text[at] == ';' ? LineEnd(text, at) : ClosingBrace(text, at) is { } close ? LineEnd(text, close) : lineEnd;
            search = at + 1;
        }
        return lineEnd;
    }

    /// <summary>The index, in <paramref name="braces"/>, of the innermost brace that opens a type's body; -1 outside any type.</summary>
    private static int TypeBrace(string text, IReadOnlyList<int> braces)
    {
        for (int index = braces.Count - 1; index >= 0; index--)
        {
            // What precedes the brace back to the end of the previous declaration or statement is its header.
            int start = braces[index];
            while (start > 0 && text[start - 1] is not (';' or '{' or '}')) start--;
            if (TypeHeader().IsMatch(text.AsSpan(start, braces[index] - start))) return index;
        }
        return -1;
    }

    /// <summary>The brace that closes the one at <paramref name="open"/>, found by asking which later brace leaves it closed.</summary>
    private static int? ClosingBrace(string text, int open)
    {
        int candidates = 0;
        for (int at = text.IndexOf('}', open + 1); at >= 0 && candidates < ClosingBraceCandidates; at = text.IndexOf('}', at + 1), candidates++)
        {
            // Inside a string or comment the answer is empty, which is not "closed": keep looking past it.
            if (at + 1 <= text.Length && CSharpTypingService.OpenBraces(text, at) is { Count: > 0 } before && before.Contains(open)
                && !CSharpTypingService.OpenBraces(text, at + 1).Contains(open)) return at;
        }
        return null;
    }

    private static int NextLineStart(string text, int offset)
    {
        int end = LineEnd(text, offset);
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        return end;
    }
    private static bool IsBlank(string text, int start, int end)
    {
        for (int i = start; i < end; i++) if (text[i] is not (' ' or '\t')) return false;
        return true;
    }
    private static string LeadingWhitespace(string text, int lineStart)
    {
        int end = lineStart;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return text[lineStart..end];
    }
    private static int LineStart(string text, int offset) { while (offset > 0 && text[offset - 1] is not ('\n' or '\r')) offset--; return offset; }
    private static int LineEnd(string text, int offset) { while (offset < text.Length && text[offset] is not ('\n' or '\r')) offset++; return offset; }

    // A keyword followed by a name, so the constraint in "where T : class" does not make a method look like a type.
    [GeneratedRegex(@"\b(class|struct|record|interface)\s+[A-Za-z_@]")]
    private static partial Regex TypeHeader();

    [GeneratedRegex(@"^[ \t]*using\s+(?!\()[^;=\r\n]*(=[^;\r\n]*)?;", RegexOptions.Multiline)]
    private static partial Regex UsingDirective();
}
