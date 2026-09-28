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
        transaction.Undo();
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
        transaction.Undo();
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
        transaction.Undo();
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
