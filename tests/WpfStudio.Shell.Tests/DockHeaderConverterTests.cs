using System.Globalization;
using System.Windows;
using WpfStudio.App.Controls;

namespace WpfStudio.Shell.Tests;

/// <summary>Dock headers bind to the content id, a document's path or a workbench id, not to the decorated tab title.</summary>
public sealed class DockHeaderConverterTests
{
    [Theory]
    [InlineData(@"C:\src\Foo.cs", "CSharp")]
    [InlineData(@"C:\src\FooViewModel.cs", "ViewModel")]
    [InlineData(@"C:\src\Foo.xaml", "Xaml")]
    [InlineData(@"C:\src\LICENSE", "File")]
    [InlineData(null, "File")]
    public void TheIconComesFromThePath(string? path, string kind) => Assert.Equal(kind, FileIconKindConverter.KindFor(path));

    [Theory]
    [InlineData(@"C:\src\Foo.cs", Visibility.Visible)]
    [InlineData("Packages", Visibility.Collapsed)]
    [InlineData("Welcome", Visibility.Collapsed)]
    [InlineData(null, Visibility.Collapsed)]
    public void OnlyFilePathsShowAFileIcon(string? contentId, Visibility expected) =>
        Assert.Equal(expected, new FileNameVisibilityConverter().Convert(contentId, typeof(Visibility), null, CultureInfo.InvariantCulture));
}
