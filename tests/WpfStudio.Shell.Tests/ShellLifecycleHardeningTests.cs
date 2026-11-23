using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.App.Features.Documents;
using WpfStudio.App.Features.Git;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Git;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Shell.Tests;

public sealed class ShellLifecycleHardeningTests
{
    [Fact]
    public async Task CloseDocumentUpdatesActiveDocumentImmediatelyBeforeAwaitsComplete()
    {
        await using var test = new ShellTestContext();
        string first = await test.CreateFileAsync("First.cs", "class First { }");
        string second = await test.CreateFileAsync("Second.cs", "class Second { }");
        await test.Shell.OpenDocumentAsync(first);
        var firstEditor = test.Shell.ActiveDocument!;
        await test.Shell.OpenDocumentAsync(second);
        var secondEditor = test.Shell.ActiveDocument!;

        EditorViewModel? observedActiveDuringClose = null;
        test.Shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.ActiveDocument) && observedActiveDuringClose is null)
                observedActiveDuringClose = test.Shell.ActiveDocument;
        };

        Assert.True(await test.Shell.CloseDocumentAsync(secondEditor));
        Assert.Same(firstEditor, observedActiveDuringClose);
        Assert.Same(firstEditor, test.Shell.ActiveDocument);
        Assert.DoesNotContain(secondEditor, test.Shell.Documents);
    }

    [Fact]
    public async Task DebugAsyncHandlesStartupProjectBecomingNullDuringSavePrompt()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Program.cs", "class Program { }");
        await test.Shell.OpenDocumentAsync(path);
        test.Shell.ActiveDocument!.State.Content = "class Program { int x; }";
        test.Shell.StartupProject = new WorkspaceProject(
            "app",
            "App",
            Path.Combine(test.Root, "App.csproj"),
            "net10.0-windows",
            null,
            true,
            []);
        test.Dialogs.WhileAsking = () =>
        {
            test.Shell.StartupProject = null;
            return Task.CompletedTask;
        };
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Discard);

        // Should return cleanly without NullReferenceException when StartupProject becomes null across awaits.
        await test.Shell.DebugCommand.ExecuteAsync(null);
        Assert.Null(test.Shell.StartupProject);
    }

    [Fact]
    public async Task RenameResourceCommandHandlesMissingResourceWithoutThrowing()
    {
        await using var test = new ShellTestContext();
        string xamlPath = await test.CreateFileAsync("App.xaml", "<ResourceDictionary />");
        test.Shell.WpfExplorer.SetIndex(new WpfIndexSnapshot([], [], [], [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        test.Shell.WpfExplorer.SelectedItem = new WpfItem("Resource", "MissingKey", xamlPath, 1, "MissingKey");

        await test.Shell.RenameResourceCommand.ExecuteAsync(null);
        Assert.Contains("no longer in the WPF index", test.Shell.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoSaveCoordinatorSerializesConcurrentRunsAndStopCancelsTimer()
    {
        string root = Path.Combine(Path.GetTempPath(), "WpfStudio-AutoSaveTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "Doc.cs");
            await File.WriteAllTextAsync(path, "initial");
            var store = new DocumentStore(root);
            var state = await store.OpenAsync(path);
            state.Content = "edited";

            var host = new FakeAutoSaveHost([]);
            var preferences = new EditorPreferences(new FontCatalog()) { AutoSave = true, AutoSaveDelaySeconds = 1 };
            var dispatcher = new InlineDispatcher();
            var messenger = new WeakReferenceMessenger();
            using var coordinator = new AutoSaveCoordinator(host, store, preferences, dispatcher, messenger) { IsEnabled = true };

            // Concurrent RunNowAsync calls must serialize without throwing.
            await Task.WhenAll(coordinator.RunNowAsync(), coordinator.RunNowAsync());

            // SuspendAsync must complete cleanly and hold saving until disposed.
            using (await coordinator.SuspendAsync())
            {
                Assert.True(coordinator.IsHeld);
            }
            Assert.False(coordinator.IsHeld);

            // Schedule then Stop must cancel/dispose the pending timer and hold permanently.
            coordinator.Schedule();
            coordinator.Stop();
            Assert.True(coordinator.IsHeld);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void KnownSourceFileWatcherNormalizesDriveRootPrefixesAndSynchronizesMembershipCheck()
    {
        Assert.Equal(@"C:\", KnownSourceFileWatcher.DirectoryPrefix(@"C:\"));
        Assert.Equal(@"C:\Repo\", KnownSourceFileWatcher.DirectoryPrefix(@"C:\Repo"));

        string root = Path.Combine(Path.GetTempPath(), "WpfStudio-WatcherTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "Main.cs");
            File.WriteAllText(file, "class Main {}");
            using var watcher = new KnownSourceFileWatcher([file], _ => { }, fallbackInterval: TimeSpan.Zero);

            Assert.True(watcher.Ignored(Path.Combine(root, "obj", "Debug", "Generated.cs")));
            Assert.True(watcher.Ignored(Path.Combine(root, "bin", "Debug", "App.dll")));
            Assert.False(watcher.Ignored(file));

            // Concurrent MembershipChanged calls must not race on _shapeCheckedUtcTicks.
            var results = Enumerable.Range(0, 8)
                .AsParallel()
                .Select(_ => watcher.MembershipChanged())
                .ToArray();
            Assert.All(results, changed => Assert.False(changed));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void OpenDocumentWatcherCreationAndDisposalAreSafe()
    {
        string root = Path.Combine(Path.GetTempPath(), "WpfStudio-OpenDocWatcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "File.cs");
            File.WriteAllText(file, "class File {}");
            using var watcher = new OpenDocumentWatcher(() => throw new InvalidOperationException("Callback fault"));
            watcher.Watch([file]);
            watcher.Watch([]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task EditorViewModelClearsStaleBlameWhenFileBecomesUntracked()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Tracked.cs", "class Tracked { }");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;

        // Simulate previously populated blame state on a tracked file, then refresh against a non-git directory.
        typeof(EditorViewModel).GetProperty(nameof(EditorViewModel.Blame))!
            .SetValue(editor, (IReadOnlyList<BlameLine>)[new BlameLine("abc1234", "Author", "author@example.com", DateTimeOffset.UtcNow, "Init")]);
        typeof(EditorViewModel).GetProperty(nameof(EditorViewModel.BlameVersion))!
            .SetValue(editor, 1L);
        typeof(EditorViewModel).GetProperty(nameof(EditorViewModel.GitBase))!
            .SetValue(editor, new GitFileBase(test.Root, "Tracked.cs", "class Tracked { }", IsTracked: true, HeadCommit: "abc1234", Remote: null));

        var notified = 0;
        editor.GitChanged += () => notified++;

        await editor.RefreshGitAsync(test.Shell.Features.GitService);

        Assert.Empty(editor.Blame);
        Assert.Equal(-1, editor.BlameVersion);
        Assert.True(notified > 0);
    }

    private sealed class FakeAutoSaveHost(IReadOnlyList<EditorViewModel> documents) : IAutoSaveHost
    {
        public IReadOnlyList<EditorViewModel> Documents { get; } = documents;
        public CancellationToken Lifetime => CancellationToken.None;
        public bool IsReviewOpen => false;
        public void ReportStatus(string status) { }
        public void AppendOutput(string line) { }
        public void AutoSaveCompleted() { }
        public void ExternalChangeDetected() { }
    }
}
