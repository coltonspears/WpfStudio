using System.Globalization;
using System.Windows.Media;
using WpfStudio.App.Features.Git;

namespace WpfStudio.Shell.Tests;

public sealed class GitAvatarTests
{
    [Fact]
    public void AuthorsKeepOneColourSlotHoweverTheirNameIsCased()
    {
        var commit = new GitCommit("abc", "abc", "Ada Lovelace", "", "Subject");
        Assert.Equal(commit.AvatarIndex, new GitCommit("def", "def", " ada lovelace ", "", "Other").AvatarIndex);
        Assert.Equal(commit.AvatarIndex, new GitCommitDetails("abc", [], "ADA LOVELACE", "", "", "", "", "Subject", []).AvatarIndex);
    }

    [Theory]
    [InlineData("Ada Lovelace", 4)]
    [InlineData("Linus Torvalds", 3)]
    [InlineData("", 1)]
    public void TheSlotIsFixedAcrossProcesses(string author, int expected) => Assert.Equal(expected, GitText.AvatarIndex(author));

    [Fact]
    public void EverySlotHasItsOwnFrozenBrush()
    {
        var converter = new GitAvatarBrushConverter();
        var brushes = Enumerable.Range(0, GitText.AvatarCount).Select(index => (Brush)converter.Convert(index, typeof(Brush), null, CultureInfo.InvariantCulture)).ToArray();
        Assert.All(brushes, brush => { Assert.True(brush.IsFrozen); Assert.NotEqual(Brushes.Gray, brush); });
        Assert.Equal(GitText.AvatarCount, brushes.Distinct().Count());
        Assert.Equal(Brushes.Gray, converter.Convert(GitText.AvatarCount, typeof(Brush), null, CultureInfo.InvariantCulture));
    }
}
