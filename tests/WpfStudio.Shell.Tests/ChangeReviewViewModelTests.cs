using WpfStudio.App.Features.Review;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.Shell.Tests;

/// <summary>The change review against a fake host, without a shell.</summary>
public sealed class ChangeReviewViewModelTests
{
    [Fact]
    public async Task ShowCompletesWithTheDecisionAndSummarisesTheFiles()
    {
        var host = new Host();
        var review = new ChangeReviewViewModel(host, new InlineDispatcher());
        var changes = new FileChange[]
        {
            new(@"C:\Shop\Views\CustomerView.xaml", "<Grid />", "<Grid>\n<Button />\n</Grid>", "edit"),
            new(@"C:\Shop\ViewModels\CustomerViewModel.cs", "", "class CustomerViewModel { }", "create"),
        };

        var decision = review.ShowAsync("Rename symbol", changes, "Check the generated code.");
        Assert.True(review.IsOpen);
        Assert.Equal("Rename symbol", review.Title);
        Assert.Equal("Check the generated code.", review.Warnings);
        Assert.Equal(2, review.Files.Count);
        Assert.True(review.HasManyFiles);
        Assert.Equal("2 files · +4 −1", review.Summary);
        Assert.Equal("New", review.Files[1].Status);
        Assert.Equal("Views", review.Files[0].Folder);
        Assert.Same(changes[0], review.SelectedChange);
        Assert.Same(review.Files[0], review.SelectedFile);

        review.SelectedFile = review.Files[1];
        Assert.Same(changes[1], review.SelectedChange);

        review.AcceptCommand.Execute(null);
        Assert.True(await decision);
        Assert.False(review.IsOpen);

        var second = review.ShowAsync("Again", changes, "");
        review.CancelCommand.Execute(null);
        Assert.False(await second);
    }

    [Fact]
    public async Task AnEmptyOrNestedReviewIsRefusedThroughTheHost()
    {
        var host = new Host();
        var review = new ChangeReviewViewModel(host, new InlineDispatcher());
        Assert.False(await review.ShowAsync("Nothing", [], ""));
        Assert.Equal("No changes required", host.Status);

        var open = review.ShowAsync("First", [new FileChange(@"C:\Shop\A.cs", "a", "b", "edit")], "");
        Assert.False(await review.ShowAsync("Second", [new FileChange(@"C:\Shop\B.cs", "a", "b", "edit")], ""));
        Assert.Equal("Finish the current change preview first", host.Status);
        review.CancelCommand.Execute(null);
        Assert.False(await open);
    }

    [Fact]
    public async Task AFolderThatOnlySharesTheWorkspacePrefixIsNotInsideTheWorkspace()
    {
        var review = new ChangeReviewViewModel(new Host(), new InlineDispatcher());
        var decision = review.ShowAsync("Edit", [new FileChange(@"C:\ShopTests\Cart.cs", "a", "b", "edit"), new FileChange(@"C:\Shop\Views\Cart.xaml", "a", "b", "edit")], "");
        Assert.Equal([@"ShopTests", @"Shop\Views"], review.Files.Select(file => file.Folder));
        review.CancelCommand.Execute(null);
        await decision;

        decision = review.ShowAsync("Edit", [new FileChange(@"C:\ShopTests\Cart.cs", "a", "b", "edit")], "");
        Assert.Equal("ShopTests", Assert.Single(review.Files).Folder);
        review.CancelCommand.Execute(null);
        await decision;
    }

    private sealed class Host : IChangeReviewHost
    {
        public string Status { get; private set; } = "";
        public event Action? DocumentsChanged;
        public void ChangeDocuments() => DocumentsChanged?.Invoke();
        public string? WorkspacePath => @"C:\Shop\Shop.sln";
        public IReadOnlyList<string> WorkspaceFiles => [];
        public IReadOnlyList<string> OpenDocumentPaths => [];
        public IReadOnlyDictionary<string, string> OpenDocumentTexts() => new Dictionary<string, string>();
        public (string Path, int Line)? ActiveLocation => null;
        public string? SelectedExplorerFile => null;
        public IUserDialogService Dialogs => new FakeDialogs();
        public CancellationToken Lifetime => CancellationToken.None;
        public Task GuardAsync(Func<Task> action) => action();
        public void ReportStatus(string status) => Status = status;
        public Task NavigateAsync(string path, int line, int column) => Task.CompletedTask;
        public Task OpenDocumentAsync(string path) => Task.CompletedTask;
        public Task<bool> CloseDocumentAsync(string path) => Task.FromResult(true);
        public void ReviewOpenChanged() { }
    }
}
