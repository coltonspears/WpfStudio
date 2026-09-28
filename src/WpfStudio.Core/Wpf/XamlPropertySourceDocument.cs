using System.Xml;
using System.Xml.Linq;

namespace WpfStudio.Core.Wpf;

/// <summary>Strict XML validation plus lexical spans; serialization never rewrites the source.</summary>
internal sealed class XamlPropertySourceDocument
{
    internal enum PartKind { Text, CData, Comment, Instruction, Element }
    internal sealed record Part(int Start, int End, PartKind Kind, Element? Child = null)
    {
        public int ActualEnd => Child?.End ?? End;
    }
    internal sealed record Attribute(string Name, XAttribute Xml, int LeadingStart, int Start, int End, int ValueStart, int ValueEnd, char Quote);
    internal sealed class Element(XElement xml, string name, int start, int nameEnd, Element? parent)
    {
        public XElement Xml { get; } = xml;
        public string Name { get; } = name;
        public int Start { get; } = start;
        public int NameEnd { get; } = nameEnd;
        public int StartTagEnd { get; set; }
        public int CloseMarkerStart { get; set; }
        public int CloseTagStart { get; set; }
        public int End { get; set; }
        public Element? Parent { get; } = parent;
        public List<Attribute> Attributes { get; } = [];
        public List<Part> Parts { get; } = [];
        public IEnumerable<Element> Children => Parts.Where(p => p.Child is not null).Select(p => p.Child!);
    }
    public IReadOnlyList<Element> Elements { get; }
    private XamlPropertySourceDocument(IReadOnlyList<Element> elements) => Elements = elements;

    public static XamlPropertySourceDocument Parse(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (xml.Root is null) throw new XmlException("A XAML root element is required.");
        using var xmlElements = xml.Root.DescendantsAndSelf().GetEnumerator();
        var elements = new List<Element>();
        var stack = new Stack<Element>();
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('<', i);
            if (open < 0) open = text.Length;
            if (open > i && stack.TryPeek(out var textOwner)) textOwner.Parts.Add(new Part(i, open, PartKind.Text));
            if (open == text.Length) break;
            i = open;
            if (text.AsSpan(i).StartsWith("<!--")) { AddPart(PartKind.Comment, "-->", 4); continue; }
            if (text.AsSpan(i).StartsWith("<![CDATA[")) { AddPart(PartKind.CData, "]]>", 9); continue; }
            if (text.AsSpan(i).StartsWith("<?")) { AddPart(PartKind.Instruction, "?>", 2); continue; }
            if (text.AsSpan(i).StartsWith("</"))
            {
                var end = text.IndexOf('>', i + 2) + 1;
                if (end <= i || !stack.TryPop(out var current)) throw new XmlException("The source element boundaries are inconsistent.");
                current.CloseTagStart = i; current.End = end; i = end; continue;
            }
            if (!xmlElements.MoveNext()) throw new XmlException("The source element mapping is inconsistent.");
            i++;
            var nameStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('>' or '/')) i++;
            var element = new Element(xmlElements.Current, text[nameStart..i], open, i, stack.TryPeek(out var parent) ? parent : null);
            elements.Add(element);
            element.Parent?.Parts.Add(new Part(open, 0, PartKind.Element, element));
            var xmlAttributes = element.Xml.Attributes().ToArray();
            var attributeIndex = 0;
            var selfClosing = false;
            while (i < text.Length)
            {
                var leadingStart = i;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) throw new XmlException("The source start tag is incomplete.");
                if (text[i] == '>') { element.CloseMarkerStart = i; i++; break; }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>')
                {
                    element.CloseMarkerStart = i; selfClosing = true; i += 2; break;
                }
                var attributeStart = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '=') i++;
                var name = text[attributeStart..i];
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || text[i++] != '=') throw new XmlException("An attribute assignment is incomplete.");
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || text[i] is not ('\'' or '"')) throw new XmlException("An attribute must have a quoted value.");
                var quote = text[i++];
                var valueStart = i;
                var valueEnd = text.IndexOf(quote, i);
                if (valueEnd < 0 || attributeIndex >= xmlAttributes.Length) throw new XmlException("The source attribute mapping is inconsistent.");
                i = valueEnd + 1;
                element.Attributes.Add(new Attribute(name, xmlAttributes[attributeIndex++], leadingStart, attributeStart, i, valueStart, valueEnd, quote));
            }
            if (attributeIndex != xmlAttributes.Length) throw new XmlException("The source attribute mapping is inconsistent.");
            element.StartTagEnd = i;
            if (selfClosing) { element.CloseTagStart = element.CloseMarkerStart; element.End = i; }
            else stack.Push(element);

            void AddPart(PartKind kind, string close, int openingLength)
            {
                var closeStart = text.IndexOf(close, i + openingLength, StringComparison.Ordinal);
                if (closeStart < 0) throw new XmlException("A source token is incomplete.");
                var end = closeStart + close.Length;
                if (stack.TryPeek(out var owner)) owner.Parts.Add(new Part(i, end, kind));
                i = end;
            }
        }
        if (stack.Count != 0 || xmlElements.MoveNext()) throw new XmlException("The source element mapping is incomplete.");
        return new XamlPropertySourceDocument(elements);
    }

    public static (int Line, int Column) Position(string text, int offset)
    {
        var line = 1; var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\r') { if (i + 1 < offset && text[i + 1] == '\n') i++; line++; lineStart = i + 1; }
            else if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        return (line, offset - lineStart + 1);
    }
}
