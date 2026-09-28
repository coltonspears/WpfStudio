using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class XamlBindingSourceLocatorTests
{
    private const string File = "C:/App/View.xaml";
    private const string Root = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>";

    [Theory]
    [InlineData("Text")]
    [InlineData("Tag")]
    [InlineData("ToolTip")]
    public void RepeatedSameLinePathsSelectOnlyTheRecordedAttribute(string property)
    {
        string source = Root + "<TextBlock Text='{Binding Name}' Tag='{Binding Name}' ToolTip='{Binding Name}'/></Grid>";
        int hint = source.IndexOf(property + "=", StringComparison.Ordinal);
        var location = Assert.IsType<WpfStudio.Contracts.SourceLocation>(Locate(source, hint).Location);
        Assert.Equal(hint + property.Length + 2, location.Start);
        Assert.Equal("{Binding Name}", source.Substring(location.Start, location.Length));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void RawEntityAndMultilineSpansArePreserved(string newline)
    {
        string extension = "{Binding" + newline + " Path=Pri&#109;ary, FallbackValue=\"😀\"}";
        string source = Root + newline + "<TextBlock Tag='😀' Text='" + extension + "'/>" + newline + "</Grid>";
        var result = Locate(source, source.IndexOf("Text=", StringComparison.Ordinal));
        var location = Assert.IsType<WpfStudio.Contracts.SourceLocation>(result.Location);
        Assert.Equal(extension, source.Substring(location.Start, location.Length));
        Assert.Equal((2, 27), (location.Line, location.Column));
    }

    [Theory]
    [InlineData("Binding", "<Binding Path='Name'/>")]
    [InlineData("MultiBinding", "<MultiBinding><Binding Path='Name'/><Binding Path='Age'/></MultiBinding>")]
    [InlineData("PriorityBinding", "<PriorityBinding><Binding Path='Name'/><Binding Path='Age'/></PriorityBinding>")]
    public void ObjectDeclarationsSelectTheWholeExpressionIncludingChildren(string kind, string expression)
    {
        string source = Root + "<TextBlock><TextBlock.Text>" + expression + "</TextBlock.Text></TextBlock></Grid>";
        int hint = source.IndexOf("<" + kind, StringComparison.Ordinal);
        foreach (int offset in new[] { hint, hint + 1 })
        {
            var location = Assert.IsType<WpfStudio.Contracts.SourceLocation>(Locate(source, offset, kind).Location);
            Assert.Equal(expression, source.Substring(location.Start, location.Length));
        }
        if (kind != "Binding")
        {
            int childHint = source.IndexOf("<Binding Path='Age'", StringComparison.Ordinal) + 1;
            var child = Assert.IsType<WpfStudio.Contracts.SourceLocation>(Locate(source, childHint).Location);
            Assert.Equal("<Binding Path='Age'/>", source.Substring(child.Start, child.Length));
        }
    }

    [Theory]
    [InlineData("http://schemas.microsoft.com/winfx/2006/xaml/presentation", true)]
    [InlineData("clr-namespace:System.Windows.Data;assembly=PresentationFramework", true)]
    [InlineData("clr-namespace:App;assembly=PresentationFramework", false)]
    [InlineData("urn:custom-binding", false)]
    public void PrefixesResolveToActualWpfBindingNamespace(string ns, bool expected)
    {
        string source = Root + "<TextBlock xmlns:b='" + ns + "' Text='{b:Binding Name}'><TextBlock.Tag><b:Binding Path='Name'/></TextBlock.Tag></TextBlock></Grid>";
        Assert.Equal(expected, Locate(source, source.IndexOf("Text=", StringComparison.Ordinal)).Location is not null);
        Assert.Equal(expected, Locate(source, source.IndexOf("<b:Binding", StringComparison.Ordinal) + 1).Location is not null);
    }

    [Theory]
    [InlineData("{Binding Name}", true)]
    [InlineData("{Binding Name, RelativeSource={RelativeSource AncestorType={x:Type TextBlock}}}", true)]
    [InlineData("{Binding Name, StringFormat=\"{0}\"}", true)]
    [InlineData("{Binding Name, StringFormat={}{0}}", true)]
    [InlineData("{}{Binding Name}", false)]
    [InlineData("literal {Binding Name}", false)]
    [InlineData("{Other Value={Binding Name}}", false)]
    [InlineData("{Binding Name", false)]
    [InlineData("{Binding Name}}", false)]
    [InlineData("{Binding Name}trailing", false)]
    [InlineData("{Binding Name, FallbackValue=\"unterminated}", false)]
    [InlineData("{MultiBinding}", false)]
    public void OnlyACompleteOuterBindingExtensionIsNavigable(string expression, bool expected)
    {
        string source = Root + "<TextBlock Text='" + expression + "'/></Grid>";
        Assert.Equal(expected, Locate(source, source.IndexOf("Text=", StringComparison.Ordinal)).Location is not null);
    }

    [Fact]
    public void StyleSetterAndSharedResourceUseTheirOwnDeclarationCoordinates()
    {
        string source = Root + "<Grid.Resources><Style TargetType='TextBlock'><Setter Property='Text' Value='{Binding Name}'/></Style><Binding x:Key='Shared' Path='Name'/></Grid.Resources><TextBlock Text='{Binding Name}'/></Grid>";
        int setterHint = source.IndexOf("Value=", StringComparison.Ordinal);
        var setter = Assert.IsType<WpfStudio.Contracts.SourceLocation>(Locate(source, setterHint).Location);
        Assert.Equal(setterHint + 7, setter.Start);
        int resourceHint = source.IndexOf("<Binding x:Key", StringComparison.Ordinal) + 1;
        var resource = Assert.IsType<WpfStudio.Contracts.SourceLocation>(Locate(source, resourceHint).Location);
        Assert.Equal("<Binding x:Key='Shared' Path='Name'/>", source.Substring(resource.Start, resource.Length));
    }

    [Fact]
    public void NearbyHintsAreNeverRoundedToAttributeOrBinding()
    {
        string source = Root + "<TextBlock Text='{Binding Name}'><TextBlock.Tag><Binding Path='Name'/></TextBlock.Tag></TextBlock></Grid>";
        int attribute = source.IndexOf("Text=", StringComparison.Ordinal);
        int binding = source.IndexOf("<Binding", StringComparison.Ordinal);
        foreach (int offset in new[] { attribute - 1, attribute + 1, attribute + 5, attribute + 6, binding - 1, binding + 2 })
            Assert.Null(Locate(source, offset).Location);
    }

    [Theory]
    [InlineData("<Grid>")]
    [InlineData("<!DOCTYPE Grid [<!ENTITY e SYSTEM 'file:///secret'>]><Grid Text='{Binding Name}'/>")]
    [InlineData("<Grid><!-- Text='{Binding Name}' --></Grid>")]
    public void MalformedXmlDtdAndCommentAreUnavailable(string source)
    {
        int hint = Math.Max(0, source.IndexOf("Text=", StringComparison.Ordinal));
        Assert.Null(Locate(source, hint).Location);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(99, 1)]
    [InlineData(1, int.MaxValue)]
    public void InvalidPositionsAreUnavailable(int line, int column) =>
        Assert.Null(XamlBindingSourceLocator.Locate(File, Root + "</Grid>", line, column, "Binding").Location);

    private static XamlRuntimeSourceResult Locate(string source, int offset, string kind = "Binding")
    {
        int line = 1, column = 1;
        for (int i = 0; i < offset; i++)
        {
            if (source[i] == '\r') { if (i + 1 < offset && source[i + 1] == '\n') i++; line++; column = 1; }
            else if (source[i] == '\n') { line++; column = 1; }
            else column++;
        }
        return XamlBindingSourceLocator.Locate(File, source, line, column, kind);
    }
}
