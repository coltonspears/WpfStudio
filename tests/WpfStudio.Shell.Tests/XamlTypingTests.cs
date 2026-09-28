using WpfStudio.App.Services;

namespace WpfStudio.Shell.Tests;

public sealed class XamlTypingTests
{
    [Theory]
    [InlineData("<Grid|", ">", "<Grid>|</Grid>")]
    [InlineData("<local:Widget|", ">", "<local:Widget>|</local:Widget>")]
    [InlineData("<Grid><Grid|</Grid>", ">", "<Grid><Grid>|</Grid></Grid>")]
    [InlineData("<Grid><Grid|</Grid></Grid>", ">", "<Grid><Grid>|</Grid></Grid>")]
    [InlineData("<Outer><Child|</Outer>", ">", "<Outer><Child>|</Child></Outer>")]
    [InlineData("<Grid|<Border/></Grid>", ">", "<Grid>|<Border/></Grid>")]
    [InlineData("<Grid Value='a > b'|", ">", "<Grid Value='a > b'>|</Grid>")]
    [InlineData("<?xml version='1.0'?><!-- <Fake> --><Grid|", ">", "<?xml version='1.0'?><!-- <Fake> --><Grid>|</Grid>")]
    [InlineData("<Grid><|", "/", "<Grid></Grid>|")]
    [InlineData("<Grid><Border/><|>", "/", "<Grid><Border/></Grid>|")]
    [InlineData("<local:Grid><|", "/", "<local:Grid></local:Grid>|")]
    [InlineData("<Grid Value=|>", "\"", "<Grid Value=\"|\">")]
    [InlineData("<Grid Value = \t|", "'", "<Grid Value = \t'|'")]
    [InlineData("<Grid Value=|\"existing\">", "\"", "<Grid Value=\"|existing\">")]
    [InlineData("<Grid Value='text|'", "'", "<Grid Value='text'|")]
    [InlineData("<Grid Value='{Binding Name}|'", "'", "<Grid Value='{Binding Name}'|")]
    [InlineData("<Grid Value='{}{literal}|'", "'", "<Grid Value='{}{literal}'|")]
    [InlineData("<Grid Value='{Binding ConverterParameter=&quot;{&quot;}|'", "'", "<Grid Value='{Binding ConverterParameter=&quot;{&quot;}'|")]
    [InlineData("<Grid Value=\"{Binding ConverterParameter=&apos;{&apos;}|\"", "\"", "<Grid Value=\"{Binding ConverterParameter=&apos;{&apos;}\"|")]
    [InlineData("<Grid Value='&#123;Binding ConverterParameter=&#34;&#123;&#x22;&#x7D;|'", "'", "<Grid Value='&#123;Binding ConverterParameter=&#34;&#123;&#x22;&#x7D;'|")]
    [InlineData("<Grid Value='&#x7B;&#125;{literal}|'", "'", "<Grid Value='&#x7B;&#125;{literal}'|")]
    public void TypedCharacterPlansPreserveLexicalContext(string before, string input, string expected)
    {
        int caret = before.IndexOf('|');
        string text = before.Remove(caret, 1);
        var plan = XamlTypingService.GetEdit(text, caret, 0, input);
        string after = plan is null ? text.Insert(caret, input) : text.Remove(plan.Edit.Start, plan.Edit.Length).Insert(plan.Edit.Start, plan.Edit.NewText);
        int newCaret = plan?.CaretOffset ?? caret + input.Length;
        Assert.Equal(expected, after.Insert(newCaret, "|"));
    }

    [Theory]
    [InlineData("<!-- <Grid|", ">")]
    [InlineData("<![CDATA[<Grid|", ">")]
    [InlineData("<?thing value=|", "\"")]
    [InlineData("<Grid Value='abc|'>", ">")]
    [InlineData("<Grid Value=\"{Binding ConverterParameter=|}\">", "'")]
    [InlineData("<Grid Value=\"{Binding ConverterParameter='abc|\"", "\"")]
    [InlineData("<Grid Value='{Binding ConverterParameter=&quot;}|'", "'")]
    [InlineData("<Grid Value='&#123;Binding ConverterParameter=&#x22;&#125;|'", "'")]
    [InlineData("<Grid Value='&#123;Binding Name|'", "'")]
    [InlineData("<Grid Value='{Binding Name}&unknown;|'", "'")]
    [InlineData("<Grid Value='{Binding Name}&#xD800;|'", "'")]
    [InlineData("<Grid Value='{Binding Name}&#0;|'", "'")]
    [InlineData("<Grid Value='{Binding Name}&#x110000;|'", "'")]
    [InlineData("<Grid Value='{Binding Name}&#x22|'", "'")]
    [InlineData("<Grid Value=word|", "\"")]
    [InlineData("<Grid Value|", "\"")]
    [InlineData("<Grid /|", ">")]
    [InlineData("<Grid|/>", ">")]
    [InlineData("</Grid|", ">")]
    [InlineData("<Grid><Bad></Wrong><Child|", ">")]
    [InlineData("<1Grid|", ">")]
    [InlineData("<local:|", ">")]
    [InlineData("<Grid|</Wrong>", ">")]
    [InlineData("<|", "/")]
    [InlineData("<Grid xml:space='preserve'>|</Grid>", "\n")]
    [InlineData("<Grid>literal|</Grid>", "\n")]
    [InlineData("<Grid|", "><Border/>")]
    [InlineData("<Grid Value=|", "\"text\"")]
    public void AmbiguousOrNonStructuralInputUsesNormalEditorBehavior(string before, string input)
    {
        int caret = before.IndexOf('|');
        Assert.Null(XamlTypingService.GetEdit(before.Remove(caret, 1), caret, 0, input));
    }

    [Theory]
    [InlineData("<Grid>|</Grid>", "    ", "\r\n", "<Grid>\r\n    |\r\n</Grid>")]
    [InlineData("<Grid>\n\t<Border>|</Border>\n</Grid>", "\t", "\n", "<Grid>\n\t<Border>\n\t\t|\n\t</Border>\n</Grid>")]
    [InlineData("  <Grid>|  </Grid>", "  ", "\n", "  <Grid>\n    |\n  </Grid>")]
    [InlineData("<Grid xml:space='preserve'><Border xml:space='default'>|</Border></Grid>", "  ", "\n", "<Grid xml:space='preserve'><Border xml:space='default'>\n  |\n</Border></Grid>")]
    public void EnterBetweenMatchingTagsUsesConfiguredWhitespace(string before, string indentation, string newline, string expected)
    {
        int caret = before.IndexOf('|');
        string text = before.Remove(caret, 1);
        var plan = Assert.IsType<XamlTypingEdit>(XamlTypingService.GetEdit(text, caret, 0, "\n", indentation, newline));
        string changed = text.Remove(plan.Edit.Start, plan.Edit.Length).Insert(plan.Edit.Start, plan.Edit.NewText);
        Assert.Equal(expected, changed.Insert(plan.CaretOffset, "|"));
    }

    [Fact]
    public void SelectionAndReadBudgetNeverProduceExtraText()
    {
        Assert.Null(XamlTypingService.GetEdit("<Grid", 5, 1, ">"));
        string oversized = new(' ', XamlTypingService.MaximumCharacters + 1);
        Assert.Null(XamlTypingService.GetEdit(oversized, oversized.Length, 0, ">"));
        Assert.Null(XamlTypingService.GetEdit("<Grid></Grid>", 6, 0, "\n", "not whitespace"));
    }
}
