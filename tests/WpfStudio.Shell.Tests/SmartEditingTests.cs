using WpfStudio.App.Services;

namespace WpfStudio.Shell.Tests;

public sealed class SmartEditingTests
{
    /// <summary>Applies a plan to the text marked with | for the caret; null when the editor's default applies.</summary>
    private static string? Apply(string marked, Func<string, int, XamlTypingEdit?> plan)
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        return plan(text, caret) is { } edit ? text.Remove(edit.Edit.Start, edit.Edit.Length).Insert(edit.Edit.Start, edit.Edit.NewText).Insert(edit.CaretOffset, "|") : null;
    }

    private static string? Backspace(string marked) => Apply(marked, (text, caret) => SmartEditing.Backspace(text, caret, 4));
    private static string? Delete(string marked) => Apply(marked, SmartEditing.Delete);

    [Fact]
    public void BackspaceOnALineOfOnlyIndentationRemovesTheLine()
    {
        Assert.Equal("void Run()\n{|\n}", Backspace("void Run()\n{\n    |\n}"));
        Assert.Equal("first|\r\nlast", Backspace("first\r\n\t\t|\r\nlast"));
        // The caret may be anywhere in the whitespace, and whitespace after it goes too.
        Assert.Equal("a|\nb", Backspace("a\n  |    \nb"));
        // The first line has no line above, so only its whitespace goes.
        Assert.Equal("|\nnext", Backspace("    |\nnext"));
    }

    [Fact]
    public void BackspaceInTheIndentationOfCodeRemovesOneLevel()
    {
        Assert.Equal("    |Run();", Backspace("        |Run();"));
        Assert.Equal("    |Run();", Backspace("      |Run();"));
        Assert.Equal("|Run();", Backspace("    |Run();"));
        // One space, a tab and anything after the first character are the editor's own business.
        Assert.Null(Backspace(" |Run();"));
        Assert.Null(Backspace("\t|Run();"));
        Assert.Null(Backspace("    Run|();"));
        Assert.Null(Backspace("|Run();"));
        Assert.Null(Backspace("line\n|"));
    }

    [Fact]
    public void DeleteAtTheEndOfALineJoinsTheNextWithoutItsIndentation()
    {
        Assert.Equal("Run(|first);", Delete("Run(|\n        first);"));
        Assert.Equal("a|b", Delete("a|  \r\n\tb"));
        // A plain join, a blank line and the middle of a line need nothing special.
        Assert.Null(Delete("a|\nb"));
        Assert.Null(Delete("    |\n    b"));
        Assert.Null(Delete("a|b\n    c"));
        Assert.Null(Delete("last|"));
    }

    [Fact]
    public void PastedLinesMoveToTheCaretsIndentationAndKeepTheirShape()
    {
        const string document = "class C\n{\n        \n}";
        int caret = document.IndexOf("        ", StringComparison.Ordinal) + 8;
        // Copied from four columns in, starting at the first line's text.
        Assert.Equal("if (ready)\n        {\n            Run();\n        }", SmartEditing.ReindentPaste(document, caret, "if (ready)\n    {\n        Run();\n    }", 4, true));
        // Copied with its indentation, from deeper than it is pasted.
        Assert.Equal("Run();\n        Stop();", SmartEditing.ReindentPaste(document, caret, "                Run();\n                Stop();", 4, true));
        // A first line that opens what the selection does not close: the rest are its contents, one level in.
        Assert.Equal("Run(first,\n            second);", SmartEditing.ReindentPaste(document, caret, "Run(first,\n        second);", 4, true));
        Assert.Equal("<Grid>\n            <Button />\n        </Grid>", SmartEditing.ReindentPaste(document, caret, "<Grid>\n    <Button />\n</Grid>", 4, true));
        // Blank lines stay blank, line endings stay as copied, and tabs are used when the editor indents with them.
        Assert.Equal("a();\r\n\r\n        b();", SmartEditing.ReindentPaste(document, caret, "a();\r\n\r\n    b();", 4, true));
        Assert.Equal("a();\n\t\tb();", SmartEditing.ReindentPaste(document, caret, "a();\nb();", 4, false));
    }

    [Fact]
    public void APasteThatIsNotWholeLinesIsLeftAsCopied()
    {
        Assert.Null(SmartEditing.ReindentPaste("    ", 4, "one line", 4, true));
        Assert.Null(SmartEditing.ReindentPaste("var x = ;", 8, "a\n    b", 4, true));
        Assert.Null(SmartEditing.ReindentPaste("", 0, "a\n    b", 4, true));
        Assert.Null(SmartEditing.ReindentPaste("    ", 4, "a();\n    b();", 4, true));
    }
}
