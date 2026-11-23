using System.Xml;
using WpfStudio.Database.Services;

namespace WpfStudio.Database.Tests;

public sealed class DatabaseHardeningTests
{
    [Fact]
    public async Task QueryRecoveryStoreSaveEmptyDoesNotThrowWhenParentDirectoryIsMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "wpfstudio-missing-" + Guid.NewGuid().ToString("N"), "recovery.json");
        var store = new QueryRecoveryStore(missingPath);
        await store.SaveAsync([]);
        Assert.False(File.Exists(missingPath));
    }

    [Fact]
    public void SqlBatchParserPreservesClosingBlockCommentOnGoLine()
    {
        const string sql = "SELECT 1 /*\n*/ GO\nSELECT 2";
        var batches = SqlBatchParser.Parse(sql);
        Assert.Equal(2, batches.Count);
        Assert.Equal("SELECT 1 /*\n*/", batches[0].Text);
        Assert.Equal("SELECT 2", batches[1].Text);
    }

    [Fact]
    public void BoundedXmlFormatterDoesNotEscapeCharactersInsideComments()
    {
        const string xml = "<root><!-- a < b & c > d --><item>1 &lt; 2</item></root>";
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });
        var (formatted, truncated) = BoundedXmlFormatter.Read(xmlReader, 1024);
        Assert.False(truncated);
        Assert.Contains("<!-- a < b & c > d -->", formatted);
        Assert.Contains("<item>1 &lt; 2</item>", formatted);
    }
}
