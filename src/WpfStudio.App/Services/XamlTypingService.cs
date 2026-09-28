using System.Globalization;
using System.Text;
using System.Xml;
using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

internal sealed record XamlTypingEdit(TextEdit Edit, int CaretOffset);

/// <summary>Pure lexical plans for one typed character; never rewrites pasted or selected text.</summary>
internal static class XamlTypingService
{
    internal const int MaximumCharacters = 1_000_000;
    private enum PendingKind { Name, Attributes, AttributeName, Value, QuotedValue, SelfClosing, Closing }
    private sealed record OpenTag(string Name, int Start, int End, bool PreserveSpace);
    private sealed record PendingTag(string Name, int Start, PendingKind Kind, char Quote = '\0', int ValueStart = 0);
    private sealed class ScanResult
    {
        public bool Valid { get; set; } = true;
        public bool Blocked { get; set; }
        public List<OpenTag> Open { get; } = [];
        public HashSet<int> Closed { get; } = [];
        public PendingTag? Pending { get; set; }
    }

    public static XamlTypingEdit? GetEdit(string text, int caret, int selectionLength, string input,
        string indentation = "    ", string? newLine = null)
    {
        if (text.Length > MaximumCharacters || caret < 0 || caret > text.Length || selectionLength != 0 ||
            input is not (">" or "/" or "\"" or "'" or "\n" or "\r" or "\r\n")) return null;
        var scan = Scan(text, caret);
        if (!scan.Valid || scan.Blocked) return null;
        char next = caret < text.Length ? text[caret] : '\0';
        if (input is "\"" or "'")
        {
            char quote = input[0];
            if (scan.Pending is { Kind: PendingKind.QuotedValue } value && value.Quote == quote && next == quote &&
                !InsideMarkupExtension(text.AsSpan(value.ValueStart, caret - value.ValueStart)))
                return new(new(caret, 0, ""), caret + 1);
            if (scan.Pending?.Kind != PendingKind.Value) return null;
            if (next == quote) return new(new(caret, 0, ""), caret + 1);
            if (next != '\0' && !char.IsWhiteSpace(next) && next is not ('>' or '/')) return null;
            return new(new(caret, 0, new string(quote, 2)), caret + 1);
        }
        if (input == "/")
        {
            if (scan.Pending is not { Kind: PendingKind.Name, Name.Length: 0 } pending ||
                caret != pending.Start + 1 || scan.Open.Count == 0 || next != '\0' && !char.IsWhiteSpace(next) && next != '>') return null;
            string closing = "/" + scan.Open[^1].Name;
            return next == '>' ? new(new(caret, 0, closing), caret + closing.Length + 1)
                : new(new(caret, 0, closing + ">"), caret + closing.Length + 1);
        }
        if (input == ">")
        {
            if (scan.Pending is not { Name.Length: > 0 } pending ||
                pending.Kind is not (PendingKind.Name or PendingKind.Attributes) || next == '/' || next != '\0' && NameCharacter(next)) return null;
            if (next == '>') return new(new(caret, 0, ""), caret + 1);
            // Test both interpretations of a later close tag. If the suffix is
            // already valid without this unfinished opener, its close belongs to
            // an ancestor (especially important for nested, identically named tags).
            var withOpening = Scan(text.Insert(caret, ">"), text.Length + 1);
            var withoutOpeningText = text.Remove(pending.Start, caret - pending.Start);
            var withoutOpening = Scan(withoutOpeningText, withoutOpeningText.Length);
            if (!withOpening.Valid && !withoutOpening.Valid) return null;
            if (withOpening.Valid && withOpening.Closed.Contains(pending.Start) && !withoutOpening.Valid) return null;
            return new(new(caret, 0, "></" + pending.Name + ">"), caret + 1);
        }
        if (scan.Pending is not null || scan.Open.Count == 0 || scan.Open[^1].PreserveSpace) return null;
        var open = scan.Open[^1];
        if (!HorizontalWhitespace(text.AsSpan(open.End, caret - open.End))) return null;
        int closingStart = caret;
        while (closingStart < text.Length && text[closingStart] is ' ' or '\t') closingStart++;
        if (!MatchesClosingTag(text, closingStart, open.Name)) return null;
        newLine ??= DetectNewLine(text);
        if (newLine is not ("\n" or "\r" or "\r\n") || indentation.Length > 64 || !HorizontalWhitespace(indentation)) return null;
        int lineStart = open.Start;
        while (lineStart > 0 && text[lineStart - 1] is not ('\n' or '\r')) lineStart--;
        int indentEnd = lineStart;
        while (indentEnd < open.Start && text[indentEnd] is ' ' or '\t') indentEnd++;
        if (indentEnd - lineStart > 256) return null;
        string outer = text[lineStart..indentEnd], inner = outer + indentation;
        return new(new(caret, closingStart - caret, newLine + inner + newLine + outer), caret + newLine.Length + inner.Length);
    }

