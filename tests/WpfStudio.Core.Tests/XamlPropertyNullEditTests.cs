using System.Text;
using System.Xml.Linq;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class XamlPropertyNullEditTests
{
    private const string Path = @"C:\Project\View.xaml";
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string Language = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NullUsesTheActualInScopeLanguageAliasAndPreservesUnrelatedSource()
    {
        string text = Wrap("<Button Content='old' Tag='&amp;' /><!--keep-->", "q");
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { SetNull = true });
        Assert.Equal(text.Replace("Content='old'", "Content='{q:Null}'", StringComparison.Ordinal), Apply(text, result));
        Assert.Contains("XAML null", result.Explanation);
        Assert.Contains("local assignment", result.Explanation);
    }

    [Fact]
    public void NullEmptyLiteralAndRemovalAreThreeDifferentEdits()
    {
        string text = Wrap("<Button Content='old'/>");
        var request = Request(text);
        Assert.Equal("{q:Null}", Content(Apply(text, XamlPropertyEditService.CreateEdit(request with { SetNull = true }))));
        Assert.Equal("", Content(Apply(text, XamlPropertyEditService.CreateEdit(request with { LiteralValue = "" }))));
        Assert.Null(Content(Apply(text, XamlPropertyEditService.CreateEdit(request with { ClearLocalValue = true }))));
        var conflict = XamlPropertyEditService.CreateEdit(request with { SetNull = true, ClearLocalValue = true });
        Assert.False(conflict.Success);
        Assert.Contains("different actions", conflict.Error);
    }

    [Fact]
    public void LiteralNullMarkupRemainsLiteralText()
    {
        string text = Wrap("<Button/>");
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { LiteralValue = "{q:Null}" });
        Assert.Equal("{}{q:Null}", Content(Apply(text, result)));
    }

    [Theory]
    [InlineData("<Button xmlns:q='urn:wrong'/>", "q", false)]
    [InlineData("<Button xmlns:q='urn:wrong'/>", null, false)]
    [InlineData("<Button xmlns:q='urn:wrong' xmlns:z='http://schemas.microsoft.com/winfx/2006/xaml'/>", "q", true)]
    public void ShadowedOrMissingAliasesAreNeverInvented(string body, string? alias, bool expected)
    {
        string text = Wrap(body, alias);
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { SetNull = true });
        Assert.Equal(expected, result.Success);
        if (expected) Assert.Equal("{z:Null}", Content(Apply(text, result)));
        else Assert.Contains("namespace alias", result.Error);
    }

    [Theory]
    [InlineData("<Button Content='{Binding Name}'/>", true, false)]
    [InlineData("<Button Content='{DynamicResource Value}'/>", true, false)]
    [InlineData("<Button><Button.Content><!--keep--><TextBlock Text='old'/></Button.Content></Button>", false, true)]
    public void NullStillRequiresExplicitReplacementOfExpressionsAndObjects(string body, bool expression, bool objectValue)
    {
        string text = Wrap(body);
        var request = Request(text) with { SetNull = true };
        var blocked = XamlPropertyEditService.CreateEdit(request);
        Assert.False(blocked.Success);
        Assert.Equal(expression, blocked.ReplacesExpression);
        Assert.Equal(objectValue, blocked.ReplacesObjectValue);
        var replaced = XamlPropertyEditService.CreateEdit(request with { ReplaceExistingValue = true });
        string after = Apply(text, replaced);
        Assert.Equal("{q:Null}", Content(after));
        if (objectValue)
        {
            Assert.Contains("<!--keep-->", after);
            Assert.DoesNotContain("<TextBlock", after);
        }
    }

    [Fact]
    public void ExistingNullKeepsItsAliasAndEntitiesWhenReplacementIsExplicit()
    {
        string text = Wrap("<Button xmlns:longAlias='" + Language + "' Content='&#123;longAlias:Null}'/>");
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { SetNull = true, ReplaceExistingValue = true });
        Assert.Equal(text, Apply(text, result));
        Assert.Empty(result.Edit!.Edits);
    }

    [Fact]
    public void NullInSharedTemplateRetainsTheAllInstancesExplanation()
    {
        string text = Wrap("<ControlTemplate><Button Content='old'/></ControlTemplate>");
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { SetNull = true });
        Assert.True(result.AffectsTemplate);
        Assert.Contains("every instance", result.Explanation);
        Assert.Equal("{q:Null}", Content(Apply(text, result)));
    }

    [Fact]
    public void ExactLocatorSpanFeedsACompleteMultilineStartTagToTheEditService()
    {
        string text = Wrap("\r\n  <Button\r\n      Content='old'\r\n      Tag='keep > marker' />\r\n");
        var location = XamlRuntimeSourceLocator.Locate(Path, text, 2, 3, "System.Windows.Controls.Button", null);
        var request = new XamlPropertyEditRequest(Path, text, 7, location.Element!, 7, DocumentStore.Hash(Encoding.UTF8.GetBytes(text)),
            "Content", null, OwnerType: "System.Windows.Controls.ContentControl", OwnerAssembly: "PresentationFramework", ContentProperty: "Content", SetNull: true);
        var result = XamlPropertyEditService.CreateEdit(request);
        Assert.Equal(text.Replace("Content='old'", "Content='{q:Null}'", StringComparison.Ordinal), Apply(text, result));
    }

    private static string Wrap(string body, string? alias = "q") =>
        $"<Grid xmlns='{Presentation}'{(alias is null ? "" : $" xmlns:{alias}='{Language}'")}>{body}</Grid>";

    private static XamlPropertyEditRequest Request(string text)
    {
        int start = text.IndexOf("<Button", StringComparison.Ordinal);
        var location = XamlRuntimeSourceLocator.Locate(Path, text, 1, start + 1, "System.Windows.Controls.Button", null);
        Assert.NotNull(location.Element);
        return new(Path, text, 7, location.Element, 7, DocumentStore.Hash(Encoding.UTF8.GetBytes(text)), "Content", null,
            OwnerType: "System.Windows.Controls.ContentControl", OwnerAssembly: "PresentationFramework", ContentProperty: "Content");
    }

    private static string Apply(string text, XamlPropertyEditResult result)
    {
        Assert.True(result.Success, result.Error ?? result.Explanation);
        return WorkspaceEditTransaction.ApplyTextEdits(text, result.Edit!.Edits);
    }

    private static string? Content(string text) => XDocument.Parse(text).Descendants(XName.Get("Button", Presentation)).Single().Attribute("Content")?.Value;
}
