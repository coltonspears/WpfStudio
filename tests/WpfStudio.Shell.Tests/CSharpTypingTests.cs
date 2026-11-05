using WpfStudio.App.Services;

namespace WpfStudio.Shell.Tests;

public sealed class CSharpTypingTests
{
    /// <summary>Applies the plan for <paramref name="input"/> typed at the | in <paramref name="marked"/>; null when the editor's default applies.</summary>
    private static string? Type(string marked, string input, bool pairs = true)
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        var plan = CSharpTypingService.GetEdit(text, caret, 0, input, "    ", "\n", pairs);
        if (plan is null) return null;
        var result = text.Remove(plan.Edit.Start, plan.Edit.Length).Insert(plan.Edit.Start, plan.Edit.NewText);
        return result.Insert(plan.CaretOffset, "|");
    }

    private static string? Backspace(string marked)
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        var plan = CSharpTypingService.Backspace(text, caret);
        return plan is null ? null : text.Remove(plan.Edit.Start, plan.Edit.Length).Insert(plan.Edit.Start, plan.Edit.NewText).Insert(plan.CaretOffset, "|");
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
    {
        Assert.Equal("    if (ready)\n    {|}", Type("    if (ready)\n        |", "{"));
        Assert.Equal("    if (ready)\n    {|", Type("    if (ready)\n        |", "{", pairs: false));
    }

    [Fact]
    public void OpeningBracketsAndQuotesBringTheirClosingOnes()
    {
        Assert.Equal("Run(|)", Type("Run|", "("));
        Assert.Equal("items[|]", Type("items|", "["));
        Assert.Equal("var o = new Order {|}", Type("var o = new Order |", "{"));
        Assert.Equal("var s = \"|\";", Type("var s = |;", "\""));
        Assert.Equal("var s = $\"|\"", Type("var s = $|", "\""));
        Assert.Equal("var c = '|'", Type("var c = |", "'"));
        Assert.Equal("Run(Read(|))", Type("Run(Read|)", "("));
        // Before a name the bracket opens what is already there, and after a name a quote is not the start of a literal.
        Assert.Null(Type("Run|value", "("));
        Assert.Null(Type("var s = name|", "\""));
        Assert.Null(Type("Run|", "(", pairs: false));
        Assert.Null(Type("var s = |;", "\"", pairs: false));
    }

    [Fact]
    public void TypingAClosingBracketOrQuoteThatIsAlreadyThereMovesPastIt()
    {
        Assert.Equal("Run()|", Type("Run(|)", ")"));
        Assert.Equal("items[0]|", Type("items[0|]", "]"));
        Assert.Equal("var s = \"text\"|;", Type("var s = \"text|\";", "\""));
        Assert.Equal("var c = 'x'|;", Type("var c = 'x|';", "'"));
        Assert.Equal("new Order { }|", Type("new Order { |}", "}"));
        // One closer short: the typed one is the missing one.
        Assert.Null(Type("Run(Read(x|)", ")"));
        // An escaped quote inside a string is typed, except in a verbatim string, where a backslash escapes nothing.
        Assert.Null(Type("var s = \"a\\|\";", "\""));
        Assert.Equal("var s = @\"C:\\\"|;", Type("var s = @\"C:\\|\";", "\""));
    }

    [Fact]
    public void QuotesInCommentsAndTheThirdQuoteOfARawStringAreJustTyped()
    {
        Assert.Null(Type("// don|", "'"));
        Assert.Null(Type("/* say | */", "\""));
        Assert.Equal("var s = \"\"|", Type("var s = \"|\"", "\""));
        Assert.Null(Type("var s = \"\"|", "\""));
    }

    [Fact]
    public void BackspaceBetweenAnEmptyPairRemovesBoth()
    {
        Assert.Equal("Run|;", Backspace("Run(|);"));
        Assert.Equal("var s = |;", Backspace("var s = \"|\";"));
        Assert.Equal("var o = new Order |", Backspace("var o = new Order {|}"));
        // Two literals side by side are not a pair, and neither is a bracket inside a string.
        Assert.Null(Backspace("var s = \"a\"|\"b\";"));
        Assert.Null(Backspace("var s = \"(|)\";"));
        Assert.Null(Backspace("Run(a|)"));
    }

    [Fact]
    public void AnOpenerTypedOverASelectionWrapsIt()
    {
        const string text = "var total = price + tax;";
        int start = text.IndexOf("price", StringComparison.Ordinal);
        var plan = CSharpTypingService.Surround(text, start, "price + tax".Length, "(");
        Assert.NotNull(plan);
        Assert.Equal("var total = (price + tax);", text.Remove(plan.Edit.Start, plan.Edit.Length).Insert(plan.Edit.Start, plan.Edit.NewText));
        Assert.Equal(start + "(price + tax)".Length, plan.CaretOffset);
        Assert.Null(CSharpTypingService.Surround(text, start, 5, ";"));
        Assert.Null(CSharpTypingService.Surround("// price", 3, 5, "("));
    }

    [Fact]
    public void EnterAfterAStatementThatBeganEarlierReturnsToWhereItBegan()
    {
        // A braceless body, a wrapped call and a chain all end the statement that started further up and further left.
        Assert.Equal("    if (ready)\n        Run();\n    |", Type("    if (ready)\n        Run();|", "\n"));
        Assert.Equal("    var total = Sum(\n        first,\n        second);\n    |", Type("    var total = Sum(\n        first,\n        second);|", "\n"));
        Assert.Equal("    var names = people\n        .Select(p => p.Name)\n        .ToList();\n    |", Type("    var names = people\n        .Select(p => p.Name)\n        .ToList();|", "\n"));
        Assert.Equal("    if (a)\n        if (b)\n            Run();\n    |", Type("    if (a)\n        if (b)\n            Run();|", "\n"));
        // A statement on one line, one inside a block, and the statements of a case section stay where they are.
        Assert.Null(Type("    Run();\n    Stop();|", "\n"));
        Assert.Null(Type("    Run(() =>\n    {\n        Stop();|", "\n"));
        Assert.Null(Type("    switch (x)\n    {\n        case 1:\n            Run();|", "\n"));
        Assert.Null(Type("    for (int i = 0; i < n; i++) Run();|", "\n"));
    }

    [Fact]
    public void EnterAfterALineThatGoesOnIndentsItsContinuation()
    {
        Assert.Equal("    var total =\n        |", Type("    var total =|", "\n"));
        Assert.Equal("    public int Count =>\n        |", Type("    public int Count =>|", "\n"));
        Assert.Equal("    Run(\n        |)", Type("    Run(|)", "\n"));
        Assert.Equal("    var ok = ready &&\n        |", Type("    var ok = ready &&|", "\n"));
        Assert.Equal("        case 1:\n            |", Type("        case 1:|", "\n"));
        Assert.Equal("        default:\n            |", Type("        default:|", "\n"));
        // Later lines of the same statement keep the indentation they have.
        Assert.Null(Type("    var total =\n        first +|", "\n"));
    }

    [Fact]
    public void EnterInsideADocumentationCommentContinuesIt()
    {
        Assert.Equal("    /// <summary>\n    /// |", Type("    /// <summary>|", "\n"));
        Assert.Equal("    /// Adds\n    /// |tax.", Type("    /// Adds|tax.", "\n"));
        Assert.Null(Type("    // a note|", "\n"));
    }

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
    [InlineData("var s = @\"{|")]
    [InlineData("var s = \"\"\"\n    {|")]
    public void StringsAndCommentsKeepTheDefaultBehaviour(string marked) => Assert.Null(Type(marked, "\n"));

    [Fact]
    public void BracesInsideStringsDoNotCountAsBlocks()
        => Assert.Equal("    void M()\n    {\n        var s = \"}\";\n    }|", Type("    void M()\n    {\n        var s = \"}\";\n        |", "}"));

    [Fact]
    public void AClosedStringBeforeTheCaretLeavesItInCode()
        => Assert.Equal("    if (name == \"x\")\n        |", Type("    if (name == \"x\")|", "\n"));

    [Fact]
    public void UnterminatedVerbatimStringKeepsItsWhitespaceWhenAClosingBraceIsTyped()
        => Assert.Null(Type("void M()\n{\n    var s = @\"\n        |", "}"));
}
