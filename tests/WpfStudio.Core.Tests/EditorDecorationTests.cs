using WpfStudio.Core.Text;

namespace WpfStudio.Core.Tests;

public sealed class EditorDecorationTests
{
    [Theory]
    [InlineData(10, 4, 20, 0, 3, 10)]   // change after the range
    [InlineData(10, 4, 14, 0, 3, 10)]   // typing right after the range leaves it alone
    [InlineData(10, 4, 2, 1, 5, 14)]    // change before the range shifts it
    [InlineData(10, 4, 10, 0, 2, 12)]   // inserting at its start moves it
    public void RangesShiftAroundChanges(int start, int length, int offset, int removed, int inserted, int expectedStart)
    {
        Assert.Equal((expectedStart, length), DecorationOffsets.MapRange(start, length, offset, removed, inserted));
    }

    [Theory]
    [InlineData(12, 0, 1)]
    [InlineData(8, 4, 0)]
    [InlineData(13, 2, 0)]
    public void RangesTouchedByAChangeAreDropped(int offset, int removed, int inserted)
    {
        Assert.Null(DecorationOffsets.MapRange(10, 4, offset, removed, inserted));
    }

    [Fact]
    public void PointsFollowTheTextAfterThem()
    {
        Assert.Equal(5, DecorationOffsets.MapPoint(5, 9, 1, 0));
        Assert.Equal(13, DecorationOffsets.MapPoint(10, 10, 0, 3));
        Assert.Equal(8, DecorationOffsets.MapPoint(10, 4, 2, 0));
        Assert.Null(DecorationOffsets.MapPoint(10, 8, 4, 0));
    }

    [Fact]
    public void CSharpFoldsBlocksRegionsUsingsAndComments()
    {
        const string text = """"
            using System;
            using System.Linq;

            namespace App;

            #region Model
            /// <summary>A customer.</summary>
            /// <remarks>Persisted.</remarks>
            public class Customer
            {
                /* multi
                   line */
                public string Name { get; set; } = "{ not a block }";
                public string Raw = """{ "a": 1 }""";
                char Brace = '{';
                public void Save() { }
                public void Load()
                {
                    var path = @"C:\{dir}\";
                    var text = $"{Name} }} {{";
                }
            }
            #endregion
            """";
        var regions = CSharpFolding.Find(text);
        string Folded(FoldRegion region) => text[region.Start..region.End];
        Assert.Contains(regions, r => r.Title == "using …" && Folded(r) == "using System;\nusing System.Linq;");
        Assert.Contains(regions, r => r.Title == "Model" && Folded(r).StartsWith("#region Model") && Folded(r).EndsWith("#endregion"));
        Assert.Contains(regions, r => r.Title == "/// A customer. …");
        Assert.Contains(regions, r => r.Title == "/* … */");
        var blocks = regions.Where(r => r.Title == "{ … }").Select(Folded).ToArray();
        Assert.Equal(2, blocks.Length); // The class and Load(); single-line and literal braces are ignored.
        // A brace on its own line folds from the end of its declaration line.
        Assert.StartsWith("\n{\n    /* multi", blocks[0]);
        Assert.Equal("public class Customer", text[..regions.First(r => r.Title == "{ … }").Start].Split('\n')[^1].TrimStart());
        Assert.EndsWith("}", blocks[1]);
        Assert.Equal(regions.OrderBy(r => r.Start), regions);
    }

    [Fact]
    public void UnbalancedBracesDoNotThrow()
    {
        Assert.Empty(CSharpFolding.Find("class C {\n void M() {\n"));
        Assert.Empty(CSharpFolding.Find("}\n}\n"));
    }
}

public sealed class WhitespaceCleanupTests
{
    [Fact]
    public void TrimsTrailingWhitespaceAndAddsAFinalLineBreakInTheFilesStyle()
    {
        const string text = "class C  \r\n{\t\r\n    int x; \r\n}";
        var edits = WhitespaceCleanup.Edits(text, ".cs", trimTrailingWhitespace: true, ensureFinalNewline: true);
        Assert.Equal("class C\r\n{\r\n    int x;\r\n}\r\n", WhitespaceCleanup.Apply(text, edits));
        Assert.Equal("a\nb\n", WhitespaceCleanup.Apply("a\nb", WhitespaceCleanup.Edits("a\nb", ".cs", true, true)));
        Assert.Empty(WhitespaceCleanup.Edits("tidy\n", ".cs", true, true));
        Assert.Empty(WhitespaceCleanup.Edits("", ".cs", true, true));
    }

    [Fact]
    public void MarkdownKeepsTrailingSpacesAndEachOptionIsIndependent()
    {
        Assert.Empty(WhitespaceCleanup.Edits("line  \nnext", ".md", trimTrailingWhitespace: true, ensureFinalNewline: false));
        Assert.Equal("x \n", WhitespaceCleanup.Apply("x ", WhitespaceCleanup.Edits("x ", ".cs", trimTrailingWhitespace: false, ensureFinalNewline: true)));
        Assert.Equal("x", WhitespaceCleanup.Apply("x ", WhitespaceCleanup.Edits("x ", ".cs", trimTrailingWhitespace: true, ensureFinalNewline: false)));
    }
}
