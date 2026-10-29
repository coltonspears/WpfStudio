using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed class ShellDocumentTests
{
    [Fact]
    public async Task CancellingCloseRetainsModifiedDocumentAndRecovery()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Customer.cs", "class Customer { }");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.State.Content = "class EditedCustomer { }";
        await test.Store.WriteRecoveryAsync();
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Cancel);
        Assert.False(await test.Shell.CloseDocumentAsync(editor));
        Assert.Same(editor, test.Shell.ActiveDocument);
        Assert.Same(editor.State, test.Store.Find(path));
        Assert.Equal("class EditedCustomer { }", (await test.Store.ReadRecoveryAsync()).Single().Content);
        Assert.Equal("class Customer { }", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DecliningExternalOverwriteKeepsBothDiskAndBuffer()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Customer.cs", "original");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.State.Content = "my edit";
        await File.WriteAllTextAsync(path, "external edit");
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Save);
        test.Dialogs.Confirmations.Enqueue(false);
        Assert.False(await test.Shell.CloseDocumentAsync(editor));
        Assert.Equal("my edit", editor.State.Content);
        Assert.True(editor.State.IsDirty);
        Assert.Equal("external edit", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExternalChangesWaitForAChoiceAndNeverReplaceTheEditorTextOnTheirOwn()
    {
        await using var test = new ShellTestContext();
        string clean = await test.CreateFileAsync("Clean.cs", "clean original");
        string dirty = await test.CreateFileAsync("Dirty.cs", "dirty original");
        await test.Shell.OpenDocumentAsync(clean);
        await test.Shell.OpenDocumentAsync(dirty);
        test.Store.Find(dirty)!.Content = "my edit";
        await File.WriteAllTextAsync(clean, "clean external");
        await File.WriteAllTextAsync(dirty, "dirty external");
        await test.Shell.CheckExternalChangesAsync();
        Assert.Equal("clean original", test.Store.Find(clean)!.Content);
        Assert.Equal("my edit", test.Store.Find(dirty)!.Content);
        Assert.Equal(["Clean.cs", "Dirty.cs"], test.Shell.ExternalChanges.Select(change => change.Name).Order());
        Assert.True(test.Store.Find(dirty)!.HasExternalChange);
        Assert.Contains("⚠", test.Store.Find(dirty)!.Title);
        var dirtyChange = test.Shell.ExternalChanges.Single(change => change.Name == "Dirty.cs");
        Assert.True(dirtyChange.HasUnsavedEdits);
        Assert.Contains("unsaved edits are discarded", dirtyChange.LoadConsequence);

        // A second check of the same disk versions does not duplicate the prompt.
        await test.Shell.CheckExternalChangesAsync();
        Assert.Equal(2, test.Shell.ExternalChanges.Count);

        await test.Shell.LoadDiskVersionCommand.ExecuteAsync(test.Shell.ExternalChanges.Single(change => change.Name == "Clean.cs"));
        Assert.Equal("clean external", test.Store.Find(clean)!.Content);
        Assert.False(test.Store.Find(clean)!.IsDirty);

        test.Shell.KeepEditorVersionCommand.Execute(dirtyChange);
        Assert.Empty(test.Shell.ExternalChanges);
        Assert.Equal("my edit", test.Store.Find(dirty)!.Content);
        Assert.True(test.Store.Find(dirty)!.IsDirty);
        Assert.False(test.Store.Find(dirty)!.HasExternalChange);
        // Keeping your version accepts the disk version as the one being replaced: saving needs no further question.
        await test.Shell.SaveAllCommand.ExecuteAsync(null);
        Assert.Equal("my edit", await File.ReadAllTextAsync(dirty));
    }

    [Fact]
    public async Task AutomaticReloadAppliesOnlyToFilesWithoutUnsavedEdits()
    {
        await using var test = new ShellTestContext();
        string clean = await test.CreateFileAsync("Clean.cs", "clean original");
        string dirty = await test.CreateFileAsync("Dirty.cs", "dirty original");
        await test.Shell.OpenDocumentAsync(clean);
        await test.Shell.OpenDocumentAsync(dirty);
        test.Shell.AutoReloadUnchangedFiles = true;
        test.Store.Find(dirty)!.Content = "my edit";
        await File.WriteAllTextAsync(clean, "clean external");
        await File.WriteAllTextAsync(dirty, "dirty external");
        await test.Shell.CheckExternalChangesAsync();
        Assert.Equal("clean external", test.Store.Find(clean)!.Content);
        Assert.False(test.Store.Find(clean)!.IsDirty);
        Assert.Equal("Dirty.cs", Assert.Single(test.Shell.ExternalChanges).Name);

        // The same text arriving on disk resolves the prompt without a choice.
        await File.WriteAllTextAsync(dirty, "my edit");
        await test.Shell.CheckExternalChangesAsync();
        Assert.Empty(test.Shell.ExternalChanges);
        Assert.False(test.Store.Find(dirty)!.IsDirty);
    }

    [Fact]
    public async Task AnOutsideChangeThatIsUndoneClearsThePromptAndTheTabMarker()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Undone.cs", "original");
        await test.Shell.OpenDocumentAsync(path);
        await File.WriteAllTextAsync(path, "external");
        await test.Shell.CheckExternalChangesAsync();
        Assert.Single(test.Shell.ExternalChanges);
        await File.WriteAllTextAsync(path, "original");
        await test.Shell.CheckExternalChangesAsync();
        Assert.Empty(test.Shell.ExternalChanges);
        Assert.False(test.Store.Find(path)!.HasExternalChange);
        Assert.DoesNotContain("⚠", test.Store.Find(path)!.Title);
    }

    [Fact]
    public async Task ADeletedFileOffersToCloseItsTabOrKeepItForSaving()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Gone.cs", "original");
        await test.Shell.OpenDocumentAsync(path);
        File.Delete(path);
        await test.Shell.CheckExternalChangesAsync();
        var change = Assert.Single(test.Shell.ExternalChanges);
        Assert.True(change.IsDeleted);
        Assert.Equal("Close tab", change.LoadLabel);
        test.Shell.KeepEditorVersionCommand.Execute(change);
        Assert.True(test.Store.Find(path)!.IsDirty);
        await test.Shell.SaveAllCommand.ExecuteAsync(null);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CloseAndReopenReadsLatestFileAndAvoidsDuplicateTabs()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Customer.cs", "first");
        await test.Shell.OpenDocumentAsync(path);
        var original = test.Shell.ActiveDocument!;
        await test.Shell.OpenDocumentAsync(path);
        Assert.Single(test.Shell.Documents);
        Assert.True(await test.Shell.CloseDocumentAsync(original));
        Assert.Empty(test.Shell.Documents);
        Assert.Null(test.Store.Find(path));
        await File.WriteAllTextAsync(path, "second");
        await test.Shell.OpenDocumentAsync(path);
        Assert.NotSame(original, test.Shell.ActiveDocument);
        Assert.Equal("second", test.Shell.ActiveDocument!.State.Content);
    }

    [Fact]
    public async Task CancelledWorkspaceSwitchRetainsEveryDirtyDocumentAndRecoveryRecord()
    {
        await using var test = new ShellTestContext();
        string first = await test.CreateFileAsync("First.cs", "first saved");
        string second = await test.CreateFileAsync("Second.cs", "second saved");
        await test.Shell.OpenDocumentAsync(first);
        test.Shell.ActiveDocument!.State.Content = "first unsaved";
        await test.Shell.OpenDocumentAsync(second);
        test.Shell.ActiveDocument!.State.Content = "second unsaved";
        await test.Store.WriteRecoveryAsync();
        var original = new WorkspaceSnapshot(Path.Combine(test.Root, "Original.sln"), "test", [], []);
        test.Shell.Workspace = original;
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Discard);
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Cancel);
        await test.Shell.LoadWorkspaceAsync(Path.Combine(test.Root, "Other.sln"));
        Assert.Same(original, test.Shell.Workspace);
        Assert.Equal(2, test.Shell.Documents.Count);
        Assert.Equal("first unsaved", test.Store.Find(first)!.Content);
        Assert.Equal("second unsaved", test.Store.Find(second)!.Content);
        Assert.Equal(2, (await test.Store.ReadRecoveryAsync()).Count);
    }

    [Fact]
    public async Task ModifiedSqlCanCancelWholeApplicationShutdown()
    {
        await using var test = new ShellTestContext();
        var query = test.Shell.Database.SelectedDocument!;
        query.SqlText += "\nSELECT 42;";
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Cancel);
        Assert.False(await test.Shell.TryCloseAsync());
        Assert.True(query.IsDirty);
        Assert.Contains(query.Title, test.Dialogs.SaveQuestions);
    }

    [Fact]
    public async Task SqlSaveDuringShutdownWritesThePendingQuery()
    {
        await using var test = new ShellTestContext();
        var query = test.Shell.Database.SelectedDocument!;
        query.SqlText = "SELECT 42;";
        string path = Path.Combine(test.Root, "SavedQuery.sql");
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Save);
        test.Dialogs.SavePaths.Enqueue(path);
        Assert.True(await test.Shell.TryCloseAsync());
        Assert.Equal("SELECT 42;", await File.ReadAllTextAsync(path));
        Assert.False(query.IsDirty);
    }
}
