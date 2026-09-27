namespace WpfStudio.Shell.Tests;

public sealed class ShellPreviewTests
{
    private const string Original = "public partial class CustomerViewModel\n{\n}\n";

    [Fact]
    public async Task AcceptingPreviewChangesBufferAndWorkspaceUndoRestoresIt()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("CustomerViewModel.cs", Original);
        await test.Shell.OpenDocumentAsync(path);
        test.Shell.ActiveDocument!.State.CaretOffset = Original.IndexOf('}') - 1;
        test.Dialogs.Prompts.Enqueue("Refresh");
        Task operation = test.Shell.InsertCommandCommand.ExecuteAsync(null);
        Assert.True(test.Shell.IsPreviewOpen);
        Assert.Equal(Original, test.Store.Find(path)!.Content);
        Assert.Single(test.Shell.PreviewChanges);
        test.Shell.AcceptPreviewCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("RefreshAsync", test.Store.Find(path)!.Content);
        Assert.Equal(Original, await File.ReadAllTextAsync(path));
        test.Shell.UndoWorkspaceEditCommand.Execute(null);
        Assert.Equal(Original, test.Store.Find(path)!.Content);
    }

    [Fact]
    public async Task CancellingPreviewLeavesFilesAndBuffersUntouched()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("CustomerViewModel.cs", Original);
        await test.Shell.OpenDocumentAsync(path);
        Task operation = test.Shell.InsertCommandCommand.ExecuteAsync(null);
        Assert.True(test.Shell.IsPreviewOpen);
        test.Shell.CancelPreviewCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Original, test.Store.Find(path)!.Content);
        Assert.Equal(Original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StalePreviewCannotOverwriteNewerTyping()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("CustomerViewModel.cs", Original);
        await test.Shell.OpenDocumentAsync(path);
        Task operation = test.Shell.InsertCommandCommand.ExecuteAsync(null);
        string updated = Original + "// typed after preview\n";
        test.Shell.ActiveDocument!.State.Content = updated;
        test.Shell.AcceptPreviewCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(updated, test.Store.Find(path)!.Content);
        Assert.Contains("changed after the preview", test.Shell.Status);
    }

    [Fact]
    public async Task ShutdownRefusesPendingPreviewAndCancelReleasesOperation()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("CustomerViewModel.cs", Original);
        await test.Shell.OpenDocumentAsync(path);
        Task operation = test.Shell.InsertCommandCommand.ExecuteAsync(null);
        Assert.False(await test.Shell.TryCloseAsync());
        Assert.True(test.Shell.IsPreviewOpen);
        test.Shell.CancelPreviewCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(test.Shell.IsPreviewOpen);
    }

    [Fact]
    public async Task SecondPreviewIsRejectedWithoutOrphaningFirstOperation()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("CustomerViewModel.cs", Original);
        await test.Shell.OpenDocumentAsync(path);
        Task first = test.Shell.InsertCommandCommand.ExecuteAsync(null);
        Assert.True(test.Shell.IsPreviewOpen);
        await test.Shell.InsertPropertyCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(test.Shell.IsPreviewOpen);
        Assert.False(first.IsCompleted);
        test.Shell.CancelPreviewCommand.Execute(null);
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Original, test.Store.Find(path)!.Content);
    }
}
