using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class XamlRuntimeSourceLocatorTests
{
    private const string Path = @"C:\Project\View.xaml";
    private const string Root = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>";

    [Theory]
    [InlineData("\r\n", 3)]
    [InlineData("\r\n", 4)]
    [InlineData("\n", 3)]
    [InlineData("\n", 4)]
    [InlineData("\r", 3)]
    [InlineData("\r", 4)]
    public void ExactElementOpeningOrNameSelectsItsNameWithAnyLineEnding(string newline, int column)
    {
        string text = Root + newline + "  <Button x:Name='Target' Content='Value' />" + newline + "</Grid>";
        var result = XamlRuntimeSourceLocator.Locate(Path, text, 2, column, "System.Windows.Controls.Button", "Target");
        var location = Assert.IsType<WpfStudio.Contracts.SourceLocation>(result.Location);
        Assert.Equal("Button", text.Substring(location.Start, location.Length));
        Assert.Equal((2, 4), (location.Line, location.Column));
        Assert.Equal(Path, location.Path);
        var element = Assert.IsType<WpfStudio.Contracts.SourceLocation>(result.Element);
        Assert.Equal("<Button x:Name='Target' Content='Value' />", text.Substring(element.Start, element.Length));
        Assert.Equal((2, 3), (element.Line, element.Column));
        Assert.Equal("http://schemas.microsoft.com/winfx/2006/xaml/presentation", result.ElementNamespace);
        Assert.Equal("Button", result.ElementType);
    }

    [Theory]
    [InlineData(1)] // Leading whitespace is not an element hint.
    [InlineData(2)]
    [InlineData(5)] // Inside a name is not its start.
    [InlineData(11)] // Attribute whitespace cannot be rounded to the containing element.
    [InlineData(15)]
    [InlineData(999)]
    public void NearbyOrOutOfRangeColumnsNeverChooseTheNearestElement(int column)
    {
        string text = Root + "\n  <Button x:Name='Target' />\n</Grid>";
        Assert.Null(XamlRuntimeSourceLocator.Locate(Path, text, 2, column, "System.Windows.Controls.Button", "Target").Location);
    }

    [Fact]
    public void QualifiedElementAndAlternateXamlPrefixAreMatchedWithoutChangingTheSourceSpan()
    {
        const string text = "<local:Control xmlns:local='clr-namespace:App' xmlns:q='http://schemas.microsoft.com/winfx/2006/xaml' q:Name='Target' />";
        var result = XamlRuntimeSourceLocator.Locate(Path, text, 1, 1, "App.Control", "Target");
        var location = Assert.IsType<WpfStudio.Contracts.SourceLocation>(result.Location);
        Assert.Equal("local:Control", text.Substring(location.Start, location.Length));
        Assert.Equal(2, location.Column);
        Assert.Equal("clr-namespace:App", result.ElementNamespace);
        Assert.Equal("Control", result.ElementType);
        Assert.Equal(text, text.Substring(result.Element!.Start, result.Element.Length));
    }

    [Fact]
    public void RootClassCanIdentifyACustomWindowButAChildClassCannotOverrideATypeMismatch()
    {
        const string root = "<Window xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='App.MainWindow' />";
        var mapped = XamlRuntimeSourceLocator.Locate(Path, root, 1, 1, "App.MainWindow", null);
        Assert.NotNull(mapped.Location);
        Assert.Equal("Window", mapped.ElementType);
        Assert.Equal("", mapped.ElementNamespace);
        Assert.Null(XamlRuntimeSourceLocator.Locate(Path, root, 1, 1, "App.OtherWindow", null).Location);
        string child = Root + "\n<Button x:Class='App.MainWindow' />\n</Grid>";
        Assert.Null(XamlRuntimeSourceLocator.Locate(Path, child, 2, 1, "App.MainWindow", null).Location);
    }

    [Theory]
    [InlineData("<Button Name='Expected' />", "System.Windows.Controls.Button", "Changed", false)]
    [InlineData("<Button />", "System.Windows.Controls.Button", "Expected", false)]
    [InlineData("<Button Name='Expected' />", "System.Windows.Controls.TextBlock", "Expected", false)]
    [InlineData("<Button Name='Expected' />", "System.Windows.Controls.Button", "Expected", true)]
    [InlineData("<Button Name='Expected' />", "System.Windows.Controls.Button", null, true)]
    [InlineData("<Button xmlns:x='urn:not-xaml' x:Name='Expected' />", "System.Windows.Controls.Button", "Expected", false)]
    [InlineData("<Button xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Name='Expected' x:Name='Other' />", "System.Windows.Controls.Button", "Expected", false)]
    public void RuntimeNameAndTypeMustMatchTheExactAuthoredDeclaration(string text, string type, string? name, bool expected)
    {
        var result = XamlRuntimeSourceLocator.Locate(Path, text, 1, 1, type, name);
        Assert.Equal(expected, result.Location is not null);
        Assert.Equal(expected, result.Element is not null);
        Assert.Equal(expected, result.ElementType is not null);
        Assert.Equal(expected, result.ElementNamespace is not null);
        Assert.False(string.IsNullOrWhiteSpace(result.Status));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void InvalidLineOrColumnIsUnavailable(int line, int column) =>
        Assert.Null(XamlRuntimeSourceLocator.Locate(Path, "<Button />", line, column, "Button", null).Location);

    [Theory]
    [InlineData("<Button>")]
    [InlineData("<!DOCTYPE Button [<!ENTITY content SYSTEM 'file:///C:/secret'>]><Button>&content;</Button>")]
    [InlineData("<Grid><!-- <Button /> --></Grid>")]
    [InlineData("<Grid><Grid.Resources /></Grid>")]
    public void MalformedXmlCommentsAndPropertyElementsAreUnavailable(string text)
    {
        int start = text.IndexOf("<Button", StringComparison.Ordinal);
        if (start < 0) start = text.IndexOf("<Grid.Resources", StringComparison.Ordinal);
        var result = XamlRuntimeSourceLocator.Locate(Path, text, 1, start + 1, "System.Windows.Controls.Button", null);
        Assert.Null(result.Location);
    }
}
