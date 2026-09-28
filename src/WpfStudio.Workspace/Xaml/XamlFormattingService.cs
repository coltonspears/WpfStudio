using System.Text;
using System.Xml;
using Microsoft.CodeAnalysis;
using WpfStudio.Contracts;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

/// <summary>Formats source trivia without serializing XML or normalizing XAML values.</summary>
public sealed partial class XamlFormattingService
{
    private const int MaximumCharacters = 1_000_000;
    private const int MaximumEdits = 32768;

    public XamlFormattingResult Format(string text, Compilation? compilation = null,
        XamlFormattingOptions? options = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        options ??= new();
        XamlFormattingResult Reject(string reason) => new(false, [], [reason]);
        if (options.IndentSize is < 1 or > 8 || options.AttributeWrapColumn is < 40 or > 300
            || options.NewLine is not (null or "\n" or "\r\n" or "\r"))
            return Reject("Formatting options require an indentation size of 1–8, a wrap column of 40–300, and a standard newline sequence.");
        if (text.Length > MaximumCharacters) return Reject("The XAML document exceeds the 1,000,000-character formatting budget.");
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumCharacters });
            int count = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.Depth > 128 || reader.NodeType == XmlNodeType.Element && ++count > 16384)
                    return Reject("The XAML tree exceeds the formatting depth or element budget.");
            }
        }
        catch (XmlException) { return Reject("XAML formatting requires well-formed XML without a document type declaration. No edits were made."); }

        var document = Scan(text, token);
        string newline = options.NewLine ?? ExistingNewline(text);
        string unit = options.UseTabs ? "\t" : new string(' ', options.IndentSize);
        var indentation = Enumerable.Range(0, 131).Select(depth => string.Concat(Enumerable.Repeat(unit, depth))).ToArray();
        var edits = new List<TextEdit>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var resolver = compilation is null ? null : new SchemaTypeResolver(compilation, token);
        long addedCharacters = 0;
        bool exceeded = false;
        void Replace(int start, int end, string replacement)
        {
            if (start == end && replacement.Length == 0 || text.AsSpan(start, end - start).SequenceEqual(replacement)) return;
            // Only XML trivia outside values is eligible. This also protects unusual
            // entity-encoded whitespace, nonbreaking spaces, and opaque text content.
            if (!XmlWhitespace(text.AsSpan(start, end - start))) return;
            if (edits.Count >= MaximumEdits || (addedCharacters += Math.Max(0, replacement.Length - (end - start))) > 4_000_000)
            { exceeded = true; return; }
            edits.Add(new(start, end - start, replacement));
        }

        foreach (var node in document.Elements)
        {
            token.ThrowIfCancellationRequested();
            if (node.Parent?.OpaqueContents == true) { node.OpaqueContents = true; continue; }
            var policy = ContentPolicyFor(node.Syntax, resolver, options.UseKnownFrameworkContent);
            node.OpaqueContents = policy == ContentPolicy.Opaque;
            if (node.Syntax.Namespace == Language && node.Syntax.LocalName is "XData" or "Code") node.OpaqueContents = true;
            if (node.OpaqueContents && node.Children.Count > 0)
                warnings.Add("Unknown or opaque custom content was preserved; only its outer tag was formatted.");

            int tagWidth = node.Depth * options.IndentSize + node.Syntax.NameLength + 2;
            bool multiline = false;
            int previous = node.Syntax.NameStart + node.Syntax.NameLength;
            foreach (var attribute in node.Attributes)
            {
                tagWidth += 1 + attribute.NameEnd - attribute.Syntax.NameStart + 1 + attribute.End - attribute.QuoteStart;
                multiline |= HasNewline(text.AsSpan(previous, attribute.Syntax.NameStart - previous));
                previous = attribute.End;
            }
            multiline |= tagWidth > options.AttributeWrapColumn;
            previous = node.Syntax.NameStart + node.Syntax.NameLength;
            foreach (var attribute in node.Attributes)
            {
                Replace(previous, attribute.Syntax.NameStart, multiline ? newline + indentation[node.Depth + 1] : " ");
                Replace(attribute.NameEnd, attribute.EqualsPosition, "");
                Replace(attribute.EqualsPosition + 1, attribute.QuoteStart, "");
                previous = attribute.End;
            }
            bool separateDelimiter = HasNewline(text.AsSpan(previous, node.DelimiterStart - previous));
            Replace(previous, node.DelimiterStart, separateDelimiter ? newline + indentation[node.Depth] : node.SelfClosing ? " " : "");
            if (!node.SelfClosing) Replace(node.CloseNameEnd, node.End - 1, "");

            if (node.SelfClosing || node.Children.Count == 0 || node.PreserveSpace || node.HasText
                || policy != ContentPolicy.Structural || node.OpaqueContents) continue;
            int cursor = node.OpenEnd;
            foreach (var child in node.Children)
            {
                Replace(cursor, child.Start, newline + indentation[node.Depth + 1]);
                cursor = child.End;
            }
            Replace(cursor, node.CloseStart, newline + indentation[node.Depth]);
            if (exceeded) break;
        }
        if (exceeded) return Reject("Formatting would exceed the edit or output budget. No edits were made.");
        // Outside the root, comments, declarations and processing instructions do not
        // contribute to XAML content. Preserve the leading/trailing file whitespace.
        for (int index = 1; index < document.Children.Count; index++)
            Replace(document.Children[index - 1].End, document.Children[index].Start, newline);
        if (exceeded) return Reject("Formatting would exceed the edit or output budget. No edits were made.");
        token.ThrowIfCancellationRequested();
        return new(true, edits.OrderBy(edit => edit.Start).ToArray(), warnings.Order(StringComparer.Ordinal).ToArray());
    }

    private static bool XmlWhitespace(ReadOnlySpan<char> value)
    {
        foreach (char character in value) if (character is not (' ' or '\t' or '\n' or '\r')) return false;
        return true;
    }
    private static bool HasNewline(ReadOnlySpan<char> value) => value.Contains('\n') || value.Contains('\r');
    private static string ExistingNewline(string text)
    {
        int index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? "\n" : text[index] == '\r' ? index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r" : "\n";
    }

    private class Piece(int start, int end)
    {
        public int Start { get; } = start;
        public int End { get; set; } = end;
    }
    private sealed class Tag(Element syntax, Tag? parent, int start) : Piece(start, 0)
    {
        public Element Syntax { get; } = syntax;
        public Tag? Parent { get; } = parent;
        public int Depth { get; } = parent is null ? 0 : parent.Depth + 1;
        public List<RawAttribute> Attributes { get; } = [];
        public List<Piece> Children { get; } = [];
        public int DelimiterStart { get; set; }
        public int OpenEnd { get; set; }
        public int CloseStart { get; set; }
        public int CloseNameEnd { get; set; }
        public bool SelfClosing { get; set; }
        public bool HasText { get; set; }
        public bool PreserveSpace { get; set; }
        public bool OpaqueContents { get; set; }
    }
    private sealed record RawAttribute(XamlSyntax.Attribute Syntax, int NameEnd, int EqualsPosition, int QuoteStart, int End);
    private sealed record Document(List<Tag> Elements, List<Piece> Children);

    // The XML reader has already validated syntax. This second pass records exact source
    // spans without decoding/reserializing comments, values, entities, or CDATA.
    private static Document Scan(string text, CancellationToken token)
    {
        var elements = new List<Tag>();
        var roots = new List<Piece>();
        var stack = new Stack<Tag>();
        void Add(Piece piece) { if (stack.TryPeek(out var parent)) parent.Children.Add(piece); else roots.Add(piece); }
        for (int position = 0; position < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            int start = text.IndexOf('<', position);
            if (start < 0) break;
            if (stack.TryPeek(out var current) && !XmlWhitespace(text.AsSpan(position, start - position))) current.HasText = true;
            if (text.AsSpan(start).StartsWith("<!--"))
            { position = text.IndexOf("-->", start + 4, StringComparison.Ordinal) + 3; Add(new(start, position)); continue; }
            if (text.AsSpan(start).StartsWith("<![CDATA["))
            {
                position = text.IndexOf("]]>", start + 9, StringComparison.Ordinal) + 3;
                if (current is not null) current.HasText = true;
                Add(new(start, position)); continue;
            }
            if (text.AsSpan(start).StartsWith("<?"))
            { position = text.IndexOf("?>", start + 2, StringComparison.Ordinal) + 2; Add(new(start, position)); continue; }
            if (text[start + 1] == '/')
            {
                var closed = stack.Pop();
                closed.CloseStart = start;
                position = start + 2;
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '>') position++;
                closed.CloseNameEnd = position;
                position = text.IndexOf('>', position) + 1;
                closed.End = position;
                continue;
            }
            position = start + 1;
            int nameStart = position;
            while (!char.IsWhiteSpace(text[position]) && text[position] is not ('/' or '>')) position++;
            var syntax = new Element(text[nameStart..position], current?.Syntax, nameStart);
            current?.Syntax.Children.Add(syntax);
            var tag = new Tag(syntax, current, start);
            Add(tag); elements.Add(tag);
            while (true)
            {
                while (char.IsWhiteSpace(text[position])) position++;
                if (text[position] is '/' or '>') break;
                int attributeStart = position;
                while (!char.IsWhiteSpace(text[position]) && text[position] != '=') position++;
                int nameEnd = position;
                while (char.IsWhiteSpace(text[position])) position++;
                int equals = position++;
                while (char.IsWhiteSpace(text[position])) position++;
                int quote = position++;
                int valueStart = position;
                position = text.IndexOf(text[quote], position);
                var attribute = new XamlSyntax.Attribute(text[attributeStart..nameEnd], valueStart, position, text[valueStart..position], syntax, attributeStart);
                syntax.Attributes.Add(attribute);
                tag.Attributes.Add(new(attribute, nameEnd, equals, quote, ++position));
            }
            tag.DelimiterStart = position;
            tag.SelfClosing = text[position] == '/';
            position += tag.SelfClosing ? 2 : 1;
            syntax.StartTagEnd = tag.OpenEnd = position;
            tag.PreserveSpace = current?.PreserveSpace == true || syntax.Attribute("xml:space")?.Value.Text == "preserve";
            if (tag.SelfClosing) tag.End = position;
            else stack.Push(tag);
        }
        return new(elements, roots);
    }
}
