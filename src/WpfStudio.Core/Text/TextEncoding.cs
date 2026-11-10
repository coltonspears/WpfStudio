using System.Text;

namespace WpfStudio.Core.Text;

public static class TextEncoding
{
    /// <summary>
    /// The encoding a file should be written back in, judged by its byte order mark: UTF-16 and UTF-32 marks map to
    /// those encodings (which emit the mark again), a UTF-8 mark to UTF-8 with a mark, and anything else to UTF-8
    /// without one, so a round trip never changes the file's encoding.
    /// </summary>
    public static Encoding Detect(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        reader.Peek();
        return reader.CurrentEncoding;
    }

    /// <summary>
    /// The encoding of text written before UTF-8 was the norm: the system's ANSI code page, as the C# compiler assumes for a file
    /// that is neither marked nor valid UTF-8, or Latin-1 where there is none. Latin-1 gives every byte a character and back.
    /// </summary>
    public static Encoding Legacy { get; } = FindLegacy();

    private static Encoding FindLegacy()
    {
        try
        {
            if (CodePagesEncodingProvider.Instance.GetEncoding(0) is { } ansi && !IsUnicode(ansi)) return ansi;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        return Encoding.Latin1;
    }

    /// <summary>True for the encodings a byte order mark identifies; every other is a code page.</summary>
    public static bool IsUnicode(Encoding encoding) => encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;

    /// <summary>
    /// A file's text, and the encoding that writes that text back as the same bytes. A byte order mark decides. Without one the text
    /// is UTF-8 when its bytes are valid UTF-8; otherwise it is in a code page: <paramref name="current"/> when the document already
    /// has one, else <see cref="Legacy"/>. Decoding such bytes as UTF-8 would put a replacement character in place of each letter
    /// outside ASCII, and the next save would write those in place of the original letters.
    /// </summary>
    public static (string Text, Encoding Encoding) Decode(byte[] bytes, Encoding? current = null)
    {
        var marked = Detect(bytes);
        bool hasMark = marked.GetPreamble() is { Length: > 0 } preamble && bytes.AsSpan().StartsWith(preamble);
        var encoding = hasMark ? marked
            : current is not null && !IsUnicode(current) ? current
            : System.Text.Unicode.Utf8.IsValid(bytes) ? new UTF8Encoding(false) : Legacy;
        var skip = hasMark ? encoding.GetPreamble().Length : 0;
        return (encoding.GetString(bytes, skip, bytes.Length - skip), encoding);
    }

    /// <summary>
    /// The bytes that store <paramref name="text"/>, and the encoding they are in. That is <paramref name="encoding"/> unless it is a code
    /// page that has no character for something in the text; then the file becomes UTF-8 with a byte order mark, which says what it is
    /// to whoever reads it next, instead of a question mark being written for each such character.
    /// </summary>
    public static (byte[] Bytes, Encoding Encoding) Encode(string text, Encoding encoding)
    {
        if (!IsUnicode(encoding))
        {
            var encoded = encoding.GetBytes(text);
            if (encoding.GetString(encoded) == text) return (encoded, encoding);
            encoding = new UTF8Encoding(true);
        }
        return ([.. encoding.GetPreamble(), .. encoding.GetBytes(text)], encoding);
    }
}
