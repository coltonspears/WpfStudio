using System.Text.Json;
using System.Windows.Threading;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

public sealed class BreakpointStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-BreakpointStore-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task BreakpointsAndWatchesSurviveAReload()
    {
        var store = new BreakpointStore(directory);
        await store.LoadAsync(@"C:\work\App");
        store.Breakpoints.Add(new BreakpointViewModel { Path = @"C:\work\App\a.cs", Line = 3, Condition = "x > 1", Enabled = false, HitCondition = ">=2", LogMessage = "at {x}" });
        store.Toggle(@"C:\work\App\b.cs", 9);
        store.WatchExpressions.Add("x");
        await store.SaveAsync();

        var again = new BreakpointStore(directory);
        await again.LoadAsync(@"c:\WORK\app"); // The same workspace on a case-insensitive file system.
        Assert.Equal(["x"], again.WatchExpressions);
        Assert.Equal(2, again.Breakpoints.Count);
        var first = again.Breakpoints[0];
        Assert.Equal((@"C:\work\App\a.cs", 3, "x > 1", false, ">=2", "at {x}"), (first.Path, first.Line, first.Condition, first.Enabled, first.HitCondition, first.LogMessage));
        var second = again.Breakpoints[1];
        Assert.Equal((@"C:\work\App\b.cs", 9, "", true, "", ""), (second.Path, second.Line, second.Condition, second.Enabled, second.HitCondition, second.LogMessage));
    }

    [Fact]
    public async Task EachWorkspaceHasItsOwnState()
    {
        var store = new BreakpointStore(directory);
        await store.LoadAsync(@"C:\work\One");
        store.Toggle(@"C:\work\One\a.cs", 1);
        store.WatchExpressions.Add("one");
        await store.SaveAsync();
        await store.LoadAsync(@"C:\work\Two");
        Assert.Empty(store.Breakpoints);
        Assert.Empty(store.WatchExpressions);
    }

    [Fact]
    public async Task SavingBeforeAWorkspaceIsOpenWritesNothing()
    {
        var store = new BreakpointStore(directory);
        store.Toggle(@"C:\a.cs", 1);
        await store.SaveAsync();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task AnUnreadableFileIsReportedAndLeavesTheStoreEmpty()
    {
        var store = new BreakpointStore(directory);
        await store.LoadAsync(@"C:\work\App");
        store.Toggle(@"C:\work\App\a.cs", 1);
        await store.SaveAsync();
        var file = Assert.Single(Directory.GetFiles(directory));
        await File.WriteAllTextAsync(file, "{ not json");
        await Assert.ThrowsAnyAsync<JsonException>(() => store.LoadAsync(@"C:\work\App"));
        Assert.Empty(store.Breakpoints);
    }

    [Fact]
    public async Task AnOlderFileWithoutTheNewerFieldsStillLoads()
    {
        var store = new BreakpointStore(directory);
        await store.LoadAsync(@"C:\work\App");
        await store.SaveAsync();
        var file = Assert.Single(Directory.GetFiles(directory));
        await File.WriteAllTextAsync(file, """{"Breakpoints":[{"Path":"C:\\a.cs","Line":4}],"Watches":["w"]}""");
        await store.LoadAsync(@"C:\work\App");
        var breakpoint = Assert.Single(store.Breakpoints);
        Assert.Equal(("C:\\a.cs", 4, "", ""), (breakpoint.Path, breakpoint.Line, breakpoint.Condition, breakpoint.HitCondition));
    }

    [Fact]
    public Task SavesThatOverlapOnTheUiThreadAllCompleteAndLeaveNoTemporaryFiles()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var store = new BreakpointStore(directory);
                    await store.LoadAsync(@"C:\work\App");
                    await Task.WhenAll(Enumerable.Range(1, 20).Select(line => { store.Toggle(@"C:\work\App\a.cs", line); return store.SaveAsync(); }));
                    var saved = new BreakpointStore(directory);
                    await saved.LoadAsync(@"C:\work\App");
                    // Writes can finish out of order and the last move wins, so this may be any of the snapshots.
                    Assert.InRange(saved.Breakpoints.Count, 1, 20);
                    Assert.Single(Directory.GetFiles(directory));
                    result.TrySetResult();
                }
                catch (Exception exception) { result.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void TogglingRemovesTheSameLineWhateverThePathCase()
    {
        var store = new BreakpointStore(directory);
        store.Toggle(@"C:\a.cs", 1);
        store.Toggle(@"C:\A.CS", 2);
        store.Toggle(@"C:\A.CS", 1);
        Assert.Equal(2, Assert.Single(store.Breakpoints).Line);
    }

    [Fact]
    public void FilesAreDistinctAndEnabledInSkipsDisabledOnes()
    {
        var store = new BreakpointStore(directory);
        store.Toggle(@"C:\a.cs", 1);
        store.Toggle(@"C:\A.cs", 2);
        store.Toggle(@"C:\b.cs", 3);
        store.Breakpoints[0].Enabled = false;
        Assert.Equal([@"C:\a.cs", @"C:\b.cs"], store.Files);
        Assert.Equal(2, Assert.Single(store.EnabledIn(@"C:\a.cs")).Line);
    }

    [Fact]
    public void AHitPrefersTheAdaptersIdOverTheLine()
    {
        var store = new BreakpointStore(directory);
        store.Toggle(@"C:\a.cs", 5);
        store.Toggle(@"C:\b.cs", 9);
        store.Breakpoints[1].AdapterId = 42;
        Assert.Same(store.Breakpoints[1], store.FindHit(new HashSet<int> { 42 }, @"C:\a.cs", 5));
        Assert.Same(store.Breakpoints[0], store.FindHit(new HashSet<int>(), @"C:\A.CS", 5));
        store.Breakpoints[0].BoundLine = 6;
        Assert.Null(store.FindHit(new HashSet<int>(), @"C:\a.cs", 5));
        Assert.Same(store.Breakpoints[0], store.FindHit(new HashSet<int>(), @"C:\a.cs", 6));
        store.Breakpoints[0].Enabled = false;
        Assert.Null(store.FindHit(new HashSet<int>(), @"C:\a.cs", 6));
    }

    [Fact]
    public void AChangeMatchesByIdOrBySourceLine()
    {
        var store = new BreakpointStore(directory);
        store.Toggle(@"C:\a.cs", 5);
        store.Toggle(@"C:\b.cs", 9);
        store.Breakpoints[1].AdapterId = 42;
        Assert.Equal([store.Breakpoints[0]], store.Matching(new DapBreakpoint(null, true, null, 5, @"C:\A.cs")));
        Assert.Equal([store.Breakpoints[1]], store.Matching(new DapBreakpoint(42, true, null, 100, "")));
        Assert.Empty(store.Matching(new DapBreakpoint(7, true, null, 5, @"C:\other.cs")));
    }

    [Fact]
    public void SessionStateResets()
    {
        var store = new BreakpointStore(directory);
        store.Toggle(@"C:\a.cs", 5);
        var breakpoint = store.Breakpoints[0];
        breakpoint.Apply(new DapBreakpoint(3, true, null, 6, null));
        breakpoint.HitCount = 4;
        Assert.Equal(("Bound", 3, 6), (breakpoint.Status, breakpoint.AdapterId, breakpoint.BoundLine));
        store.ResetForSession();
        Assert.Equal((0, null, null), (breakpoint.HitCount, breakpoint.AdapterId, breakpoint.BoundLine));
        store.MarkPending();
        Assert.Equal("Pending", breakpoint.Status);
    }

    [Fact]
    public void BreakpointAnswersSetTheStatusTheUserSees()
    {
        var breakpoint = new BreakpointViewModel { Path = @"C:\a.cs", Line = 5 };
        breakpoint.Apply(new DapBreakpoint(null, false, null, 0, null));
        Assert.Equal(("Unbound · symbols not loaded", null, null), (breakpoint.Status, breakpoint.AdapterId, breakpoint.BoundLine));
        breakpoint.Apply(new DapBreakpoint(1, false, "no code here", 7, null));
        Assert.Equal(("no code here", 1, 7), (breakpoint.Status, breakpoint.AdapterId, breakpoint.BoundLine));
        breakpoint.ApplyChange(new DapBreakpoint(1, false, null, 0, ""));
        Assert.Equal(("Unbound", 7), (breakpoint.Status, breakpoint.BoundLine));
        breakpoint.ApplyChange(new DapBreakpoint(1, true, null, 8, ""));
        Assert.Equal(("Bound", 8), (breakpoint.Status, breakpoint.BoundLine));
    }
}
