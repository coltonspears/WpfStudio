using System.Collections;

namespace WpfStudio.Core.Tests;

public sealed class WorkspaceStateStoreTests
{
    private static string DirectoryPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "WpfStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task TheLaterCallWinsWhenAnEarlierCallIsStillTakingItsSnapshot()
    {
        var directory = DirectoryPath();
        var workspace = Path.Combine(directory, "App.sln");
        var store = new WorkspaceStateStore<string>("State", directory);
        using var older = new BlockingItems(["older"]);
        var first = Task.Run(() => store.SaveAsync(workspace, older));
        Assert.True(older.Entered.Wait(TimeSpan.FromSeconds(30)));
        // Started second, so it is the latest call even though the first has not finished reading its items.
        var second = Task.Run(() => store.SaveAsync(workspace, ["newer"]));
        await Task.WhenAny(second, Task.Delay(500));
        older.Release.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["newer"], await store.LoadAsync(workspace));
    }

    [Fact]
    public async Task SequentialSavesKeepTheLastItemsAndEachSaveSnapshotsItsItems()
    {
        var directory = DirectoryPath();
        var workspace = Path.Combine(directory, "App.sln");
        var store = new WorkspaceStateStore<string>("State", directory);
        var items = new List<string> { "one" };
        var saving = store.SaveAsync(workspace, items);
        items.Add("two");
        await saving;
        Assert.Equal(["one"], await store.LoadAsync(workspace));
        await store.SaveAsync(workspace, items);
        Assert.Equal(["one", "two"], await store.LoadAsync(workspace));
    }

    private sealed class BlockingItems(string[] items) : IEnumerable<string>, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public IEnumerator<string> GetEnumerator()
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return ((IEnumerable<string>)items).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
