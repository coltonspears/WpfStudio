using WpfStudio.App.Controls;
using WpfStudio.App.Controls.Editor;

namespace WpfStudio.Shell.Tests;

public sealed class CompletionGlyphTests
{
    [Theory]
    [InlineData("class", "SymbolClass")]
    [InlineData("record", "SymbolClass")]
    [InlineData("module", "SymbolClass")]
    [InlineData("struct", "SymbolStruct")]
    [InlineData("record struct", "SymbolStruct")]
    [InlineData("interface", "SymbolInterface")]
    [InlineData("enum", "SymbolEnum")]
    [InlineData("enum member", "SymbolEnumMember")]
    [InlineData("delegate", "SymbolDelegate")]
    [InlineData("method", "SymbolMethod")]
    [InlineData("extension method", "SymbolExtensionMethod")]
    [InlineData("operator", "SymbolOperator")]
    [InlineData("property", "SymbolProperty")]
    [InlineData("field", "SymbolField")]
    [InlineData("constant", "SymbolConstant")]
    [InlineData("event", "SymbolEvent")]
    [InlineData("local", "SymbolLocal")]
    [InlineData("range variable", "SymbolLocal")]
    [InlineData("parameter", "SymbolParameter")]
    [InlineData("type parameter", "SymbolTypeParameter")]
    [InlineData("namespace", "SymbolNamespace")]
    [InlineData("keyword", "SymbolKeyword")]
    [InlineData("label", "SymbolLabel")]
    [InlineData("snippet", "SymbolSnippet")]
    [InlineData(null, "SymbolOther")]
    public void EveryKindOfSymbolHasADrawnGlyph(string? kind, string glyph)
    {
        Assert.Equal(glyph, CompletionRow.GlyphFor(kind));
        Assert.True(Glyph.Exists(glyph));
    }
}
