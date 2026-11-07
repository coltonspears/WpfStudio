using WpfStudio.App.Features.Workbench;

namespace WpfStudio.Shell.Tests;

public sealed class ShellExperienceTests
{
    [Fact]
    public async Task CommandPaletteListsEveryCommandWithShortcutsAndSwitchesModesByPrefix()
    {
        await using var test = new ShellTestContext();
        test.Shell.CommandPaletteCommand.Execute(null);
        Assert.True(test.Shell.Palette.IsOpen);
        Assert.True(test.Shell.Palette.IsCommandMode);
        Assert.Contains(test.Shell.Palette.Results, entry => entry.Label == "Build" && entry.Shortcut == "Ctrl+Shift+B" && entry.Category == "Build");
        Assert.Contains(test.Shell.Palette.Results, entry => entry.Label == "Switch view ⇄ view model" && entry.Shortcut == "F7");

        test.Shell.Palette.Query = ">rbld";
        Assert.Equal("Rebuild", test.Shell.Palette.Results.First().Label);
        test.Shell.Palette.Query = ">zzzz";
        Assert.Empty(test.Shell.Palette.Results);
        test.Shell.Palette.Query = ">compare snapshots";
        Assert.Contains(test.Shell.Palette.Results, entry => entry.Label == "Compare memory snapshots" && entry.Category == "Memory");
        Assert.Contains(test.Shell.Palette.Results.Take(3), entry => entry.Label == "Compare memory snapshots");

        test.Shell.Palette.Query = "";
        Assert.False(test.Shell.Palette.IsCommandMode);
        test.Shell.Palette.Query = ">";
        Assert.True(test.Shell.Palette.IsCommandMode);
    }

    [Fact]
    public async Task PersonalTasksLoadWithoutAWorkspaceAndRunFromThePalette()
    {
        await using var test = new ShellTestContext();
        var personal = WpfStudio.Core.Tasks.TaskCatalog.PersonalFile(test.Shell.DataFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(personal)!);
        await File.WriteAllTextAsync(personal, """{ "tasks": [ { "label": "greet", "command": "echo hi", "detail": "Says hi", "group": "build" }, { "label": "secret", "command": "echo", "hide": true } ] }""");
        test.Shell.CommandPaletteCommand.Execute(null);
        Assert.DoesNotContain(test.Shell.Palette.Results, entry => entry.Label == "Run build task");
        test.Shell.Palette.CloseCommand.Execute(null);
        await test.Shell.InitializeAsync([]);

        Assert.Equal("greet", Assert.Single(test.Shell.Tasks.Items).Label);
        Assert.False(test.Shell.Tasks.HasWorkspace);
        test.Shell.RunTaskPaletteCommand.Execute(null);
        Assert.True(test.Shell.Palette.IsOpen);
        var entry = Assert.Single(test.Shell.Palette.Results);
        Assert.Equal(("Task: greet", "Says hi", "Tasks"), (entry.Label, entry.Detail, entry.Category));
        test.Shell.Palette.Query = ">run build task";
        Assert.Equal("Run build task", test.Shell.Palette.Results.First().Label);
        Assert.DoesNotContain(test.Shell.Palette.Results, entry => entry.Label == "Run test task");
        await test.Shell.Tasks.RunTestTaskCommand.ExecuteAsync(null);
        Assert.StartsWith("No test task yet", test.Shell.Status);
    }

    [Fact]
    public void FuzzyScoringPrefersPrefixesWordStartsAndRuns()
    {
        Assert.Null(CommandPaletteViewModel.FuzzyScore("Build", "xyz"));
        Assert.NotNull(CommandPaletteViewModel.FuzzyScore("CounterViewModel.cs", "cvm"));
        Assert.True(CommandPaletteViewModel.FuzzyScore("Build", "bu") > CommandPaletteViewModel.FuzzyScore("Rebuild", "bu"));
        Assert.Equal(0, CommandPaletteViewModel.FuzzyScore("anything", ""));
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
        Assert.True(test.Shell.IsDarkTheme);
        test.Shell.SetThemeCommand.Execute("Light");
        Assert.Equal("Light", test.Theme.Applications[^1]);
        Assert.True(test.Shell.IsLightTheme);
        Assert.False(test.Shell.IsDarkTheme);
        var applications = test.Theme.Applications.Count;
        test.Shell.SetThemeCommand.Execute("Light");
        Assert.Equal(applications, test.Theme.Applications.Count);
    }
}
