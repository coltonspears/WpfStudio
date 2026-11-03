using WpfStudio.App.Services;

namespace WpfStudio.Shell.Tests;

public sealed class DesktopServiceTests
{
    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("calc.exe")]
    [InlineData("ms-settings:privacy")]
    public void OnlyWebAddressesCanBeOpened(string url) => Assert.Throws<InvalidOperationException>(() => new DesktopService().OpenUrl(url));

    [Fact]
    public async Task TheShellCopiesThroughTheClipboardServiceAndReportsAFailure()
    {
        await using var context = new ShellTestContext();
        context.Shell.CopyAboutTextCommand.Execute(null);
        Assert.Equal([context.Shell.AboutText], context.Desktop.Copied);
        Assert.Equal("Version information copied", context.Shell.Status);
        context.Desktop.ClipboardBusy = true;
        context.Shell.CopyAboutTextCommand.Execute(null);
        Assert.Contains("clipboard", context.Shell.Status);
    }
}
