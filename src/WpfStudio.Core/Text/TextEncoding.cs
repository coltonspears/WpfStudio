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
}
