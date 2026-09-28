using System.Net;
using System.Text;

namespace WpfStudio.Workspace.Xaml;

/// <summary>A non-executing, source-preserving reader that also accepts an unfinished start tag.</summary>
internal static class XamlSyntax
{
    internal const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    internal const string Language = "http://schemas.microsoft.com/winfx/2006/xaml";
    internal const string Design = "http://schemas.microsoft.com/expression/blend/2008";

    internal sealed class Element(string name, Element? parent, int nameStart = 0)
    {
        public string Name { get; } = name;
        public int Start => Math.Max(0, NameStart - 1);
        public int NameStart { get; } = nameStart;
        public int NameLength => Name.Length;
        public int StartTagEnd { get; set; }
        public bool IsClosed { get; set; }
        public bool HasSignificantText { get; set; }
        public string LocalName => SplitName(Name).Local;
        public Element? Parent { get; } = parent;
        public List<Element> Children { get; } = [];
        public List<Attribute> Attributes { get; } = [];
        public string? Namespace => LookupNamespace(SplitName(Name).Prefix);
        public string? LookupNamespace(string prefix)
        {
            var name = prefix.Length == 0 ? "xmlns" : "xmlns:" + prefix;
            return Attributes.FirstOrDefault(a => a.Name == name)?.Value.Text ?? Parent?.LookupNamespace(prefix);
        }
        public Attribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);
        public Attribute? Attribute(string ns, string local) => Attributes.FirstOrDefault(a =>
        {
            var name = SplitName(a.Name);
            return name.Local == local && name.Prefix.Length > 0 && LookupNamespace(name.Prefix) == ns;
        });
    }

    internal sealed class Attribute(string name, int valueStart, int valueEnd, string raw, Element owner, int nameStart = 0)
    {
        public string Name { get; } = name;
        public int NameStart { get; } = nameStart;
        public int NameLength => Name.Length;
        public int ValueStart { get; } = valueStart;
        public int ValueEnd { get; } = valueEnd;
        public Element Owner { get; } = owner;
        public DecodedValue Value { get; } = new(raw, valueStart);
    }

    /// <summary>Maps decoded XML characters back to the original UTF-16 source, including entities.</summary>
    internal sealed class DecodedValue
    {
        private readonly int[] _starts;
        private readonly int[] _ends;
        private readonly int _sourceStart;
        public string Text { get; }
        public DecodedValue(string raw, int sourceStart)
        {
            _sourceStart = sourceStart;
            var text = new StringBuilder();
            var starts = new List<int>();
            var ends = new List<int>();
            for (var i = 0; i < raw.Length;)
            {
                var end = raw[i] == '&' ? raw.IndexOf(';', i + 1) : -1;
                var encoded = end >= 0 ? raw[i..(end + 1)] : "";
                var decoded = end >= 0 ? WebUtility.HtmlDecode(encoded) : "";
                if (end >= 0 && decoded != encoded)
                {
                    foreach (var c in decoded) { text.Append(c); starts.Add(sourceStart + i); ends.Add(sourceStart + end + 1); }
                    i = end + 1;
                }
                else { text.Append(raw[i]); starts.Add(sourceStart + i); ends.Add(sourceStart + i + 1); i++; }
            }
            Text = text.ToString(); _starts = starts.ToArray(); _ends = ends.ToArray();
        }
        public int PositionAt(int sourcePosition)
        {
            var position = 0;
            while (position < _ends.Length && _ends[position] <= sourcePosition) position++;
            return position;
        }
        public (int Start, int Length) Span(int start, int length)
        {
            var sourceStart = start < _starts.Length ? _starts[start] : _ends.Length > 0 ? _ends[^1] : _sourceStart;
            return (sourceStart, length == 0 ? 0 : _ends[start + length - 1] - sourceStart);
        }
    }

    internal static IReadOnlyList<Element> Read(string text, CancellationToken token)
    {
        var elements = new List<Element>();
        var stack = new Stack<Element>();
        for (var i = 0; i < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            var open = text.IndexOf('<', i);
            if (stack.TryPeek(out var textOwner) && !string.IsNullOrWhiteSpace(text[i..(open < 0 ? text.Length : open)])) textOwner.HasSignificantText = true;
            if (open < 0) break;
            i = open + 1;
            if (text.AsSpan(open).StartsWith("<!--")) { i = After(text, "-->", open + 4); continue; }
            if (text.AsSpan(open).StartsWith("<![CDATA[")) { if (stack.TryPeek(out var cdataOwner)) cdataOwner.HasSignificantText = true; i = After(text, "]]>", open + 9); continue; }
            if (i >= text.Length) break;
            if (text[i] is '!' or '?') { i = After(text, ">", i + 1); continue; }
            if (text[i] == '/')
            {
                i++;
                var nameStart = i;
                while (i < text.Length && IsName(text[i])) i++;
                var closeName = text[nameStart..i];
                if (stack.Any(e => e.Name == closeName))
                {
                    while (stack.TryPop(out var closing))
                        if (closing.Name == closeName) { closing.IsClosed = i < text.Length && text[i..After(text, ">", i)].TrimEnd().EndsWith('>'); break; }
                }
                i = After(text, ">", i); continue;
            }
            var start = i;
            while (i < text.Length && IsName(text[i])) i++;
            if (start == i) continue;
            var element = new Element(text[start..i], stack.TryPeek(out var parent) ? parent : null, start);
            element.Parent?.Children.Add(element);
            elements.Add(element);
            var selfClosing = false;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || text[i] == '<') break;
                if (text[i] == '>') { i++; break; }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>') { i += 2; selfClosing = true; break; }
                var attributeStart = i;
                while (i < text.Length && IsName(text[i])) i++;
                if (attributeStart == i) { i++; continue; }
                var attributeName = text[attributeStart..i];
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || text[i] != '=') continue;
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || text[i] is not ('\'' or '"')) continue;
                var quote = text[i++];
                var valueStart = i;
                var valueEnd = text.IndexOf(quote, i);
                if (valueEnd < 0) valueEnd = text.Length;
                element.Attributes.Add(new Attribute(attributeName, valueStart, valueEnd, text[valueStart..valueEnd], element, attributeStart));
                i = Math.Min(text.Length, valueEnd + 1);
            }
            element.StartTagEnd = i;
            element.IsClosed = selfClosing;
            if (!selfClosing) stack.Push(element);
        }
        return elements;
    }

    private static bool IsName(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';
    private static int After(string text, string marker, int start)
    {
        var end = text.IndexOf(marker, start, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + marker.Length;
    }
    internal static (string Prefix, string Local) SplitName(string name)
    {
        var colon = name.IndexOf(':');
        return colon < 0 ? ("", name) : (name[..colon], name[(colon + 1)..]);
    }

    internal sealed record Argument(string? Name, string Value, int Start, int Length);
    internal sealed record Extension(string Name, IReadOnlyList<Argument> Arguments, bool IsComplete)
    {
        public Argument? Argument(string name) => Arguments.FirstOrDefault(a => a.Name == name);
        public Argument? Positional => Arguments.FirstOrDefault(a => a.Name == null);
    }

    internal static Extension? ParseExtension(string text)
    {
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i++] != '{' || i < text.Length && text[i] == '}') return null;
        var nameStart = i;
        while (i < text.Length && IsName(text[i])) i++;
        if (nameStart == i) return null;
        var name = text[nameStart..i];
        var args = new List<Argument>();
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        var start = i;
        var depth = 0;
        var quote = '\0';
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '{') { depth++; continue; }
            if (c == '}' && depth > 0) { depth--; continue; }
            if (depth == 0 && c is ',' or '}')
            {
                AddArgument(text, start, i, args);
                if (c == '}') return text[(i + 1)..].All(char.IsWhiteSpace) ? new Extension(name, args, true) : null;
                start = i + 1;
            }
        }
        AddArgument(text, start, i, args);
        return new Extension(name, args, false);
    }
    private static void AddArgument(string text, int start, int end, List<Argument> args)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (start == end) return;
        var equals = -1;
        for (var i = start; i < end; i++)
        {
            if (text[i] is '{' or '\'' or '"') break;
            if (text[i] == '=') { equals = i; break; }
        }
        string? name = null;
        if (equals >= 0)
        {
            name = text[start..equals].Trim(); start = equals + 1;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
        }
        args.Add(new Argument(name, text[start..end], start, end - start));
    }
    internal static bool IsExtension(Element element, Extension extension, string ns, string local)
    {
        var name = SplitName(extension.Name);
        return name.Local == local && (element.LookupNamespace(name.Prefix) == ns || name.Prefix.Length == 0 && ns == Presentation && element.LookupNamespace("") == null);
    }
}
