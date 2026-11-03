using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.App.Services;

namespace WpfStudio.Shell.Tests;

internal sealed class FakeTheme : IThemeService
{
    public Palette Palette { get; private set; } = Palette.Dark;
    public List<string> Applications { get; } = [];
    public void Apply(string name) { Applications.Add(name); Palette = Palette.For(name); }
    public void Track(Window window) { }
}

public sealed class ThemeTests
{
    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static string StudioThemeXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "WpfStudio.App", "Resources", "StudioTheme.xaml");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("StudioTheme.xaml was not found above the test output.");
    }

    [Fact]
    public void TheXamlStartingColoursAreTheDarkPalette()
    {
        var brushes = Regex.Matches(StudioThemeXaml(), @"<SolidColorBrush x:Key=""(\w+)"" Color=""(#[0-9A-Fa-f]+)""/>");
        Assert.True(brushes.Count > 30);
        foreach (Match brush in brushes) Assert.Equal(Hex(brush.Groups[2].Value), Palette.Dark.Colors[brush.Groups[1].Value]);
    }

    [Fact]
    public void BothPalettesCarryEveryColour()
    {
        Assert.Equal(Palette.Dark.Colors.Keys, Palette.Light.Colors.Keys);
        Assert.Contains("AccentHoverBrush", Palette.Dark.Colors.Keys);
        Assert.Contains("SubtleBrush", Palette.Dark.Colors.Keys);
    }

    [Theory]
    [InlineData("Light", true)]
    [InlineData("light", true)]
    [InlineData("Dark", false)]
    [InlineData("anything else", false)]
    public void ThemeNamesSelectAPalette(string name, bool light) => Assert.Equal(light, Palette.For(name).IsLight);

    // Values the dock theme and the window frame had written out before they shared the palette.
    [Fact]
    public void DockAndFrameColoursAreUnchanged()
    {
        Assert.Equal(Hex("#131417"), Palette.Dark.Window);
        Assert.Equal(Hex("#EDEEF1"), Palette.Light.Window);
        Assert.Equal(Hex("#34373F"), Palette.Dark.StrongBorder);
        Assert.Equal(Hex("#CBCED6"), Palette.Light.StrongBorder);
        Assert.Equal(Hex("#2E3037"), Palette.Dark.WindowFrame);
        Assert.Equal(Hex("#CBCED6"), Palette.Light.WindowFrame);
        Assert.Equal(Hex("#E4E5E9"), Palette.Dark.Text);
        Assert.Equal(Hex("#1C1D21"), Palette.Light.Text);
        Assert.Equal(Hex("#5B63E6"), Palette.Dark.AccentFill);
        Assert.Equal(Hex("#4A52D4"), Palette.Light.AccentFill);
    }

    [Fact]
    public void ApplyingOwnsThePaletteAndNotifies()
    {
        var messenger = new WeakReferenceMessenger();
        var theme = new ThemeService(messenger);
        Assert.Same(Palette.Dark, theme.Palette);
        Palette? applied = null;
        messenger.Register<ThemeChangedMessage>(this, (_, message) => applied = message.Palette);
        theme.Apply("Light");
        Assert.Same(Palette.Light, theme.Palette);
        Assert.Same(Palette.Light, applied);

        messenger.UnregisterAll(this);
        applied = null;
        theme.Apply("Dark");
        Assert.Null(applied);
    }

    [Fact]
    public async Task TheShellAppliesItsThemeFromTheStartAndOnEveryChange()
    {
        await using var test = new ShellTestContext();
        Assert.Equal(["Dark"], test.Theme.Applications);
        test.Shell.ToggleThemeCommand.Execute(null);
        test.Shell.SetThemeCommand.Execute("Light");
        test.Shell.SetThemeCommand.Execute("Dark");
        Assert.Equal(["Dark", "Light", "Dark"], test.Theme.Applications);
    }
}
