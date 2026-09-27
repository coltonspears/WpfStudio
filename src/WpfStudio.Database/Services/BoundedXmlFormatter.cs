using System.Text;
using System.Xml;

namespace WpfStudio.Database.Services;

internal static class BoundedXmlFormatter
{
    public static (string Text, bool Truncated) Read(XmlReader reader, int maximumCharacters)
    {
        using var output = new LimitedWriter(maximumCharacters);
        try
        {
            using var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true, ConformanceLevel = ConformanceLevel.Fragment, CloseOutput = false });
            var chunk = new char[4096];
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                        bool empty = reader.IsEmptyElement;
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                writer.WriteStartAttribute(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                                CopyValue();
                                writer.WriteEndAttribute();
                            } while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }
                        if (empty) writer.WriteEndElement();
                        break;
                    case XmlNodeType.EndElement: writer.WriteFullEndElement(); break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace: CopyValue(); break;
                    case XmlNodeType.Comment: writer.WriteRaw("<!--"); CopyValue(); writer.WriteRaw("-->"); break;
                }
                writer.Flush();
            }
            return (output.ToString(), false);

            void CopyValue()
            {
                if (!reader.CanReadValueChunk) { writer.WriteString(reader.Value); return; }
                int count;
                while ((count = reader.ReadValueChunk(chunk, 0, chunk.Length)) > 0) { writer.WriteChars(chunk, 0, count); writer.Flush(); }
            }
        }
        catch (DisplayLimitException) { return (output.ToString(), true); }
    }
    private sealed class DisplayLimitException : Exception;
    private sealed class LimitedWriter(int maximumCharacters) : TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.Unicode;
        public override void Write(char value)
        {
            if (_text.Length >= maximumCharacters) throw new DisplayLimitException();
            _text.Append(value);
        }
        public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));
        public override void Write(string? value) { if (value is not null) Write(value.AsSpan()); }
        public override void Write(ReadOnlySpan<char> buffer)
        {
            int remaining = maximumCharacters - _text.Length;
            _text.Append(buffer[..Math.Min(remaining, buffer.Length)]);
            if (buffer.Length > remaining) throw new DisplayLimitException();
        }
        public override string ToString() => _text.ToString();
    }
}
