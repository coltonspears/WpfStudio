using System.Reflection;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Tests;

public sealed class WorkspaceEditUndoTests
{
    [Fact]
    public async Task UndoRestoresAllBuffersAndPopsStackSynchronouslyBeforeRecoveryCleanup()
    {
        string directory = CreateDirectory();
        try
        {
            string createdPath = Path.Combine(directory, "Created.cs");
            string existingPath = Path.Combine(directory, "Existing.cs");
            await File.WriteAllTextAsync(existingPath, "original");
            var store = new DocumentStore(directory);
            var existing = await store.OpenAsync(existingPath);
            var transaction = new WorkspaceEditTransaction(store);

            await transaction.ApplyAsync([
                new(createdPath, "", "class Created { }", "create"),
                new(existingPath, "original", "edited", "edit")
            ]);
            Assert.True(transaction.CanUndo);
            Assert.NotNull(store.Find(createdPath));
            Assert.Equal("edited", existing.Content);

            // Hold _recoveryGate so DeleteRecoveryAsync blocks until we inspect in-memory state.
            var recoveryGate = (SemaphoreSlim)typeof(DocumentStore)
                .GetField("_recoveryGate", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(store)!;
            await recoveryGate.WaitAsync();
            try
            {
                var undoTask = transaction.UndoAsync();

                // Even while DeleteRecoveryAsync is blocked, all in-memory state must already be restored atomically.
                Assert.Null(store.Find(createdPath));
                Assert.Equal("original", existing.Content);
                Assert.False(transaction.CanUndo);
                Assert.False(undoTask.IsCompleted);

                recoveryGate.Release();
                var removed = await undoTask.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal([createdPath], removed);
            }
            catch
            {
                if (recoveryGate.CurrentCount == 0) recoveryGate.Release();
                throw;
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public async Task UndoCompletesCleanlyWhenRecoveryDeletionIsCancelled()
    {
        string directory = CreateDirectory();
        try
        {
            string createdPath = Path.Combine(directory, "Created.cs");
            string existingPath = Path.Combine(directory, "Existing.cs");
            await File.WriteAllTextAsync(existingPath, "before");
            var store = new DocumentStore(directory);
            var existing = await store.OpenAsync(existingPath);
            var transaction = new WorkspaceEditTransaction(store);

            await transaction.ApplyAsync([
                new(createdPath, "", "new content", "create"),
                new(existingPath, "before", "after", "edit")
            ]);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Best-effort recovery deletion must not throw or leave partial undo state when cancelled.
            var removed = await transaction.UndoAsync(cts.Token);
            Assert.Equal([createdPath], removed);
            Assert.Null(store.Find(createdPath));
            Assert.Equal("before", existing.Content);
            Assert.False(transaction.CanUndo);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public async Task UndoValidationFailureLeavesAllBuffersAndUndoStackIntact()
    {
        string directory = CreateDirectory();
        try
        {
            string createdPath = Path.Combine(directory, "Created.cs");
            string existingPath = Path.Combine(directory, "Existing.cs");
            await File.WriteAllTextAsync(existingPath, "before");
            var store = new DocumentStore(directory);
            var existing = await store.OpenAsync(existingPath);
            var transaction = new WorkspaceEditTransaction(store);

            await transaction.ApplyAsync([
                new(createdPath, "", "new content", "create"),
                new(existingPath, "before", "after", "edit")
            ]);

            // Newer edit on the second file invalidates undo of the transaction.
            existing.Content = "newer edit";

            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.UndoAsync());
            Assert.True(transaction.CanUndo);
            Assert.NotNull(store.Find(createdPath));
            Assert.Equal("newer edit", existing.Content);
        }
        finally { TryDeleteDirectory(directory); }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }
}
