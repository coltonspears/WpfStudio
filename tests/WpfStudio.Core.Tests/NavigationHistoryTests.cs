using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Tests;

public sealed class NavigationHistoryTests
{
    private static NavigationLocation At(string file, int line) => new(file, line * 10, line);

    [Fact]
    public void BackAndForwardRetraceJumps()
    {
        var history = new NavigationHistory();
        history.Record(At("A.cs", 1));            // A:1 → B:50
        history.Record(At("B.cs", 50));           // B:50 → C:5
        Assert.Equal(At("B.cs", 50), history.GoBack(At("C.cs", 5)));
        Assert.Equal(At("A.cs", 1), history.GoBack(At("B.cs", 50)));
        Assert.False(history.CanGoBack);
        Assert.Equal(At("B.cs", 50), history.GoForward(At("A.cs", 1)));
        Assert.Equal(At("C.cs", 5), history.GoForward(At("B.cs", 50)));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void ANewJumpDiscardsForwardHistory()
    {
        var history = new NavigationHistory();
        history.Record(At("A.cs", 1));
        history.GoBack(At("B.cs", 1));
        Assert.True(history.CanGoForward);
        history.Record(At("A.cs", 40));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void NearbyOriginsInTheSameFileMerge()
    {
        var history = new NavigationHistory();
        history.Record(At("A.cs", 10));
        history.Record(At("a.cs", 14));
        Assert.Equal(At("a.cs", 14), history.GoBack(null));
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void CapacityDropsTheOldestEntries()
    {
        var history = new NavigationHistory(capacity: 3);
        for (var i = 0; i < 5; i++) history.Record(At($"F{i}.cs", 1));
        Assert.Equal("F4.cs", history.GoBack(null)!.Path);
        Assert.Equal("F3.cs", history.GoBack(null)!.Path);
        Assert.Equal("F2.cs", history.GoBack(null)!.Path);
        Assert.Null(history.GoBack(null));
    }
}
