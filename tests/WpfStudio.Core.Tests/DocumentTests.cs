using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Tests;

public class DocumentTests
{
    private static string DirectoryPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "WpfStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void EverySourceOfTheDirtyStateAndTitleNotifiesThem()
    {
        var document = new DocumentState(Path.Combine(Path.GetTempPath(), "Notified.cs"), "saved", diskHash: "first");
        var raised = new List<string?>();
        document.PropertyChanged += (_, args) => raised.Add(args.PropertyName);
        void NotifiesBoth(Action change, bool dirty)
        {
            raised.Clear();
            change();
            Assert.Contains(nameof(DocumentState.IsDirty), raised);
            Assert.Contains(nameof(DocumentState.Title), raised);
            Assert.Equal(dirty, document.IsDirty);
        }

        NotifiesBoth(() => document.Content = "edited", dirty: true);
        // Only the saved text changes here, and only the hash in the next one.
        NotifiesBoth(() => document.MarkSavedSnapshot("edited", "first"), dirty: false);
        NotifiesBoth(() => document.KeepEditorVersion("edited", null), dirty: true);
        NotifiesBoth(() => document.MarkSaved("second"), dirty: false);
        NotifiesBoth(() => document.KeepEditorVersion("on disk", "third"), dirty: true);

        raised.Clear();
        document.HasExternalChange = true;
        Assert.Contains(nameof(DocumentState.Title), raised);
        Assert.EndsWith("\u26a0", document.Title);
    }

    [Theory]
    [InlineData("utf-16", "\ufeffcaf\u00e9")]
    [InlineData("utf-8", "\ufeffcaf\u00e9")]
    [InlineData("utf-8", "caf\u00e9")]
    public async Task SaveWritesThePreambleFollowedByTheEncodedText(string encodingName, string initial)
    {
        var directory = DirectoryPath();
        var path = Path.Combine(directory, "View.cs");
        await File.WriteAllBytesAsync(path, Encoding.GetEncoding(encodingName).GetBytes(initial));
        var store = new DocumentStore(directory);
        var document = await store.OpenAsync(path);
        document.Content = "na\u00efve";
        await store.SaveAsync(document);
        byte[] expected = [.. document.Encoding.GetPreamble(), .. document.Encoding.GetBytes("na\u00efve")];
        Assert.Equal(initial.StartsWith('\ufeff'), document.Encoding.GetPreamble().Length > 0);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Equal(DocumentStore.Hash(expected), document.DiskHash);
    }

