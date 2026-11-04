namespace WpfStudio.Workspace.Tests;

// Shares a collection with SolutionCacheTests, whose constructor points the same variable somewhere temporary.
[Collection(Collection)]
public sealed class WorkspaceCacheIsolationTests
{
    public const string Collection = "Workspace cache folder";

    [Fact]
    public void TestsDoNotSaveModelsInThePersonsOwnCache()
    {
        var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio");
        Assert.False(SolutionCache.Directory.StartsWith(own, StringComparison.OrdinalIgnoreCase), SolutionCache.Directory);
    }
}
