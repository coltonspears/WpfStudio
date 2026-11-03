using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Tests;

public sealed class WorkspaceEditGuardTests
{
    [Fact]
    public async Task FinalContextGuardRunsAfterResolutionAndRejectsEveryMutationAndCreation()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "View.xaml");
        string createdPath = Path.Combine(directory, "Created.xaml");
        await File.WriteAllTextAsync(path, "before");
        var store = new DocumentStore(directory);
        var transaction = new WorkspaceEditTransaction(store);
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ApplyAsync(
            [new(path, "before", "after", "edit"), new(createdPath, "", "new content", "create")], canApply: () =>
            {
                calls++;
                // The disk-backed file has already been opened and checked asynchronously.
                Assert.Equal("before", store.Find(path)!.Content);
                Assert.Null(store.Find(createdPath));
                return false;
            }));
        Assert.Equal(1, calls);
        Assert.Equal("before", store.Find(path)!.Content);
        Assert.False(store.Find(path)!.IsDirty);
        Assert.Null(store.Find(createdPath));
        Assert.False(File.Exists(createdPath));
        Assert.False(transaction.CanUndo);
    }

    [Fact]
    public async Task CreatedFilesArePreparedAsNewBuffersAndRefuseToOverwrite()
    {
        string directory = CreateDirectory();
        string created = Path.Combine(directory, "Customer.cs");
        var store = new DocumentStore(directory);
        var transaction = new WorkspaceEditTransaction(store);
        var edit = new WpfStudio.Contracts.DocumentEdits(created, 0, [new(0, 0, "class Customer { }")], Create: true);
        var change = Assert.Single(await transaction.PrepareAsync(new([edit], [])));
        Assert.Equal(("", "class Customer { }"), (change.Before, change.After));
        await transaction.ApplyAsync([change]);
        Assert.Equal("class Customer { }", store.Find(created)!.Content);
        Assert.False(File.Exists(created));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.PrepareAsync(new([edit], [])));
        Assert.Equal([created], await transaction.UndoAsync());
        Assert.Null(store.Find(created));
    }

    [Fact]
    public async Task UndoWritesBoundDocumentsOnTheCallersThreadWhileRecoveryIsBusy()
    {
        string directory = CreateDirectory();
        string created = Path.Combine(directory, "Created.cs"), existingPath = Path.Combine(directory, "Existing.cs");
        await File.WriteAllTextAsync(existingPath, "before");
        var store = new DocumentStore(directory);
        var existing = await store.OpenAsync(existingPath);
        var transaction = new WorkspaceEditTransaction(store);
        // The created file comes first, so undo awaits the recovery gate before it restores the existing document.
        await transaction.ApplyAsync([new(created, "", "class Created { }", "create"), new(existingPath, "before", "after", "edit")]);

        var writer = -1;
        existing.ContentChanged += (_, _) => writer = Environment.CurrentManagedThreadId;
        var recoveryGate = (SemaphoreSlim)typeof(DocumentStore).GetField("_recoveryGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
        await recoveryGate.WaitAsync(); // A recovery write is in progress.
        using var ui = new SingleThreadContext();
        var undo = ui.RunAsync(() => transaction.UndoAsync());
        await Task.Delay(100);
        recoveryGate.Release();

        Assert.Equal([created], await undo.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("before", existing.Content);
        Assert.Equal(ui.ThreadId, writer);
    }

    /// <summary>Runs continuations on one dedicated thread, as the WPF dispatcher does.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;
        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true };
            _thread.Start();
        }
        public int ThreadId => _thread.ManagedThreadId;
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { result.SetResult(await action()); }
                catch (Exception ex) { result.SetException(ex); }
            }, null);
            return result.Task;
        }
        public void Dispose() { _queue.CompleteAdding(); _thread.Join(TimeSpan.FromSeconds(5)); _queue.Dispose(); }
    }

    [Fact]
    public async Task AcceptedContextGuardKeepsNormalApplyAndUndoBehavior()
    {
        string directory = CreateDirectory();
        var store = new DocumentStore(directory);
        var document = store.Create(Path.Combine(directory, "View.xaml"), "before");
        var transaction = new WorkspaceEditTransaction(store);
        int calls = 0;
        await transaction.ApplyAsync([new(document.Path, "before", "after", "edit")], canApply: () => ++calls == 1);
        Assert.Equal(1, calls);
        Assert.Equal("after", document.Content);
        Assert.True(transaction.CanUndo);
        await transaction.UndoAsync();
        Assert.Equal("before", document.Content);
    }

    [Fact]
    public async Task StaleBufferIsRejectedBeforeTheContextGuard()
    {
        string directory = CreateDirectory();
        var store = new DocumentStore(directory);
        var document = store.Create(Path.Combine(directory, "View.xaml"), "newer");
        int calls = 0;
        var transaction = new WorkspaceEditTransaction(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ApplyAsync(
            [new(document.Path, "before", "after", "edit")], canApply: () => { calls++; return true; }));
        Assert.Equal(0, calls);
        Assert.Equal("newer", document.Content);
        Assert.False(transaction.CanUndo);
    }

    [Fact]
    public async Task UnchangedPrerequisitesAreCheckedButDoNotBecomeUndoDependencies()
    {
        string directory = CreateDirectory();
        string view = Path.Combine(directory, "View.xaml"), code = Path.Combine(directory, "View.xaml.cs");
        await File.WriteAllTextAsync(view, "<View />");
        await File.WriteAllTextAsync(code, "class View {}");
        var store = new DocumentStore(directory);
        var source = await store.OpenAsync(view);
        long version = source.Version;
        var transaction = new WorkspaceEditTransaction(store);
        await transaction.ApplyAsync([new(view, source.Content, source.Content, "prerequisite"),
            new(code, "class View {}", "class View { void Click() {} }", "handler")]);
        Assert.Equal(version, source.Version);
        Assert.False(source.IsDirty);
        source.Content = "<View Tag=\"later edit\" />";
        await transaction.UndoAsync();
        Assert.Equal("class View {}", store.Find(code)!.Content);
        Assert.Equal("<View Tag=\"later edit\" />", source.Content);
        Assert.False(transaction.CanUndo);
    }

    [Fact]
    public async Task ChangedPrerequisiteOnDiskRejectsCrossFileMutation()
    {
        string directory = CreateDirectory();
        string view = Path.Combine(directory, "View.xaml"), code = Path.Combine(directory, "View.xaml.cs");
        await File.WriteAllTextAsync(view, "<View />");
        await File.WriteAllTextAsync(code, "class View {}");
        var store = new DocumentStore(directory);
        var source = await store.OpenAsync(view);
        await File.WriteAllTextAsync(view, "<View Tag=\"external\" />");
        var transaction = new WorkspaceEditTransaction(store);
        await Assert.ThrowsAsync<ExternalFileChangedException>(() => transaction.ApplyAsync([
            new(view, source.Content, source.Content, "prerequisite"),
            new(code, "class View {}", "class View { void Click() {} }", "handler")]));
        Assert.Equal("class View {}", (await store.OpenAsync(code)).Content);
        Assert.False(transaction.CanUndo);
    }

    [Fact]
    public async Task UnchangedExistingDocumentDoesNotCreateUndoEntryAndEmptyFileCreationStillWorks()
    {
        var store = new DocumentStore(CreateDirectory());
        var transaction = new WorkspaceEditTransaction(store);
        var existing = store.Create(Path.Combine(CreateDirectory(), "Existing.cs"), "existing");
        await transaction.ApplyAsync([new(existing.Path, "existing", "existing", "no change")]);
        Assert.False(transaction.CanUndo);
        string absent = Path.Combine(CreateDirectory(), "Absent.cs");
        await transaction.ApplyAsync([new(absent, "", "", "create empty")]);
        Assert.NotNull(store.Find(absent));
        Assert.False(File.Exists(absent));
        Assert.True(transaction.CanUndo);
        await transaction.UndoAsync();
        Assert.Null(store.Find(absent));
        Assert.False(transaction.CanUndo);
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
