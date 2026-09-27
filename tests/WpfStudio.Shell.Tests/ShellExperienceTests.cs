using WpfStudio.App.ViewModels;

namespace WpfStudio.Shell.Tests;

public sealed class ShellExperienceTests
{
    [Fact]
    public async Task CommandPaletteListsEveryCommandWithShortcutsAndSwitchesModesByPrefix()
    {
        await using var test = new ShellTestContext();
        test.Shell.CommandPaletteCommand.Execute(null);
        Assert.True(test.Shell.IsPaletteOpen);
        Assert.True(test.Shell.IsCommandPalette);
        Assert.Contains(test.Shell.PaletteResults, entry => entry.Label == "Build" && entry.Shortcut == "Ctrl+Shift+B" && entry.Category == "Build");
        Assert.Contains(test.Shell.PaletteResults, entry => entry.Label == "Switch view ⇄ view model" && entry.Shortcut == "F7");

        test.Shell.PaletteQuery = ">rbld";
        Assert.Equal("Rebuild", test.Shell.PaletteResults.First().Label);
        test.Shell.PaletteQuery = ">zzzz";
        Assert.Empty(test.Shell.PaletteResults);

        test.Shell.PaletteQuery = "";
        Assert.False(test.Shell.IsCommandPalette);
        test.Shell.PaletteQuery = ">";
        Assert.True(test.Shell.IsCommandPalette);
    }

    [Fact]
    public void FuzzyScoringPrefersPrefixesWordStartsAndRuns()
    {
        Assert.Null(ShellViewModel.FuzzyScore("Build", "xyz"));
        Assert.NotNull(ShellViewModel.FuzzyScore("CounterViewModel.cs", "cvm"));
        Assert.True(ShellViewModel.FuzzyScore("Build", "bu") > ShellViewModel.FuzzyScore("Rebuild", "bu"));
        Assert.Equal(0, ShellViewModel.FuzzyScore("anything", ""));
    }

    [Fact]
    public async Task EditorContextBarListsConventionalRelatedFiles()
    {
        await using var test = new ShellTestContext();
        var view = await test.CreateFileAsync("CustomerView.xaml", "<Grid />");
        var codeBehind = await test.CreateFileAsync("CustomerView.xaml.cs", "class CustomerView {}");
        var viewModel = await test.CreateFileAsync("CustomerViewModel.cs", "class CustomerViewModel {}");

        await test.Shell.OpenDocumentAsync(view);
        var related = test.Shell.ActiveDocument!.RelatedFiles;
        Assert.Contains(related, file => file.Path == codeBehind && file.Role == "Code-behind");
        Assert.Contains(related, file => file.Path == viewModel && file.Role == "View model");
        Assert.Equal("XAML", test.Shell.ActiveDocument.KindLabel);

        await test.Shell.OpenDocumentAsync(viewModel);
        Assert.True(test.Shell.ActiveDocument!.IsViewModel);
        Assert.Contains(test.Shell.ActiveDocument.RelatedFiles, file => file.Path == view && file.Role == "View");

        test.Shell.ActiveDocument.OpenRelatedCommand.Execute(test.Shell.ActiveDocument.RelatedFiles.First(file => file.Path == view));
        for (int i = 0; i < 100 && test.Shell.ActiveDocument?.State.Path != view; i++) await Task.Delay(10);
        Assert.Equal(view, test.Shell.ActiveDocument!.State.Path);
    }

    [Fact]
    public async Task ThemeCanBeChosenDirectlyAndReportsItsState()
    {
        await using var test = new ShellTestContext();
        string? applied = null;
        test.Shell.ThemeChanged += name => applied = name;
        Assert.True(test.Shell.IsDarkTheme);
        test.Shell.SetThemeCommand.Execute("Light");
        Assert.Equal("Light", applied);
        Assert.True(test.Shell.IsLightTheme);
        Assert.False(test.Shell.IsDarkTheme);
        applied = null;
        test.Shell.SetThemeCommand.Execute("Light");
        Assert.Null(applied);
    }
}
