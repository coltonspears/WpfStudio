using System.Collections.Concurrent;
using WpfStudio.App.Services;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed class ProjectXamlAnalysisLifecycleTests
{
    [Fact]
    public async Task DebounceCoalescesBurstAndRunsOnlyNewestGeneration()
    {
        var scans = new List<long>();
        var failures = new List<Exception>();
        using var scheduler = new ProjectXamlAnalysisScheduler((generation, _) =>
        { scans.Add(generation); return Task.CompletedTask; }, (_, error) => failures.Add(error), TimeSpan.FromMilliseconds(25));
        for (int index = 0; index < 15; index++) _ = scheduler.Schedule();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(scans); Assert.Empty(failures); Assert.True(scheduler.IsCurrent(scans[0]));
    }

    [Fact]
    public async Task LateUncooperativeReplyCannotPublishAndNewScanRemainsSerial()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<long>();
        int running = 0, calls = 0;
        ProjectXamlAnalysisScheduler? scheduler = null;
        using (scheduler = new(async (generation, _) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref running));
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; }
            if (scheduler!.IsCurrent(generation)) published.Add(generation);
            Interlocked.Decrement(ref running);
        }, (_, error) => throw new Xunit.Sdk.XunitException(error.ToString()), TimeSpan.Zero))
        {
            var first = scheduler.Schedule(immediate: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = scheduler.Schedule(immediate: true);
            Assert.Equal(1, calls);
            release.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, calls); Assert.Single(published); Assert.True(scheduler.IsCurrent(published[0]));
        }
    }

    [Fact]
    public async Task InvalidatedFailureIsNotReportedIntoNewContext()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        using var scheduler = new ProjectXamlAnalysisScheduler(async (_, _) =>
        { entered.SetResult(); await release.Task; throw new IOException("old workspace"); }, (_, error) => errors.Add(error));
        var pending = scheduler.Schedule(immediate: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        scheduler.Invalidate();
        release.SetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task DisposingCancelsQueuedAnalysis()
    {
        int calls = 0;
        var scheduler = new ProjectXamlAnalysisScheduler((_, _) => { calls++; return Task.CompletedTask; }, (_, _) => { }, TimeSpan.FromSeconds(2));
        var pending = scheduler.Schedule(); scheduler.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task WatcherIncludesExternalLinkedSourcesAndIgnoresGeneratedOutput()
    {
        await using var fixture = new WatcherFixture();
        var project = await fixture.WriteAsync("app/Fixture.csproj", "<Project />");
        var linked = await fixture.WriteAsync("linked/Shared.xaml", "<Grid />");
        var events = new ConcurrentQueue<KnownSourceChange>();
        using var watcher = new KnownSourceFileWatcher([project, linked], events.Enqueue, TimeSpan.Zero);
        await File.WriteAllTextAsync(linked, "<StackPanel />");
        await WaitAsync(() => events.Any(change => change.Path == linked));
        await fixture.WriteAsync("app/obj/Generated.cs", "class Generated { }");
        await fixture.WriteAsync("app/Logs/session.txt", "ordinary application output");
        await Task.Delay(150);
        // A single write can yield several Changed notifications, including
        // callbacks arriving after the first one was observed. Keep the whole
        // history: every event must belong to the linked source; generated and
        // ordinary output must never appear or trigger project reevaluation.
        Assert.NotEmpty(events);
        Assert.All(events, change =>
        {
            Assert.Equal(linked, change.Path);
            Assert.False(change.RequiresReload);
        });
    }

    [Theory]
    [InlineData("NewModel.cs")]
    [InlineData("NewView.xaml")]
    [InlineData("Directory.Build.props")]
    public async Task NewProjectInputsRequireReevaluation(string name)
    {
        await using var fixture = new WatcherFixture();
        var project = await fixture.WriteAsync("app/Fixture.csproj", "<Project />");
        var events = new ConcurrentQueue<KnownSourceChange>();
        using var watcher = new KnownSourceFileWatcher([project], events.Enqueue, TimeSpan.Zero);
        var added = await fixture.WriteAsync("app/" + name, "new input");
        await WaitAsync(() => events.Any(change => change.Path == added && change.RequiresReload));
    }

    [Fact]
    public async Task AtomicReplacementOfKnownFileDoesNotRequireReload()
    {
        await using var fixture = new WatcherFixture();
        var source = await fixture.WriteAsync("app/ViewModel.cs", "class Before { }");
        var events = new ConcurrentQueue<KnownSourceChange>();
        using var watcher = new KnownSourceFileWatcher([source], events.Enqueue, TimeSpan.Zero);
        var temporary = await fixture.WriteAsync("app/replacement.tmp", "class After { }");
        File.Move(temporary, source, overwrite: true);
        await WaitAsync(() => events.Any(change => change.Path == source));
        await Task.Delay(300);
        Assert.DoesNotContain(events, change => change.RequiresReload);
    }

    [Fact]
    public async Task DeletedSourceDirectoryAndOverflowRequireReload()
    {
        await using var fixture = new WatcherFixture();
        var project = await fixture.WriteAsync("app/Fixture.csproj", "<Project />");
        var source = await fixture.WriteAsync("app/Views/View.xaml", "<Grid />");
        var events = new ConcurrentQueue<KnownSourceChange>();
        using var watcher = new KnownSourceFileWatcher([project, source], events.Enqueue, TimeSpan.Zero);
        Directory.Delete(Path.GetDirectoryName(source)!, recursive: true);
        await WaitAsync(() => events.Any(change => change.RequiresReload));
        while (events.TryDequeue(out _)) { }
        watcher.Reconcile();
        Assert.Contains(events, change => change.Path is null && change.RequiresReload);
        await fixture.WriteAsync("app/Views/Replacement.xaml", "<Button />");
        await WaitAsync(() => events.Any(change => change.Path?.EndsWith("Replacement.xaml", StringComparison.Ordinal) == true && change.RequiresReload));
    }

    [Fact]
    public async Task AncestorConfigurationWatchIsExactAndNonrecursive()
    {
        await using var fixture = new WatcherFixture();
        var project = await fixture.WriteAsync("app/Fixture.csproj", "<Project />");
        var configuration = Path.Combine(fixture.Root, "Directory.Build.props");
        var events = new ConcurrentQueue<KnownSourceChange>();
        using var watcher = new KnownSourceFileWatcher([project], events.Enqueue, TimeSpan.Zero, contextFiles: [configuration]);
        await fixture.WriteAsync("unrelated/Other.cs", "class Other { }");
        await Task.Delay(100); Assert.Empty(events);
        await File.WriteAllTextAsync(configuration, "<Project />");
        await WaitAsync(() => events.Any(change => change.Path == configuration && change.RequiresReload));
    }

    [Fact]
    public async Task ClosedXamlIsNotOpenedByUnavailableBackgroundAnalysis()
    {
        await using var test = new ShellTestContext();
        var view = await test.CreateFileAsync("Views/Closed.xaml", "<Grid />");
        var project = await test.CreateFileAsync("Fixture.csproj", "<Project />");
        test.Shell.Workspace = new(project, "Test", [new("id", "Fixture", project, null, null, false, [new(view, "Closed.xaml", "Page")])], []);
        await test.Shell.RefreshProjectXamlAnalysisAsync();
        Assert.Empty(test.Shell.Documents); Assert.Null(test.Store.Find(view));
        Assert.Contains("disconnected", test.Shell.XamlAnalysisStatus);
        Assert.False(test.Shell.IsProjectXamlAnalysisRunning);
    }

    [Fact]
    public async Task ProjectMembershipChangeClearsEditorIssuesButPreservesBufferAndSelectedContext()
    {
        await using var test = new ShellTestContext();
        var view = await test.CreateFileAsync("View.xaml", "<Grid />");
        var project = await test.CreateFileAsync("Fixture.csproj", "<Project />");
        var context = new WorkspaceProject("id", "Fixture", project, "net10.0-windows", null, false, [new(view, "View.xaml", "Page")]);
        test.Shell.Workspace = new(project, "Test", [context], []);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        editor.State.Content = "<Grid><!-- unsaved --></Grid>";
        var version = editor.State.Version;
        var revision = editor.XamlContextRevision;
        editor.Diagnostics.Add(new("XAMLBIND001", "Old type assumption", "Warning", view, 1, 2, 1, 4, project, "Fixture"));

        await test.CreateFileAsync("NewModel.cs", "class NewModel { }");

        await WaitAsync(() => test.Shell.XamlAnalysisStatus.Contains("Reload the workspace", StringComparison.Ordinal));
        Assert.Empty(editor.Diagnostics);
        Assert.Empty(test.Shell.WpfIssues);
        Assert.Equal("<Grid><!-- unsaved --></Grid>", editor.State.Content);
        Assert.Equal(version, editor.State.Version);
        Assert.True(editor.XamlContextRevision > revision);
        Assert.Equal(project, editor.XamlProjectPath);
        await test.Shell.RefreshProjectXamlAnalysisAsync();
        Assert.Contains("Reload the workspace", test.Shell.XamlAnalysisStatus);
    }

    [Fact]
    public async Task DisposedWatcherDoesNotObserveSubsequentWrites()
    {
        await using var fixture = new WatcherFixture();
        var source = await fixture.WriteAsync("app/View.xaml", "<Grid />");
        var events = new ConcurrentQueue<KnownSourceChange>();
        var watcher = new KnownSourceFileWatcher([source], events.Enqueue, TimeSpan.Zero);
        watcher.Dispose();
        await File.WriteAllTextAsync(source, "<Button />");
        await Task.Delay(150);
        Assert.Empty(events);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class WatcherFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WpfStudio-Watcher-" + Guid.NewGuid().ToString("N"));
        public async Task<string> WriteAsync(string relative, string text)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text); return path;
        }
        public ValueTask DisposeAsync() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); return ValueTask.CompletedTask; }
    }
}
