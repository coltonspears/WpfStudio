using WpfStudio.App.Services;

namespace WpfStudio.Shell.Tests;

public sealed class CSharpTypingTests
{
    /// <summary>Applies the plan for <paramref name="input"/> typed at the | in <paramref name="marked"/>; null when the editor's default applies.</summary>
    private static string? Type(string marked, string input)
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        var plan = CSharpTypingService.GetEdit(text, caret, 0, input, "    ", "\n");
        if (plan is null) return null;
        var result = text.Remove(plan.Edit.Start, plan.Edit.Length).Insert(plan.Edit.Start, plan.Edit.NewText);
        return result.Insert(plan.CaretOffset, "|");
    }

    [Fact]
    public void EnterBetweenBracesOpensAnIndentedLineAndMovesTheClosingBraceDown()
    {
        Assert.Equal("    void Run()\n    {\n        |\n    }", Type("    void Run()\n    {|}", "\n"));
        Assert.Equal("    void Run() {\n        |\n    }", Type("    void Run() {| }", "\n"));
    }

    [Fact]
    public void EnterAfterAnOpeningBraceIndentsTheRestOfTheLine()
        => Assert.Equal("{\n    |Run();", Type("{| Run();", "\n"));

    [Fact]
    public void EnterBeforeAClosingBraceLinesItUpWithItsBlock()
        => Assert.Equal("class C\n{\n    void M() { Run();\n    |}\n}", Type("class C\n{\n    void M() { Run();|}\n}", "\n"));

    [Fact]
    public void EnterAfterABracelessHeaderIndentsTheBody()
    {
        Assert.Equal("    if (ready)\n        |", Type("    if (ready)|", "\n"));
        Assert.Equal("    else\n        |", Type("    else|", "\n"));
        Assert.Null(Type("    if (a) Run(b);|", "\n"));
        Assert.Null(Type("    Run(b)|", "\n"));
    }

    [Fact]
    public void BraceTypedOnTheBodyLineOfAHeaderSnapsBackToTheHeader()
        => Assert.Equal("    if (ready)\n    {|", Type("    if (ready)\n        |", "{"));

    [Fact]
    public void ClosingBraceOnABlankLineLinesUpWithItsOpeningLine()
    {
        Assert.Equal("    void M()\n    {\n        Run();\n    }|", Type("    void M()\n    {\n        Run();\n        |", "}"));
        Assert.Null(Type("    {\n    |", "}"));
    }

    [Theory]
    [InlineData("var s = \"{|\";")]
    [InlineData("// {|")]
    [InlineData("/* {| */")]
    [InlineData("var s = @\"{|\";")]
    public void StringsAndCommentsKeepTheDefaultBehaviour(string marked) => Assert.Null(Type(marked, "\n"));

    [Fact]
    public void BracesInsideStringsDoNotCountAsBlocks()
        => Assert.Equal("    void M()\n    {\n        var s = \"}\";\n    }|", Type("    void M()\n    {\n        var s = \"}\";\n        |", "}"));
}
