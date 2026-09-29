using WpfStudio.Core.Text;

namespace WpfStudio.Core.Tests;

public sealed class TextDiffTests
{
    [Fact]
    public void IdenticalTextAndLineEndingOnlyDifferencesHaveNoChanges()
    {
        Assert.True(TextDiff.Compute("a\nb\n", "a\nb\n").IsIdentical);
        var endings = TextDiff.Compute("a\r\nb\r\n", "a\nb");
        Assert.True(endings.IsIdentical);
        Assert.Equal(2, endings.Rows.Count);
        Assert.Equal(0, endings.AddedLines + endings.RemovedLines);
    }

    [Fact]
    public void InsertionKeepsSurroundingLineNumbersAligned()
    {
        var diff = TextDiff.Compute("class A\n{\n}\n", "class A\n{\n    int x;\n    int y;\n}\n");
        Assert.Equal(new[] { DiffRowKind.Unchanged, DiffRowKind.Unchanged, DiffRowKind.Added, DiffRowKind.Added, DiffRowKind.Unchanged },
            diff.Rows.Select(row => row.Kind));
        Assert.Equal((2, 0), (diff.AddedLines, diff.RemovedLines));
        Assert.Equal(new int?[] { 1, 2, null, null, 3 }, diff.Rows.Select(row => row.OldLine));
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, diff.Rows.Select(row => row.NewLine));
        Assert.Equal(new[] { 2 }, diff.ChangeBlocks);
        Assert.Equal("    int x;", diff.Rows[2].NewText);
    }

    [Fact]
    public void ModifiedLinesArePairedWithWordSpans()
    {
        var diff = TextDiff.Compute("public string Name { get; set; }\n", "public string FullName { get; set; }\n");
        var row = Assert.Single(diff.Rows);
        Assert.Equal(DiffRowKind.Modified, row.Kind);
        Assert.Equal((1, 1), (row.OldLine, row.NewLine));
        var oldSpan = Assert.Single(row.OldSpans);
        var newSpan = Assert.Single(row.NewSpans);
        Assert.Equal("Name", row.OldText.Substring(oldSpan.Start, oldSpan.Length));
        Assert.Equal("FullName", row.NewText.Substring(newSpan.Start, newSpan.Length));
        Assert.Equal((1, 1), (diff.AddedLines, diff.RemovedLines));
    }

    [Fact]
    public void DissimilarPairedLinesHaveNoNoisyWordSpans()
    {
        var diff = TextDiff.Compute("return total;\n", "throw new InvalidOperationException(message);\n");
        var row = Assert.Single(diff.Rows);
        Assert.Equal(DiffRowKind.Modified, row.Kind);
        Assert.Empty(row.OldSpans);
        Assert.Empty(row.NewSpans);
    }

    [Fact]
    public void UnequalChangeBlocksPairThenAddOrRemoveTheRest()
    {
        var diff = TextDiff.Compute("a\nold1\nold2\nold3\nz\n", "a\nnew1\nz\n");
        Assert.Equal(new[] { DiffRowKind.Unchanged, DiffRowKind.Modified, DiffRowKind.Removed, DiffRowKind.Removed, DiffRowKind.Unchanged },
            diff.Rows.Select(row => row.Kind));
        Assert.Equal((1, 3), (diff.AddedLines, diff.RemovedLines));
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, diff.Rows.Select(row => row.OldLine));
        Assert.Equal(new int?[] { 1, 2, null, null, 3 }, diff.Rows.Select(row => row.NewLine));
    }

    [Fact]
    public void NewAndDeletedFilesAreSingleSided()
    {
        var created = TextDiff.Compute("", "one\ntwo\n");
        Assert.All(created.Rows, row => Assert.Equal(DiffRowKind.Added, row.Kind));
        Assert.Equal(2, created.AddedLines);
        var deleted = TextDiff.Compute("one\ntwo", null);
        Assert.All(deleted.Rows, row => Assert.Equal(DiffRowKind.Removed, row.Kind));
        Assert.Equal(2, deleted.RemovedLines);
    }

    [Fact]
    public void HunksIncludeContextAndMergeNearbyBlocks()
    {
        var before = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i}").ToArray();
        lines[4] = "changed 5"; lines[8] = "changed 9"; lines[30] = "changed 31";
        var diff = TextDiff.Compute(before, string.Join("\n", lines));
        Assert.Equal(new[] { 4, 8, 30 }, diff.ChangeBlocks);
        // Blocks at rows 4 and 8 share context; row 30 is a separate hunk.
        Assert.Equal(new[] { new DiffHunk(1, 12), new DiffHunk(27, 34) }, diff.Hunks(3));
        Assert.Equal(new[] { new DiffHunk(4, 5), new DiffHunk(8, 9), new DiffHunk(30, 31) }, diff.Hunks(0));
    }

    [Fact]
    public void ExceedingTheSearchBudgetFallsBackToOneConsistentReplacement()
    {
        var before = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"a{i}"));
        var after = string.Join("\n", Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? $"a{i}" : $"b{i}"));
        var exact = TextDiff.Compute(before, after);
        Assert.True(exact.IsExact);
        Assert.Equal((100, 100), (exact.AddedLines, exact.RemovedLines));
        var bounded = TextDiff.Compute(before, after, maxEditDistance: 10);
        Assert.False(bounded.IsExact);
        Assert.Equal(TextDiff.SplitLines(before), bounded.Rows.Where(row => row.OldLine is not null).Select(row => row.OldText));
        Assert.Equal(TextDiff.SplitLines(after), bounded.Rows.Where(row => row.NewLine is not null).Select(row => row.NewText));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("x", 1)]
    [InlineData("x\n", 1)]
    [InlineData("x\n\n", 2)]
    [InlineData("x\r\ny", 2)]
    public void SplitLinesIgnoresOneFinalTerminator(string text, int count) => Assert.Equal(count, TextDiff.SplitLines(text).Length);
}
