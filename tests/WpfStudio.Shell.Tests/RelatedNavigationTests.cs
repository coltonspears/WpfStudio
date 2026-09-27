namespace WpfStudio.Shell.Tests;

public sealed class RelatedNavigationTests
{
    [Theory]
    [InlineData("CustomerViewViewModel.cs")]
    [InlineData("CustomerViewModel.cs")]
    public async Task RelatedNavigationCyclesViewCodeBehindAndViewModel(string viewModelName)
    {
        await using var test = new ShellTestContext();
        var view = await test.CreateFileAsync("CustomerView.xaml", "<Grid />");
        var codeBehind = await test.CreateFileAsync("CustomerView.xaml.cs", "class CustomerView {}");
        var viewModel = await test.CreateFileAsync(viewModelName, "class CustomerViewModel {}");
        await test.Shell.OpenDocumentAsync(viewModel);
        await test.Shell.OpenDocumentAsync(view);
        await test.Shell.SwitchRelatedCommand.ExecuteAsync(null);
        Assert.Equal(codeBehind, test.Shell.ActiveDocument!.State.Path);
        await test.Shell.SwitchRelatedCommand.ExecuteAsync(null);
        Assert.Equal(viewModel, test.Shell.ActiveDocument!.State.Path);
        await test.Shell.SwitchRelatedCommand.ExecuteAsync(null);
        Assert.Equal(view, test.Shell.ActiveDocument!.State.Path);
    }
}
