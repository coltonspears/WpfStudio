using System.Text;
using WpfStudio.Core.Text;

namespace WpfStudio.Core.Tests;

public sealed class TextEncodingTests
{
    [Fact]
    public void DetectKeepsAFileInTheEncodingItWasReadIn()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("x")).ToArray();
        var utf8Marked = Encoding.UTF8.GetPreamble().Concat("x"u8.ToArray()).ToArray();
        var utf8Plain = "x"u8.ToArray();

        Assert.Equal(Encoding.Unicode.CodePage, TextEncoding.Detect(utf16).CodePage);
        Assert.NotEmpty(TextEncoding.Detect(utf16).GetPreamble());
        Assert.Equal(Encoding.UTF8.CodePage, TextEncoding.Detect(utf8Marked).CodePage);
        Assert.Equal(3, TextEncoding.Detect(utf8Marked).GetPreamble().Length);
        Assert.Equal(Encoding.UTF8.CodePage, TextEncoding.Detect(utf8Plain).CodePage);
        Assert.Empty(TextEncoding.Detect(utf8Plain).GetPreamble());
        Assert.Empty(TextEncoding.Detect([]).GetPreamble());
    }

    [Fact]
    public void TextThatIsNotValidUtf8IsReadInACodePageAndWrittenBackAsTheSameBytes()
    {
        // "// © Müller" as Windows-1252 or Latin-1 wrote it: A9 and FC are not UTF-8.
        byte[] bytes = [.. "// "u8, 0xA9, .. " M"u8, 0xFC, .. "ller\r\nclass C { }\r\n"u8];

        var (text, encoding) = TextEncoding.Decode(bytes);

        Assert.DoesNotContain('\uFFFD', text);
        Assert.False(TextEncoding.IsUnicode(encoding));
        Assert.Empty(encoding.GetPreamble());
        var edited = text.Replace("class C", "class Renamed", StringComparison.Ordinal);
        var (written, kept) = TextEncoding.Encode(edited, encoding);
        Assert.Same(encoding, kept);
        Assert.Equal(bytes.AsSpan(0, 16).ToArray(), written.AsSpan(0, 16).ToArray());
        // The document's code page stays its encoding when its bytes later happen to be plain ASCII.
        Assert.Same(encoding, TextEncoding.Decode("class C { }"u8.ToArray(), encoding).Encoding);
    }

    [Fact]
    public void ACodePageThatCannotStoreTheTextGivesWayToMarkedUtf8()
    {
        var legacy = TextEncoding.Decode([0xA9]).Encoding;

        var (bytes, encoding) = TextEncoding.Encode("\u00a9 \u4e2d\u6587 \U0001F600", legacy);

        Assert.Equal(Encoding.UTF8.CodePage, encoding.CodePage);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal("\u00a9 \u4e2d\u6587 \U0001F600", TextEncoding.Decode(bytes).Text);
    }

    [Fact]
    public void DecodeKeepsUtf8AndMarkedFilesAsTheyWere()
    {
        var plain = TextEncoding.Decode("na\u00efve"u8.ToArray());
        Assert.Equal(("na\u00efve", Encoding.UTF8.CodePage, 0), (plain.Text, plain.Encoding.CodePage, plain.Encoding.GetPreamble().Length));
        var marked = TextEncoding.Decode([.. Encoding.UTF8.GetPreamble(), .. "na\u00efve"u8]);
        Assert.Equal(("na\u00efve", Encoding.UTF8.CodePage, 3), (marked.Text, marked.Encoding.CodePage, marked.Encoding.GetPreamble().Length));
        var utf16 = TextEncoding.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("na\u00efve")]);
        Assert.Equal(("na\u00efve", Encoding.Unicode.CodePage), (utf16.Text, utf16.Encoding.CodePage));
        Assert.Equal(("", Encoding.UTF8.CodePage), (TextEncoding.Decode([]).Text, TextEncoding.Decode([]).Encoding.CodePage));
        // A marked file's encoding wins over the code page the document had.
        Assert.Equal(Encoding.UTF8.CodePage, TextEncoding.Decode([.. Encoding.UTF8.GetPreamble(), .. "x"u8], Encoding.Latin1).Encoding.CodePage);
    }

    [Fact]
    public void WritingBackWithTheDetectedEncodingRoundTripsUtf16()
    {
        var path = Path.Combine(Path.GetTempPath(), "wpfstudio-encoding-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "before", Encoding.Unicode);
            var bytes = File.ReadAllBytes(path);
            File.WriteAllText(path, "after", TextEncoding.Detect(bytes));
            var written = File.ReadAllBytes(path);
            Assert.True(written.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()));
            Assert.Equal("after", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
