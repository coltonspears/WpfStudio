using WpfStudio.Core.Text;

namespace WpfStudio.Core.Tests;

public sealed class CompletionMatchTests
{
    [Fact]
    public void TheTypedTextIsPickedOutWhereItAppearsWhole()
    {
        Assert.Equal([(0, 2)], CompletionMatch.Ranges("Obsolete", "ob"));
        Assert.Equal([(4, 4)], CompletionMatch.Ranges("OpenFileDialog", "file"));
        // The start of a word is what the typing meant, over the same letters earlier inside one.
        Assert.Equal([(21, 1)], CompletionMatch.Ranges("DesignerSerializationOptions", "o"));
        Assert.Equal([(1, 2)], CompletionMatch.Ranges("Assert", "ss"));
    }

    [Fact]
    public void OtherwiseEachLetterIsPickedOutInOrder()
    {
        Assert.Equal([(0, 1), (4, 1), (8, 1)], CompletionMatch.Ranges("OpenFileDialog", "OFD"));
        Assert.Equal([(0, 2), (4, 1)], CompletionMatch.Ranges("OpenFileDialog", "opf"));
    }

    [Theory]
    [InlineData("Obsolete", "")]
    [InlineData("Obsolete", "xyz")]
    [InlineData("Obsolete", "eo")]
    [InlineData("Ob", "obsolete")]
    public void NothingIsPickedOutWhenNothingMatches(string text, string query) => Assert.Empty(CompletionMatch.Ranges(text, query));
}
