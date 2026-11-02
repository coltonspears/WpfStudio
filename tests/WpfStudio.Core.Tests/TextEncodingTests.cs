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
