using WpfStudio.Core.Documents;

namespace WpfStudio.Shell.Tests;

public sealed class DesignerOverlayTests
{
    [Fact]
    public async Task EachOverlayOfTheShellSuppressesTheDesignersNativePreview()
    {
        await using var context = new ShellTestContext();
        var shell = context.Shell;
        Assert.False(shell.Designer.IsOverlayOpen);

        shell.Palette.OpenCommands();
        Assert.True(shell.Designer.IsOverlayOpen);
        shell.Palette.CloseCommand.Execute(null);
        Assert.False(shell.Designer.IsOverlayOpen);

        shell.IsScaffoldOpen = true;
        Assert.True(shell.Designer.IsOverlayOpen);
        shell.IsScaffoldOpen = false;
        Assert.False(shell.Designer.IsOverlayOpen);

        shell.IsSettingsOpen = true;
        Assert.True(shell.Designer.IsOverlayOpen);
        // A second overlay closing does not lift the suppression while another stays open.
        shell.IsScaffoldOpen = true;
        shell.IsSettingsOpen = false;
        Assert.True(shell.Designer.IsOverlayOpen);
        shell.IsScaffoldOpen = false;
        Assert.False(shell.Designer.IsOverlayOpen);
    }

    [Fact]
    public async Task TheBreakpointSettingsAndTheGroupPickerSuppressTheNativePreviewUntilTheyClose()
    {
        await using var context = new ShellTestContext();
        var shell = context.Shell;
        var path = await context.CreateFileAsync("Program.cs", "class Program { }\n");
        await shell.OpenDocumentAsync(path);

        shell.Breakpoints.EditSettingsCommand.Execute(null);
        Assert.True(shell.Breakpoints.Settings.IsOpen);
        Assert.True(shell.Designer.IsOverlayOpen);
        shell.Breakpoints.Settings.CancelCommand.Execute(null);
        Assert.False(shell.Designer.IsOverlayOpen);

        shell.FileGroups.Picker.Open("Add to a file group", [path], [], null);
        Assert.True(shell.Designer.IsOverlayOpen);
        shell.FileGroups.Picker.CancelCommand.Execute(null);
        Assert.False(shell.Designer.IsOverlayOpen);
    }

    [Fact]
    public async Task AChangeReviewSuppressesTheNativePreviewUntilItCloses()
    {
        await using var context = new ShellTestContext();
        var path = await context.CreateFileAsync("View.xaml", "<Grid />");
        var review = context.Shell.ChangeReview.ShowAsync("Review", [new FileChange(path, "<Grid />", "<Grid Margin=\"1\" />", "Margin")], "");
        Assert.True(context.Shell.IsPreviewOpen);
        Assert.True(context.Shell.Designer.IsOverlayOpen);
        context.Shell.ChangeReview.CancelCommand.Execute(null);
        await review;
        Assert.False(context.Shell.Designer.IsOverlayOpen);
    }
}