    [Fact]
    public async Task SavePreservesUtf16BomAndLineEndingsAndDetectsExternalChanges()
    {
        var directory = DirectoryPath();
        var path = Path.Combine(directory, "View.cs");
        await File.WriteAllTextAsync(path, "one\r\ntwo\r\n", Encoding.Unicode);
        var store = new DocumentStore(directory);
        var document = await store.OpenAsync(path);
        document.Content += "three\r\n";
        await store.SaveAsync(document);
        Assert.False(document.IsDirty);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()));
        Assert.Equal("one\r\ntwo\r\nthree\r\n", await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "external");
        document.Content = "local";
        await Assert.ThrowsAsync<ExternalFileChangedException>(() => store.SaveAsync(document));
        Assert.Equal("external", await File.ReadAllTextAsync(path));
        await store.SaveAsync(document, true);
        Assert.Equal("local", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AFileAnotherProgramHasOpenForWritingCanStillBeOpenedCheckedAndReloaded()
    {
        var directory = DirectoryPath();
        var path = Path.Combine(directory, "View.cs");
        await File.WriteAllTextAsync(path, "one");
        var store = new DocumentStore(directory);
        // The other program allows readers, as most do while they write; a read that insists on being alone would fail here.
        await using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var document = await store.OpenAsync(path);
            Assert.Equal("one", document.Content);
            writer.SetLength(0);
            await writer.WriteAsync(Encoding.UTF8.GetBytes("two"));
            await writer.FlushAsync();
            Assert.True(await store.IsExternallyChangedAsync(document));
            Assert.Equal("two", (await store.ReadDiskAsync(document))!.Value.Text);
            await store.ReloadAsync(document);
            Assert.Equal("two", document.Content);
            Assert.False(document.IsDirty);
        }
    }

    [Fact]
    public async Task ARecoveryRecordIsRewrittenOnlyWhenItsDocumentChangedOrTheRecordIsGone()
    {
        var directory = DirectoryPath();
        var store = new DocumentStore(directory);
        var document = store.Create(Path.Combine(directory, "draft.cs"), "first");
        await store.WriteRecoveryAsync();
        var record = Assert.Single(Directory.GetFiles(Path.Combine(directory, "Recovery"), "*.json"));
        var written = (await store.ReadRecoveryAsync())[0].SavedAt;

        await store.WriteRecoveryAsync();
        Assert.Equal(written, (await store.ReadRecoveryAsync())[0].SavedAt);

        document.Content = "second";
        await store.WriteRecoveryAsync();
        var rewritten = Assert.Single(await store.ReadRecoveryAsync());
        Assert.Equal("second", rewritten.Content);
        Assert.NotEqual(written, rewritten.SavedAt);

        // A record removed behind the store's back is written again, and one the store removed is too.
        File.Delete(record);
        await store.WriteRecoveryAsync();
        Assert.Equal("second", Assert.Single(await store.ReadRecoveryAsync()).Content);
        await store.DeleteRecoveryAsync(document.Path);
        await store.WriteRecoveryAsync();
        Assert.Equal("second", Assert.Single(await store.ReadRecoveryAsync()).Content);
    }

    [Fact]
    public async Task EmptyNewDocumentIsDirtyAndRecoveryIgnoresCorruptRecords()
    {
        var directory = DirectoryPath();
        var store = new DocumentStore(directory);
        var document = store.Create(Path.Combine(directory, "empty.cs"));
        Assert.True(document.IsDirty);
        document.Content = "unsaved";
        await store.WriteRecoveryAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "Recovery", "broken.json"), "{");
        Assert.Equal("unsaved", Assert.Single(await store.ReadRecoveryAsync()).Content);
        await store.SaveAsync(document);
        Assert.Empty(await store.ReadRecoveryAsync());
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task SavingAFileThatIsNotUtf8KeepsTheLettersOutsideAscii()
    {
        var directory = DirectoryPath();
        var path = Path.Combine(directory, "legacy.cs");
        byte[] original = [.. "// "u8, 0xA9, .. " M"u8, 0xFC, .. "ller\nclass C { }\n"u8];
        await File.WriteAllBytesAsync(path, original);
        var store = new DocumentStore(directory);
        var document = await store.OpenAsync(path);

        document.Content += "// added\n";
        await store.SaveAsync(document);

        var saved = await File.ReadAllBytesAsync(path);
        Assert.Equal(original, saved.AsSpan(0, original.Length).ToArray());
        Assert.False(await store.IsExternallyChangedAsync(document));
        Assert.Equal(document.Content, (await store.ReadDiskAsync(document))!.Value.Text);

        // Text the code page cannot hold turns the file into marked UTF-8, and the document follows.
        document.Content += "// \u4e2d\u6587\n";
        await store.SaveAsync(document);
        Assert.True((await File.ReadAllBytesAsync(path)).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        await store.ReloadAsync(document);
        Assert.EndsWith("// \u4e2d\u6587\n", document.Content);
        Assert.DoesNotContain('\uFFFD', document.Content);
    }

    [Fact]
    public async Task ReloadUsesTheNewEncoding()
    {
        var directory = DirectoryPath();
        var path = Path.Combine(directory, "file.cs");
        await File.WriteAllTextAsync(path, "utf8", new UTF8Encoding(false));
        var store = new DocumentStore(directory);
        var document = await store.OpenAsync(path);
        await File.WriteAllTextAsync(path, "utf16", Encoding.Unicode);
        await store.ReloadAsync(document);
        document.Content += " updated";
        await store.SaveAsync(document);
        Assert.True((await File.ReadAllBytesAsync(path)).AsSpan().StartsWith(Encoding.Unicode.GetPreamble()));
    }

    [Fact]
    public async Task ApplyingAndUndoingWorkspaceEditsPreservesNewerChanges()
    {
        var directory = DirectoryPath();
        var store = new DocumentStore(directory);
        var document = store.Create(Path.Combine(directory, "file.cs"), "before");
        var transaction = new WorkspaceEditTransaction(store);
        await transaction.ApplyAsync([new FileChange(document.Path, "before", "after", "rename")]);
        Assert.Equal("after", document.Content);
        document.Content = "newer";
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.UndoAsync());
        document.Content = "after";
        await transaction.UndoAsync();
        Assert.Equal("before", document.Content);
    }

    [Fact]
    public async Task UnopenedFileMustMatchLanguageServiceHash()
    {
        var directory = DirectoryPath();
        var path = Path.Combine(directory, "file.cs");
        await File.WriteAllTextAsync(path, "externally changed");
        var store = new DocumentStore(directory);
        var edits = new WorkspaceEditResult([new DocumentEdits(path, 0, [new TextEdit(0, 3, "new")], DocumentStore.Hash(Encoding.UTF8.GetBytes("old")))], []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkspaceEditTransaction(store).PrepareAsync(edits));
        Assert.Equal("externally changed", (await store.OpenAsync(path)).Content);
    }

    [Fact]
    public async Task UndoRemovesOnlyTransactionCreationsAndTheirRecoveryWithoutCreatingEmptyFiles()
    {
        var directory = DirectoryPath();
        var store = new DocumentStore(directory);
        var existing = store.Create(Path.Combine(directory, "existing.cs"), "before");
        var generatedPath = Path.Combine(directory, "GeneratedViewModel.cs");
        var transaction = new WorkspaceEditTransaction(store);
        await transaction.ApplyAsync([new(existing.Path, "before", "after", "edit"), new(generatedPath, "", "class GeneratedViewModel {}", "create")]);
        await store.WriteRecoveryAsync();
        Assert.Equal(2, (await store.ReadRecoveryAsync()).Count);
        var generated = store.Find(generatedPath)!;
        generated.Content = "newer user changes";
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.UndoAsync());
        Assert.Equal("after", existing.Content);
        Assert.Same(generated, store.Find(generatedPath));
        generated.Content = "class GeneratedViewModel {}";
        Assert.Equal([generatedPath], await transaction.UndoAsync());
        Assert.Null(store.Find(generatedPath));
        Assert.Same(existing, store.Find(existing.Path));
        Assert.Equal("before", existing.Content);
        Assert.DoesNotContain(await store.ReadRecoveryAsync(), recovery => recovery.Path == generatedPath);
        foreach (var document in store.Documents) await store.SaveAsync(document);
        Assert.False(File.Exists(generatedPath));
        Assert.False(transaction.CanUndo);
    }

    [Fact]
    public async Task UndoNeverDeletesSavedGeneratedFiles()
    {
        var directory = DirectoryPath();
        var store = new DocumentStore(directory);
        var generatedPath = Path.Combine(directory, "Generated.cs");
        var transaction = new WorkspaceEditTransaction(store);
        await transaction.ApplyAsync([new(generatedPath, "", "class Generated {}", "create")]);
        var generated = store.Find(generatedPath)!;
        await store.SaveAsync(generated);
        Assert.Empty(await transaction.UndoAsync());
        Assert.Same(generated, store.Find(generatedPath));
        Assert.Equal("class Generated {}", await File.ReadAllTextAsync(generatedPath));
        Assert.Equal("", generated.Content);
        Assert.True(generated.IsDirty);
    }

    [Fact]
    public async Task EarlierDocumentChangesDuringAsyncValidationRejectTheWholeTransaction()
    {
        var directory = DirectoryPath();
        var firstPath = Path.Combine(directory, "first.cs");
        var secondPath = Path.Combine(directory, "second.cs");
        var text = new string('a', 4 * 1024 * 1024);
        await File.WriteAllTextAsync(firstPath, text);
        await File.WriteAllTextAsync(secondPath, text);
        var store = new DocumentStore(directory);
        var first = await store.OpenAsync(firstPath);
        var second = await store.OpenAsync(secondPath);
        var transaction = new WorkspaceEditTransaction(store);
        var apply = transaction.ApplyAsync([new(first.Path, text, "replaced first", "edit"), new(second.Path, text, "replaced second", "edit")]);
        first.Content = "typed during validation";
        await Assert.ThrowsAsync<InvalidOperationException>(() => apply);
        Assert.Equal("typed during validation", first.Content);
        Assert.Equal(text, second.Content);
    }

    [Fact]
    public async Task FailedScaffoldTransactionDoesNotLeaveBlankNewDocuments()
    {
        var directory = DirectoryPath();
        var store = new DocumentStore(directory);
        var existing = store.Create(Path.Combine(directory, "existing.cs"), "changed");
        var createdPath = Path.Combine(directory, "new.cs");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkspaceEditTransaction(store).ApplyAsync([new(createdPath, "", "new content", "create"), new(existing.Path, "old", "new", "edit")]));
        Assert.Null(store.Find(createdPath));
    }

    [Fact]
    public void InvalidOverlappingEditsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceEditTransaction.ApplyTextEdits("abcdef", [new(1, 3, "x"), new(2, 2, "y")]));
        Assert.Equal("aXdeY", WorkspaceEditTransaction.ApplyTextEdits("abcdef", [new(1, 2, "X"), new(5, 1, "Y")]));
    }

    [Fact]
    public void SameOffsetInsertionsRetainTransactionOrdering()
    {
        Assert.Equal("aBAc", WorkspaceEditTransaction.ApplyTextEdits("abc", [new(1, 1, "A"), new(1, 0, "B")]));
        Assert.Equal("aBAbc", WorkspaceEditTransaction.ApplyTextEdits("abc", [new(1, 0, "A"), new(1, 0, "B")]));
        Assert.Throws<InvalidOperationException>(() => WorkspaceEditTransaction.ApplyTextEdits("abc", [new(1, 0, "A"), new(1, 1, "B")]));
    }

    [Fact]
    public void OverflowingEditSpansAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceEditTransaction.ApplyTextEdits("abc", [new(1, int.MaxValue, "invalid")]));
    }

    [Fact]
    public void LargeFormattingBatchDoesNotCopyWholeDocumentForEachEdit()
    {
        const int count = 8000;
        string source = string.Concat(Enumerable.Repeat("<Grid/>", count));
        var edits = Enumerable.Range(0, count).Select(index => new WpfStudio.Contracts.TextEdit(index * 7 + 5, 0, " ")).ToArray();
        _ = WorkspaceEditTransaction.ApplyTextEdits("<Grid/>", [new(5, 0, " ")]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string result = WorkspaceEditTransaction.ApplyTextEdits(source, edits);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(string.Concat(Enumerable.Repeat("<Grid />", count)), result);
        Assert.True(allocated < source.Length * 24L + count * 160L, $"Applying the batch allocated {allocated:N0} bytes.");
    }
}
