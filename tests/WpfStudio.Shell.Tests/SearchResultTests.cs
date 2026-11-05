using System.Text.RegularExpressions;
using WpfStudio.App.ViewModels;

namespace WpfStudio.Shell.Tests;

/// <summary>What find in workspace matches under its switches, and what a result row shows of the line it found.</summary>
public sealed class SearchResultTests
{
    private static List<NavigationResult> Find(string text, string query, bool matchCase = false, bool wholeWord = false, bool useRegex = false)
    {
        var found = new List<NavigationResult>();
        ShellViewModel.FindIn(@"C:\work\Order.cs", text, ShellViewModel.SearchPattern(query, matchCase, wholeWord, useRegex), found);
        return found;
    }

    [Fact]
    public void ARowIsTheMatchingLineWithoutItsIndentationAndMarksWhatWasFound()
    {
        var found = Find("class Order\r\n{\r\n    public int Count { get; set; }\r\n\tint count;\r\n}\r\n", "count");
        Assert.Equal([3, 4], found.Select(result => result.Line));
        var first = found[0];
        Assert.Equal("public int Count { get; set; }", first.Text);
        Assert.Equal("Count", first.Text.Substring(first.MatchStart, first.MatchLength));
        // The column and the offset are the file's, for navigating; the mark is the row's.
        Assert.Equal(16, first.Column);
        Assert.Equal("class Order\r\n{\r\n    public int ".Length, first.Start);
        Assert.Equal("int count;", found[1].Text);
        Assert.Equal(4, found[1].MatchStart);
    }

    [Fact]
    public void TheSwitchesNarrowWhatMatches()
    {
        const string text = "count Count recount count2 _count\ncounts\n";
        Assert.Equal(2, Find(text, "count").Count);
        Assert.Equal(["count Count recount count2 _count"], Find(text, "count", wholeWord: true).Select(result => result.Text));
        Assert.Equal(6, Find(text, "Count", matchCase: true)[0].MatchStart);
        Assert.Empty(Find(text, "COUNT", matchCase: true));

        // Whole word applies to the sides of the text that are words, so punctuation can be searched for with it on.
        Assert.Single(Find("a => b\nb =>c\n", "=> b", wholeWord: true));
        Assert.Single(Find("total(1)\nsubtotal(2)\n", "total(", wholeWord: true));

        // Text is searched as typed unless it is a regular expression.
        Assert.Single(Find("a.b\naxb\n", "a.b"));
        Assert.Equal(2, Find("a.b\naxb\n", "a.b", useRegex: true).Count);
        var groups = Find("int a = 12;\nint b = x;\n", @"=\s*\d+", useRegex: true);
        Assert.Equal("= 12", Assert.Single(groups).Text.Substring(groups[0].MatchStart, groups[0].MatchLength));
        Assert.ThrowsAny<ArgumentException>(() => ShellViewModel.SearchPattern("(", false, false, useRegex: true));
        Assert.Single(Find("call(\n", "("));

        // An expression that matches nothing at all, such as an empty alternative, lists no line.
        Assert.Empty(Find("abc\n", "x*", useRegex: true));
    }

    [Fact]
    public void AVeryLongLineIsShownAroundWhatWasFound()
    {
        var line = "    var data = \"" + new string('a', 400) + "\" + needle + \"" + new string('b', 400) + "\";";
        var row = Assert.Single(Find(line, "needle"));
        Assert.True(row.Text.Length <= 242, row.Text.Length.ToString());
        Assert.StartsWith("…", row.Text);
        Assert.EndsWith("…", row.Text);
        Assert.Equal("needle", row.Text.Substring(row.MatchStart, row.MatchLength));

        // Found near its start, a long line is only cut at its end.
        var early = Assert.Single(Find("needle " + new string('c', 500), "needle"));
        Assert.StartsWith("needle ccc", early.Text);
        Assert.EndsWith("…", early.Text);
        Assert.Equal(0, early.MatchStart);
    }

    [Fact]
    public void ARowWhoseMatchIsNotOnItsLineShowsTheLineUnmarked()
    {
        var row = new NavigationResult(@"C:\work\Order.cs", 3, 9, "Count");
        // A reference whose span runs past the line, or a line that changed since it was found.
        Assert.Equal(-1, row.WithLine("    int Count;", 8, 40).MatchStart);
        Assert.Equal("int Count;", row.WithLine("    int Count;", 8, 40).Text);
        Assert.Equal(-1, row.WithLine("    int Count;", 2, 3).MatchStart);
        var marked = row.WithLine("    int Count;", 8, 5);
        Assert.Equal(("int Count;", 4, 5), (marked.Text, marked.MatchStart, marked.MatchLength));
        Assert.Equal("Order.cs:3", marked.Location);
    }

    [Fact]
    public void ASearchIsCutOffAtItsLimit()
    {
        var found = new List<NavigationResult>();
        var text = string.Concat(Enumerable.Repeat("x\n", ShellViewModel.SearchResultLimit + 50));
        ShellViewModel.FindIn("a.cs", text, new Regex("x"), found);
        Assert.Equal(ShellViewModel.SearchResultLimit, found.Count);
    }
}
