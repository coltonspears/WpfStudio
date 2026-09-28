using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class XamlPropertyEditTests
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string Language = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Path = @"C:\Source\View.xaml";

    [Fact]
    public void ExistingAttributePreservesEveryOtherCharacterAndEscapesLiteralOnce()
    {
        var text = Wrap("<!--before-->\r\n<TextBlock Tag=\"😀 &amp; untouched\"  Text = 'old' />\r\n<!--after-->");
        var result = Edit(text, literal: "{x}&<'\"\r\n\t>");
        Assert.True(result.Success, result.Error);
        Assert.Equal(text.Replace("Text = 'old'", "Text = '{}{x}&amp;&lt;&apos;\"&#xD;&#xA;&#x9;&gt;'"), Apply(text, result));
        var edit = Assert.Single(result.Edit!.Edits);
        Assert.Equal(text.IndexOf("old", StringComparison.Ordinal), edit.Start);
        Assert.Equal(3, edit.Length);
        Assert.Equal(7, result.Edit.Version);
        Assert.Equal(DocumentStore.Hash(Encoding.UTF8.GetBytes(text)), result.Edit.ExpectedTextHash);
    }

    [Theory]
    [InlineData("&amp;", "&")]
    [InlineData("&#65;", "A")]
    [InlineData("{} {literal}", " {literal}")]
    [InlineData("{}{}", "{}")]
    public void ExistingEquivalentLiteralRetainsOriginalEntitySpelling(string authored, string value)
    {
        var text = Wrap($"<TextBlock Text='{authored}'/>");
        var result = Edit(text, literal: value);
        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Edit!.Edits);
        Assert.Equal(text, Apply(text, result));
    }

    [Fact]
    public void NewAttributeCopiesQuoteIndentAndCrlfStyle()
    {
        var text = Wrap("<TextBlock\r\n    Tag='keep'\r\n    />");
        var result = Edit(text, literal: "new");
        Assert.Equal(text.Replace("Tag='keep'", "Tag='keep'\r\n    Text='new'"), Apply(text, result));
        Assert.Contains("local literal override", result.Explanation);
    }

    [Fact]
    public void ExactSourceSpanChoosesSecondIdenticalSiblingWithoutRenaming()
    {
        var text = Wrap("<TextBlock x:Name='First' Text='old'/><TextBlock x:Name='Second' Text='old'/>");
        var result = XamlPropertyEditService.CreateEdit(Request(text, occurrence: 1));
        Assert.Equal(Wrap("<TextBlock x:Name='First' Text='old'/><TextBlock x:Name='Second' Text='new'/>"), Apply(text, result));
    }

    [Fact]
    public void StaleSnapshotAndMismatchedSourceLocationsRefuseEdits()
    {
        var text = Wrap("\r\n  <TextBlock Text='old'/>");
        var request = Request(text);
        var variants = new[]
        {
            request with { ElementSourceVersion = 6 },
            request with { ElementSourceHash = "not-the-source-hash" },
            request with { Text = text.Replace("old", "changed") },
            request with { Element = request.Element with { Path = @"C:\Source\Other.xaml" } },
            request with { Element = request.Element with { Start = request.Element.Start + 1 } },
            request with { Element = request.Element with { Length = request.Element.Length - 1 } },
            request with { Element = request.Element with { Line = request.Element.Line + 1 } },
            request with { Element = request.Element with { Column = request.Element.Column + 1 } },
            request with { Element = request.Element with { DisplayText = "Button" } }
        };
        foreach (var variant in variants) AssertRejected(XamlPropertyEditService.CreateEdit(variant));
    }

    [Theory]
    [InlineData("<TextBlock Text='one' Text='two'/>")]
    [InlineData("<TextBlock><Run></TextBlock>")]
    [InlineData("<TextBlock unknown:Value='x'/>")]
    public void MalformedOrAmbiguousXmlRefusesEdits(string element)
    {
        AssertRejected(Edit(Wrap(element)));
    }

    [Fact]
    public void DtdIsNeverExpanded()
    {
        var text = "<!DOCTYPE Grid [<!ENTITY dangerous SYSTEM 'file:///C:/secret'>]>" + Wrap("<TextBlock Text='&dangerous;'/>");
        AssertRejected(Edit(text));
    }

    [Fact]
    public void InvalidXmlCharactersRefuseEdits()
    {
        AssertRejected(Edit(Wrap("<TextBlock/>"), literal: "\u0001"));
        // Attribute metadata serializes invalid Unicode as replacement characters;
        // construct the malformed string at runtime to exercise the real API boundary.
        AssertRejected(Edit(Wrap("<TextBlock/>"), literal: new string('\uD800', 1)));
        AssertRejected(Edit(Wrap("<TextBlock/>"), literal: new string('\uDC00', 1)));
    }

    [Fact]
    public void BindingReplacementRequiresExplicitIntentAndClearIsExplicit()
    {
        var text = Wrap("<TextBlock Text='{Binding Customer.Name}'/>");
        var request = Request(text);
        var rejected = XamlPropertyEditService.CreateEdit(request);
        AssertRejected(rejected);
        Assert.True(rejected.ReplacesExpression);
        var replaced = XamlPropertyEditService.CreateEdit(request with { ReplaceExistingValue = true });
        Assert.True(replaced.ReplacesExpression);
        Assert.Equal(Wrap("<TextBlock Text='new'/>"), Apply(text, replaced));
        var cleared = XamlPropertyEditService.CreateEdit(request with { ClearLocalValue = true, LiteralValue = null });
        Assert.True(cleared.ReplacesExpression);
        Assert.Equal(Wrap("<TextBlock/>"), Apply(text, cleared));
    }

    [Theory]
    [InlineData("{StaticResource Label}")]
    [InlineData("{DynamicResource Label}")]
    public void AuthoredResourceExpressionsRequireReplacementIntent(string expression)
    {
        var text = Wrap($"<TextBlock Text='{expression}'/>");
        AssertRejected(Edit(text));
        Assert.True(XamlPropertyEditService.CreateEdit(Request(text) with { ReplaceExistingValue = true }).ReplacesExpression);
    }

    [Fact]
    public void ScalarPropertyElementBecomesLiteralAttributeAndRetainsUnrelatedProperties()
    {
        var text = Wrap("<TextBlock><TextBlock.Text>old &amp; value</TextBlock.Text><!--keep--><TextBlock.Tag>other</TextBlock.Tag></TextBlock>");
        var result = Edit(text, literal: "{literal}");
        Assert.Equal(Wrap("<TextBlock Text='{}{literal}'><!--keep--><TextBlock.Tag>other</TextBlock.Tag></TextBlock>"), Apply(text, result));
        Assert.False(result.ReplacesObjectValue);
    }

    [Fact]
    public void ObjectPropertyReplacementPreservesCommentsAndReportsRemovedExpression()
    {
        var text = Wrap("<TextBlock><TextBlock.Text><!--explain--><Binding Path='Name'/><!--after--></TextBlock.Text></TextBlock>");
        AssertRejected(Edit(text));
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { ReplaceExistingValue = true });
        Assert.True(result.ReplacesObjectValue);
        Assert.True(result.ReplacesExpression);
        Assert.Equal(Wrap("<TextBlock Text='new'><!--explain--><!--after--></TextBlock>"), Apply(text, result));
    }

    [Fact]
    public void ClearPropertyElementPreservesSiblingAndComments()
    {
        var text = Wrap("<TextBlock><TextBlock.Text><!--keep-->old</TextBlock.Text><TextBlock.Tag>other</TextBlock.Tag></TextBlock>");
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { ClearLocalValue = true, LiteralValue = null });
        Assert.Equal(Wrap("<TextBlock><!--keep--><TextBlock.Tag>other</TextBlock.Tag></TextBlock>"), Apply(text, result));
    }

    [Fact]
    public void ClearingAbsentLocalValueDoesNotRewriteStyleOrInheritedValue()
    {
        var text = Wrap("<TextBlock Style='{StaticResource TextStyle}'/>");
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { ClearLocalValue = true, LiteralValue = null });
        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Edit!.Edits);
        Assert.Contains("No authored local value", result.Explanation);
    }

    [Theory]
    [InlineData("<TextBlock Text='one'><TextBlock.Text>two</TextBlock.Text></TextBlock>")]
    [InlineData("<TextBlock><TextBlock.Text>one</TextBlock.Text><TextBlock.Text>two</TextBlock.Text></TextBlock>")]
    [InlineData("<TextBlock Text='one'>two</TextBlock>")]
    [InlineData("<TextBlock Text='one'><Run Text='two'/></TextBlock>")]
    [InlineData("<TextBlock Text='one' xml:space='preserve'> </TextBlock>")]
    public void CompetingLocalDeclarationsAreNotSilentlyCombined(string element)
    {
        AssertRejected(XamlPropertyEditService.CreateEdit(Request(Wrap(element)) with { ReplaceExistingValue = true }));
    }

    [Fact]
    public void AttachedPropertyRequiresProvenOwnerIdentityAndUsesExistingAlias()
    {
        var text = Wrap("<TextBlock/>", "xmlns:p='clr-namespace:Demo.Layout;assembly=Layout'");
        var request = Attached(text, "p:Provider.Row", "Demo.Layout.Provider", "Layout");
        Assert.Equal(Wrap("<TextBlock p:Provider.Row='new'/>", "xmlns:p='clr-namespace:Demo.Layout;assembly=Layout'"), Apply(text, XamlPropertyEditService.CreateEdit(request)));
        AssertRejected(XamlPropertyEditService.CreateEdit(request with { OwnerAssembly = "Other" }));
        AssertRejected(XamlPropertyEditService.CreateEdit(request with { Property = "p:Wrong.Row" }));
    }

    [Fact]
    public void DefaultPresentationNamespaceProvesWpfAttachedOwner()
    {
        var text = Wrap("<TextBlock Grid.Row='1'/>");
        var request = Attached(text, "Grid.Row", "System.Windows.Controls.Grid", "PresentationFramework") with { LiteralValue = "2" };
        Assert.Equal(Wrap("<TextBlock Grid.Row='2'/>"), Apply(text, XamlPropertyEditService.CreateEdit(request)));
    }

    [Fact]
    public void AssemblylessClrNamespaceRequiresEvaluatedSourceAssembly()
    {
        var text = Wrap("<TextBlock/>", "xmlns:p='clr-namespace:Demo.Layout'");
        var request = Attached(text, "Provider.Row", "Demo.Layout.Provider", "Demo");
        AssertRejected(XamlPropertyEditService.CreateEdit(request));
        AssertRejected(XamlPropertyEditService.CreateEdit(request with { SourceAssembly = "Other" }));
        Assert.Equal(Wrap("<TextBlock p:Provider.Row='new'/>", "xmlns:p='clr-namespace:Demo.Layout'"), Apply(text, XamlPropertyEditService.CreateEdit(request with { SourceAssembly = "Demo" })));
    }

    [Fact]
    public void PrefixRedeclarationCannotRedirectAttachedProperty()
    {
        var text = Wrap("<TextBlock xmlns:p='clr-namespace:Other;assembly=Other' p:Provider.Row='old'/>", "xmlns:p='clr-namespace:Demo.Layout;assembly=Layout'");
        AssertRejected(XamlPropertyEditService.CreateEdit(Attached(text, "Provider.Row", "Demo.Layout.Provider", "Layout")));
    }

    [Fact]
    public void IncorrectRequestedAliasRefusesEvenWhenAnExistingDeclarationMatchesCanonicalOwner()
    {
        var text = Wrap("<TextBlock a:Provider.Row='old'/>", "xmlns:a='clr-namespace:Demo.Layout;assembly=Layout' xmlns:p='clr-namespace:Other;assembly=Other'");
        AssertRejected(XamlPropertyEditService.CreateEdit(Attached(text, "p:Provider.Row", "Demo.Layout.Provider", "Layout")));
    }

    [Theory]
    [InlineData("<TextBlock a:Added.Flag='true'/>")]
    [InlineData("<TextBlock a:Base.Flag='true'/>")]
    [InlineData("<TextBlock Flag='true'/>")]
    [InlineData("<TextBlock><a:Added.Flag>true</a:Added.Flag></TextBlock>")]
    public void UnprovenAddOwnerAliasCannotCreateDuplicateDependencyProperty(string element)
    {
        // Even distinct CLR namespace/assembly names cannot prove distinct DP identity:
        // Added.Flag may expose Base.FlagProperty.AddOwner(typeof(Added)).
        var text = Wrap(element, "xmlns:a='clr-namespace:Other;assembly=Other' xmlns:b='clr-namespace:Demo;assembly=Demo'");
        AssertRejected(XamlPropertyEditService.CreateEdit(Attached(text, "Base.Flag", "Demo.Base", "Demo")));
    }

    [Fact]
    public void PropertyElementUsesItsOwnNamespaceScope()
    {
        var text = Wrap("<TextBlock><p:Provider.Row xmlns:p='clr-namespace:Demo.Layout;assembly=Layout'>old</p:Provider.Row></TextBlock>", "xmlns:p='clr-namespace:Other;assembly=Other' xmlns:layout='clr-namespace:Demo.Layout;assembly=Layout'");
        var request = Attached(text, "Provider.Row", "Demo.Layout.Provider", "Layout");
        Assert.Equal(Wrap("<TextBlock layout:Provider.Row='new'></TextBlock>", "xmlns:p='clr-namespace:Other;assembly=Other' xmlns:layout='clr-namespace:Demo.Layout;assembly=Layout'"), Apply(text, XamlPropertyEditService.CreateEdit(request)));
    }

    [Fact]
    public void ObjectOwnerQualifiedInheritedWrapperCanBeReplaced()
    {
        var text = Wrap("<Button><Button.Width>20</Button.Width></Button>");
        var request = Request(text, "Button", "Width", "50") with { OwnerType = "System.Windows.FrameworkElement", ContentProperty = "Content" };
        Assert.Equal(Wrap("<Button Width='50'></Button>"), Apply(text, XamlPropertyEditService.CreateEdit(request)));
    }

    [Fact]
    public void UnknownQualifiedInheritedOwnerRefusesDuplicateNormalWrapper()
    {
        var text = Wrap("<Button Control.Foreground='Red'/>");
        var request = Request(text, "Button", "Foreground", "Blue") with { OwnerType = "System.Windows.Documents.TextElement", ContentProperty = "Content" };
        AssertRejected(XamlPropertyEditService.CreateEdit(request));
    }

    [Fact]
    public void TemplateEditClearlyReportsSharedAuthoredDeclaration()
    {
        var text = Wrap("<Grid.Resources><DataTemplate x:Key='row'><TextBlock Text='old'/></DataTemplate></Grid.Resources>");
        var result = Edit(text);
        Assert.True(result.AffectsTemplate);
        Assert.Contains("every instance", result.Explanation);
    }

    [Fact]
    public void ButtonContentReplacesImplicitTextWhilePreservingResourcesAndComments()
    {
        var text = Wrap("<Button><Button.Resources><Style x:Key='s'/></Button.Resources><!--keep-->old &amp; value</Button>");
        var result = XamlPropertyEditService.CreateEdit(ContentRequest(text));
        Assert.Equal(Wrap("<Button Content='new'><Button.Resources><Style x:Key='s'/></Button.Resources><!--keep--></Button>"), Apply(text, result));
    }

    [Fact]
    public void ButtonContentObjectRequiresExplicitReplacementAndRemovalPreservesComments()
    {
        var text = Wrap("<Button><!--before--><StackPanel><!--inside--><TextBlock Text='old'/></StackPanel><!--after--></Button>");
        AssertRejected(XamlPropertyEditService.CreateEdit(ContentRequest(text)));
        var request = ContentRequest(text) with { ReplaceExistingValue = true };
        var result = XamlPropertyEditService.CreateEdit(request);
        Assert.True(result.ReplacesObjectValue);
        Assert.Equal(Wrap("<Button Content='new'><!--before--><!--inside--><!--after--></Button>"), Apply(text, result));
        var removed = XamlPropertyEditService.CreateEdit(request with { ClearLocalValue = true });
        Assert.Equal(Wrap("<Button><!--before--><!--inside--><!--after--></Button>"), Apply(text, removed));
    }

    [Theory]
    [InlineData("old text")]
    [InlineData("<Run Text='old'/>")]
    [InlineData("<TextBlock.Inlines><Run Text='old'/></TextBlock.Inlines>")]
    [InlineData("<![CDATA[old text]]>")]
    public void TextBlockTextReplacesInlineContentWithoutDisturbingResources(string content)
    {
        var resources = "<TextBlock.Resources><Style x:Key='s'/></TextBlock.Resources>";
        var text = Wrap("<TextBlock>" + resources + "<!--keep-->" + content + "</TextBlock>");
        var result = XamlPropertyEditService.CreateEdit(Request(text) with { ReplaceExistingValue = true });
        Assert.Equal(Wrap("<TextBlock Text='new'>" + resources + "<!--keep--></TextBlock>"), Apply(text, result));
    }

    [Fact]
    public void InheritedPreserveSpaceMakesWhitespaceARealLocalContentValue()
    {
        var text = Wrap("<TextBlock> \t </TextBlock>", "xml:space='preserve'");
        var result = Edit(text);
        Assert.Equal(Wrap("<TextBlock Text='new'></TextBlock>", "xml:space='preserve'"), Apply(text, result));
        Assert.Contains("implicit content", result.Explanation);
    }

    [Fact]
    public void UnknownContentIdentityRefusesEditsBesideImplicitContent()
    {
        var text = Wrap("<TextBlock>old</TextBlock>");
        AssertRejected(XamlPropertyEditService.CreateEdit(Request(text, property: "Width") with { ContentProperty = null, OwnerType = "System.Windows.FrameworkElement" }));
    }

    [Fact]
    public void NonContentPropertyDoesNotChangeKnownImplicitContent()
    {
        var text = Wrap("<TextBlock><Run Text='old'/></TextBlock>");
        var request = Request(text, property: "Width", literal: "40") with { OwnerType = "System.Windows.FrameworkElement" };
        Assert.Equal(Wrap("<TextBlock Width='40'><Run Text='old'/></TextBlock>"), Apply(text, XamlPropertyEditService.CreateEdit(request)));
    }

    [Fact]
    public void DirectiveAndMissingMetadataCannotBeEditedAsClrProperty()
    {
        var text = Wrap("<TextBlock x:Name='name'/>");
        AssertRejected(XamlPropertyEditService.CreateEdit(Request(text, property: "Name")));
        AssertRejected(XamlPropertyEditService.CreateEdit(Request(text, property: "xmlns")));
        AssertRejected(XamlPropertyEditService.CreateEdit(Request(text) with { OwnerType = null }));
        AssertRejected(XamlPropertyEditService.CreateEdit(Request(text) with { OwnerAssembly = null }));
    }

    private static string Wrap(string body, string? extra = null) => $"<Grid xmlns='{Presentation}' xmlns:x='{Language}'{(extra is null ? "" : " " + extra)}>{body}</Grid>";
    private static XamlPropertyEditResult Edit(string text, string literal = "new") => XamlPropertyEditService.CreateEdit(Request(text, literal: literal));
    private static XamlPropertyEditRequest ContentRequest(string text) => Request(text, "Button", "Content") with { OwnerType = "System.Windows.Controls.ContentControl", ContentProperty = "Content" };
    private static XamlPropertyEditRequest Attached(string text, string property, string owner, string assembly) => Request(text, property: property) with { OwnerType = owner, OwnerAssembly = assembly, IsAttached = true };
    private static XamlPropertyEditRequest Request(string text, string tag = "TextBlock", string property = "Text", string literal = "new", int occurrence = 0)
    {
        var start = -1;
        for (var count = 0; count <= occurrence; count++) start = text.IndexOf("<" + tag, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = start + 1;
        var quote = '\0';
        for (; end < text.Length; end++)
        {
            var current = text[end];
            if (quote != '\0') { if (current == quote) quote = '\0'; }
            else if (current is '\'' or '"') quote = current;
            else if (current == '>') { end++; break; }
        }
        var line = 1; var lineStart = 0;
        for (var i = 0; i < start; i++)
        {
            if (text[i] == '\r') { if (i + 1 < start && text[i + 1] == '\n') i++; line++; lineStart = i + 1; }
            else if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        var source = new SourceLocation(Path, start, end - start, line, start - lineStart + 1, tag);
        return new XamlPropertyEditRequest(Path, text, 7, source, 7, DocumentStore.Hash(Encoding.UTF8.GetBytes(text)), property, literal,
            OwnerType: "System.Windows.Controls.TextBlock", OwnerAssembly: "PresentationFramework", ContentProperty: "Inlines");
    }
    private static string Apply(string text, XamlPropertyEditResult result)
    {
        Assert.True(result.Success, result.Error ?? result.Explanation);
        return WorkspaceEditTransaction.ApplyTextEdits(text, result.Edit!.Edits);
    }
    private static void AssertRejected(XamlPropertyEditResult result)
    {
        Assert.False(result.Success);
        Assert.Null(result.Edit);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
