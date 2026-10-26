using WpfStudio.Core.Text;

namespace WpfStudio.Core.Tests;

public sealed class CodeSnippetTests
{
    [Fact]
    public void FirstOccurrenceIsTheFieldAndLaterOnesMirrorIt()
    {
        var parts = SnippetCatalog.Parse("Can$Name=Save$ $Name$ $$x $end$$selected$");
        Assert.Equal<SnippetPart>(
        [
            new SnippetLiteral("Can"), new SnippetField("Name", "Save"), new SnippetLiteral(" "), new SnippetMirror("Name"),
            new SnippetLiteral(" $x "), new SnippetCaret(), new SnippetSelection()
        ], parts);
    }

    [Fact]
    public void FieldsWithoutDefaultsUseTheirNameAndUnclosedMarkersStayLiteral()
    {
        Assert.Equal<SnippetPart>([new SnippetField("type", "type"), new SnippetLiteral(" $open")], SnippetCatalog.Parse("$type$ $open"));
    }

    [Fact]
    public void EveryCatalogSnippetParsesWithOneCaretAndUniqueShortcuts()
    {
        foreach (var snippet in SnippetCatalog.All)
        {
            var parts = SnippetCatalog.Parse(snippet.Body);
            Assert.True(parts.OfType<SnippetCaret>().Count() == 1, snippet.Shortcut);
            Assert.All(parts.OfType<SnippetMirror>(), mirror => Assert.Contains(parts.OfType<SnippetField>(), field => field.Name == mirror.Name));
        }
        Assert.Equal(SnippetCatalog.All.Count, SnippetCatalog.All.Select(s => (s.Shortcut, s.Extension)).Distinct().Count());
    }

    [Fact]
    public void ClassNameResolvesToTheNearestPrecedingTypeDeclaration()
    {
        const string text = "namespace App; public partial class Outer { } public sealed partial class CustomerViewModel : ObservableObject { | }";
        var offset = text.IndexOf('|');
        Assert.Equal("CustomerViewModel", SnippetCatalog.EnclosingTypeName(text, offset));
        var resolved = SnippetCatalog.Resolve(SnippetCatalog.Parse(SnippetCatalog.Find("ctor", ".cs")!.Body), text, offset);
        Assert.Contains(new SnippetField("classname", "CustomerViewModel"), resolved);
    }

    [Theory]
    [InlineData("    obsprop", "obsprop")]
    [InlineData("{ relay", "relay")]
    [InlineData("x.prop", null)]
    [InlineData("$prop", null)]
    [InlineData("prop|erty", null)]
    [InlineData("   ", null)]
    public void ShortcutMustBeAWholeWordBeforeTheCaret(string text, string? expected)
    {
        var caret = text.IndexOf('|') is var bar and >= 0 ? bar : text.Length;
        Assert.Equal(expected, SnippetCatalog.WordBefore(text.Replace("|", ""), caret, out _));
    }

    [Fact]
    public void LookupIsPerLanguage()
    {
        Assert.NotNull(SnippetCatalog.Find("grid", ".xaml"));
        Assert.Null(SnippetCatalog.Find("grid", ".cs"));
        Assert.Null(SnippetCatalog.Find("Relay", ".cs"));
    }
}
