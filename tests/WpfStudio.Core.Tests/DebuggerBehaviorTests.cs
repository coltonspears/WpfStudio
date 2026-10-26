using WpfStudio.Contracts;
using WpfStudio.Core.Text;

namespace WpfStudio.Core.Tests;

public sealed class DebuggerBehaviorTests
{
    [Theory]
    [InlineData("", HitConditionKind.Always, 0)]
    [InlineData("5", HitConditionKind.Equal, 5)]
    [InlineData("= 5", HitConditionKind.Equal, 5)]
    [InlineData("==12", HitConditionKind.Equal, 12)]
    [InlineData(">= 3", HitConditionKind.AtLeast, 3)]
    [InlineData("%4", HitConditionKind.Multiple, 4)]
    public void HitConditionsParse(string text, HitConditionKind kind, int count)
    {
        Assert.True(HitCondition.TryParse(text, out var condition, out var error));
        Assert.Null(error);
        Assert.Equal(new HitCondition(kind, count), condition);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData(">5")]
    [InlineData("five")]
    [InlineData("%")]
    public void InvalidHitConditionsExplainTheSyntax(string text)
    {
        Assert.False(HitCondition.TryParse(text, out _, out var error));
        Assert.Contains("%5", error);
    }

    [Fact]
    public void HitConditionsBreakOnTheRequestedHits()
    {
        int[] Hits(HitCondition condition) => Enumerable.Range(1, 10).Where(condition.ShouldBreak).ToArray();
        Assert.Equal([3], Hits(new(HitConditionKind.Equal, 3)));
        Assert.Equal([8, 9, 10], Hits(new(HitConditionKind.AtLeast, 8)));
        Assert.Equal([4, 8], Hits(new(HitConditionKind.Multiple, 4)));
        Assert.Equal(10, Hits(HitCondition.Always).Length);
    }

    [Fact]
    public void TracepointMessagesSplitExpressionsAndEscapes()
    {
        var segments = TracepointMessage.Parse("Count={Count} items={new[] { 1 }.Length} {{literal}} {unclosed");
        Assert.Equal(
        [
            new(false, "Count="), new(true, "Count"), new(false, " items="), new(true, "new[] { 1 }.Length"),
            new(false, " {literal} {unclosed")
        ], segments);
    }

    [Fact]
    public async Task TracepointMessagesEvaluateEachHoleAndKeepGoingAfterErrors()
    {
        var text = await TracepointMessage.FormatAsync("$FUNCTION on $TID: {a} {bad} {b}", expression => expression == "bad"
            ? throw new InvalidOperationException("not in scope") : Task.FromResult(expression.ToUpperInvariant()),
            new Dictionary<string, string> { ["FUNCTION"] = "Save()", ["TID"] = "7" });
        Assert.Equal("Save() on 7: A <not in scope> B", text);
    }

    [Theory]
    [InlineData("var total = order.Customer.Na$$me;", "order.Customer.Name")]
    [InlineData("var total = order.Cust$$omer.Name;", "order.Customer")]
    [InlineData("return this.cou$$nt;", "this.count")]
    [InlineData("return item?.Pri$$ce;", "item?.Price")]
    [InlineData("Save(cus$$tomer);", "customer")]
    [InlineData("var x = @cl$$ass;", "@class")]
    [InlineData("var x = name$$;", "name")]
    [InlineData("var x = Load().Na$$me;", "Name")]
    [InlineData("var x = items[0].Na$$me;", "Name")]
    public void DataTipsEvaluateTheHoveredMemberChain(string line, string expected)
    {
        var offset = line.IndexOf("$$", StringComparison.Ordinal);
        Assert.Equal(expected, DebugExpressionLocator.Find(line.Replace("$$", ""), offset)?.Expression);
    }

    [Theory]
    [InlineData("ret$$urn value;")]
    [InlineData("var x = 4$$2;")]
    [InlineData("// see cust$$omer")]
    [InlineData("var x = \"cust$$omer\";")]
    [InlineData("var x = a + $$ b;")]
    [InlineData("base$$.Save();")]
    public void DataTipsSkipKeywordsLiteralsCommentsAndWhitespace(string line)
    {
        var offset = line.IndexOf("$$", StringComparison.Ordinal);
        Assert.Null(DebugExpressionLocator.Find(line.Replace("$$", ""), offset));
    }
}