    private static ScanResult Scan(string text, int limit)
    {
        var result = new ScanResult();
        int elements = 0;
        for (int i = 0; i < limit;)
        {
            if (text[i] != '<') { i++; continue; }
            int start = i;
            string? terminator = text.AsSpan(i, limit - i).StartsWith("<!--") ? "-->" :
                text.AsSpan(i, limit - i).StartsWith("<![CDATA[") ? "]]>" : text.AsSpan(i, limit - i).StartsWith("<?") ? "?>" : null;
            if (terminator is not null)
            {
                int end = text.IndexOf(terminator, i + 2, StringComparison.Ordinal);
                if (end < 0 || end + terminator.Length > limit) { result.Blocked = true; return result; }
                i = end + terminator.Length; continue;
            }
            i++;
            if (i == limit) { result.Pending = new("", start, PendingKind.Name); return result; }
            if (text[i] == '!') { result.Valid = false; return result; }
            bool closing = text[i] == '/';
            if (closing) i++;
            int nameStart = i;
            while (i < limit && NameCharacter(text[i])) i++;
            string name = text[nameStart..i];
            if (name.Length == 0 || !QualifiedName(name)) { result.Valid = false; return result; }
            if (i == limit) { result.Pending = new(name, start, closing ? PendingKind.Closing : PendingKind.Name); return result; }
            bool? preserve = null;
            bool requireWhitespace = true;
            while (true)
            {
                int beforeSpace = i;
                while (i < limit && char.IsWhiteSpace(text[i])) i++;
                if (i == limit) { result.Pending = new(name, start, closing ? PendingKind.Closing : PendingKind.Attributes); return result; }
                if (text[i] == '>')
                {
                    i++;
                    if (closing)
                    {
                        if (result.Open.Count == 0 || result.Open[^1].Name != name) { result.Valid = false; return result; }
                        result.Closed.Add(result.Open[^1].Start); result.Open.RemoveAt(result.Open.Count - 1);
                    }
                    else
                    {
                        if (++elements > 8192 || result.Open.Count >= 256) { result.Valid = false; return result; }
                        result.Open.Add(new(name, start, i, preserve ?? (result.Open.Count > 0 && result.Open[^1].PreserveSpace)));
                    }
                    break;
                }
                if (closing) { result.Valid = false; return result; }
                if (text[i] == '/')
                {
                    if (++i == limit) { result.Pending = new(name, start, PendingKind.SelfClosing); return result; }
                    if (text[i] != '>') { result.Valid = false; return result; }
                    if (++elements > 8192) { result.Valid = false; return result; }
                    i++; break;
                }
                if (requireWhitespace && i == beforeSpace) { result.Valid = false; return result; }
                int attributeStart = i;
                while (i < limit && NameCharacter(text[i])) i++;
                string attribute = text[attributeStart..i];
                if (!QualifiedName(attribute)) { result.Valid = false; return result; }
                while (i < limit && char.IsWhiteSpace(text[i])) i++;
                if (i == limit) { result.Pending = new(name, start, PendingKind.AttributeName); return result; }
                if (text[i++] != '=') { result.Valid = false; return result; }
                while (i < limit && char.IsWhiteSpace(text[i])) i++;
                if (i == limit) { result.Pending = new(name, start, PendingKind.Value); return result; }
                char quote = text[i++];
                if (quote is not ('\'' or '"')) { result.Valid = false; return result; }
                int valueStart = i;
                while (i < limit && text[i] != quote)
                {
                    if (text[i] == '<') { result.Valid = false; return result; }
                    i++;
                }
                if (i == limit) { result.Pending = new(name, start, PendingKind.QuotedValue, quote, valueStart); return result; }
                if (attribute == "xml:space") preserve = text[valueStart..i] != "default";
                i++;
            }
        }
        return result;
    }

