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
        Assert.StartsWith("{\n    /* multi", blocks[0]);
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