    private static bool MatchesClosingTag(string text, int offset, string name)
    {
        string prefix = "</" + name;
        if (!text.AsSpan(offset).StartsWith(prefix, StringComparison.Ordinal)) return false;
        offset += prefix.Length;
        while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
        return offset < text.Length && text[offset] == '>';
    }
    private static bool InsideMarkupExtension(ReadOnlySpan<char> value)
    {
        // XAML parses the XML-decoded attribute. An encoded quote can delimit
        // an extension argument, and an encoded brace can open or close one.
        if (value.Contains('&'))
        {
            if (!TryDecodeXmlCharacters(value, out string decoded)) return true;
            value = decoded.AsSpan();
        }
        value = value.TrimStart();
        if (value.StartsWith("{}") || value.IsEmpty || value[0] != '{') return false;
        int depth = 0; char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (character == '\\') { i++; continue; }
            if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
            if (depth > 0 && character is '\'' or '"') { quote = character; continue; }
            if (character == '{') depth++;
            else if (character == '}' && depth > 0) depth--;
        }
        return depth > 0 || quote != '\0';
    }
    private static bool TryDecodeXmlCharacters(ReadOnlySpan<char> value, out string decoded)
    {
        var result = new StringBuilder(value.Length);
        while (!value.IsEmpty)
        {
            int ampersand = value.IndexOf('&');
            if (ampersand < 0) { result.Append(value); break; }
            result.Append(value[..ampersand]);
            value = value[(ampersand + 1)..];
            int end = value.IndexOf(';');
            if (end < 0) { decoded = ""; return false; }
            var entity = value[..end];
            if (entity.SequenceEqual("amp")) result.Append('&');
            else if (entity.SequenceEqual("lt")) result.Append('<');
            else if (entity.SequenceEqual("gt")) result.Append('>');
            else if (entity.SequenceEqual("quot")) result.Append('"');
            else if (entity.SequenceEqual("apos")) result.Append('\'');
            else
            {
                bool hexadecimal = entity.StartsWith("#x", StringComparison.Ordinal);
                int prefix = hexadecimal ? 2 : 1;
                if (entity.Length <= prefix || entity[0] != '#' ||
                    !uint.TryParse(entity[prefix..], hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                        CultureInfo.InvariantCulture, out uint scalar) ||
                    !(scalar is 9 or 10 or 13 or >= 0x20 and <= 0xD7FF or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF))
                { decoded = ""; return false; }
                result.Append(char.ConvertFromUtf32((int)scalar));
            }
            value = value[(end + 1)..];
        }
        decoded = result.ToString(); return true;
    }
    private static bool QualifiedName(string name)
    {
        var parts = name.Split(':');
        if (parts.Length is 0 or > 2 || parts.Any(string.IsNullOrEmpty)) return false;
        try { foreach (string part in parts) XmlConvert.VerifyNCName(part); return true; }
        catch (XmlException) { return false; }
    }
    private static bool NameCharacter(char character) => char.IsLetterOrDigit(character) || character is '_' or ':' or '.' or '-' || char.IsSurrogate(character);
    private static bool HorizontalWhitespace(ReadOnlySpan<char> value)
    { foreach (char character in value) if (character is not (' ' or '\t')) return false; return true; }
    private static string DetectNewLine(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? Environment.NewLine : text[newline] == '\r' && newline + 1 < text.Length && text[newline + 1] == '\n' ? "\r\n" : text[newline].ToString();
    }
}
