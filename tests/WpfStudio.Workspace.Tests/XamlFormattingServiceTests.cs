using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlFormattingServiceTests
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:c='clr-namespace:FormatFixture'";
    private static readonly XamlFormattingService Service = new();
    private static readonly Lazy<CSharpCompilation> Fixture = new(CreateCompilation);

    [Fact]
    public void MinifiedKnownContainersBecomeReadableWithoutReorderingValues()
    {
        var before = $"<Grid xmlns='{Presentation}'><StackPanel><Button Content='Save'/><TextBlock Text='Ready'/></StackPanel></Grid>";
        var (after, result) = Format(before, Fixture.Value);
        Assert.True(result.Accepted);
        Assert.Contains("><StackPanel", before);
        Assert.Contains(">\n    <StackPanel>\n        <Button Content='Save' />\n        <TextBlock Text='Ready' />\n    </StackPanel>\n</Grid>", after);
        Assert.Equal(ElementsAndAttributes(before), ElementsAndAttributes(after));
        Assert.Empty(Service.Format(after, Fixture.Value).Edits);
        Assert.All(result.Edits, edit => Assert.True(before.AsSpan(edit.Start, edit.Length).ToString().All(char.IsWhiteSpace)));
    }

    [Fact]
    public void AttributeOrderQuotesEntitiesAndMarkupExtensionStringsStayExact()
    {
        var before = $"<Button xmlns='{Presentation}'  Tag = \"A&amp;B &#x1F600;\" Content = '{{Binding Path=Name, StringFormat=\"Hello, {{0}}\"}}' ToolTip = 'x &lt; y'/>";
        var (after, result) = Format(before);
        Assert.True(result.Accepted);
        Assert.Contains("Tag=\"A&amp;B &#x1F600;\"", after);
        Assert.Contains("Content='{Binding Path=Name, StringFormat=\"Hello, {0}\"}'", after);
        Assert.Contains("ToolTip='x &lt; y'", after);
        Assert.True(after.IndexOf("Tag=", StringComparison.Ordinal) < after.IndexOf("Content=", StringComparison.Ordinal));
        Assert.Equal(ElementsAndAttributes(before), ElementsAndAttributes(after));
        Assert.Empty(Service.Format(after).Edits);
    }

    [Fact]
    public void LongAndExistingMultilineTagsWrapAttributesWithoutTouchingValueNewlines()
    {
        var before = $"<Grid xmlns='{Presentation}'>\r\n<Button\r\n  Content = 'first\nsecond'\r\n  ToolTip = 'detail'\r\n/>\r\n</Grid>";
        var (after, _) = Format(before, options: new(IndentSize: 2, AttributeWrapColumn: 60));
        Assert.Contains("\r\n  <Button\r\n    Content='first\nsecond'\r\n    ToolTip='detail'\r\n  />", after);
        Assert.Empty(Service.Format(after, options: new(IndentSize: 2, AttributeWrapColumn: 60)).Edits);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void ExistingLineEndingsAndRequestedTabsArePreserved(string newline)
    {
        var before = $"<Grid xmlns='{Presentation}'>{newline}<StackPanel><Button/></StackPanel>{newline}</Grid>";
        var (after, _) = Format(before, options: new(UseTabs: true));
        Assert.Contains(newline + "\t<StackPanel>" + newline + "\t\t<Button />" + newline + "\t</StackPanel>", after);
        Assert.Empty(Service.Format(after, options: new(UseTabs: true)).Edits);
    }

    [Fact]
    public void InlineAdjacencyAndMixedTextHaveIdenticalTextNodeSequences()
    {
        var before = $"<StackPanel {Ns}><TextBlock>Hello<Run Text='world'/><Run Text='!'/></TextBlock><TextBlock><Run>A</Run> <Run>B</Run></TextBlock><TextBlock>中文\n字符<Span> end </Span></TextBlock></StackPanel>";
        var (after, _) = Format(before, Fixture.Value);
        var oldBlocks = XDocument.Parse(before, LoadOptions.PreserveWhitespace).Descendants().Where(element => element.Name.LocalName == "TextBlock").ToArray();
        var newBlocks = XDocument.Parse(after, LoadOptions.PreserveWhitespace).Descendants().Where(element => element.Name.LocalName == "TextBlock").ToArray();
        Assert.Equal(oldBlocks.Length, newBlocks.Length);
        for (int i = 0; i < oldBlocks.Length; i++) Assert.Equal(TextNodes(oldBlocks[i]), TextNodes(newBlocks[i]));
        Assert.Contains("<Run Text='world' /><Run Text='!' />", after);
        Assert.Contains("<Run>A</Run> <Run>B</Run>", after);
    }

    [Fact]
    public void ExplicitInlineCollectionCannotGainWhitespaceBetweenChildren()
    {
        var before = $"<TextBlock {Ns}><TextBlock.Inlines><Run>A</Run><Run>B</Run></TextBlock.Inlines></TextBlock>";
        var (after, _) = Format(before, Fixture.Value);
        Assert.Contains("<TextBlock.Inlines><Run>A</Run><Run>B</Run></TextBlock.Inlines>", after);
    }

    [Fact]
    public void XmlSpacePreserveIsConservativeAcrossDescendantsIncludingDefaultReset()
    {
        var before = $"<Grid {Ns} xml:space='preserve'> \n <StackPanel xml:space='default'><Button/> <Button/></StackPanel>\t </Grid>";
        var (after, _) = Format(before, Fixture.Value);
        Assert.Equal(TextNodes(XDocument.Parse(before, LoadOptions.PreserveWhitespace).Root!), TextNodes(XDocument.Parse(after, LoadOptions.PreserveWhitespace).Root!));
        Assert.Contains("<Button /> <Button />", after);
    }

    [Fact]
    public void CommentsProcessingInstructionsAndCdataPayloadsStayByteForByte()
    {
        var before = $"<?xml version='1.0'?><!-- first\n  note --><?designer keep='x'?><Grid {Ns}><!-- A  <b/> --><TextBlock><![CDATA[ A\n <Run/> & B ]]></TextBlock><Button/><!-- end --></Grid>";
        var (after, _) = Format(before, Fixture.Value);
        foreach (string chunk in new[] { "<?xml version='1.0'?>", "<!-- first\n  note -->", "<?designer keep='x'?>", "<!-- A  <b/> -->", "<![CDATA[ A\n <Run/> & B ]]>", "<!-- end -->" })
            Assert.Contains(chunk, after);
        Assert.Contains("<TextBlock><![CDATA[ A\n <Run/> & B ]]></TextBlock>", after);
        Assert.Equal(ElementsAndAttributes(before), ElementsAndAttributes(after));
    }

    [Fact]
    public void OpaqueXDataAndUnprovenCustomContentPreserveAllDescendantBytes()
    {
        string raw = "<Grid  key = 'value'><Button  name='a'/><Button/></Grid>";
        var before = $"<Grid {Ns}><x:XData>{raw}</x:XData><c:Unknown>{raw}</c:Unknown></Grid>";
        var (after, result) = Format(before, Fixture.Value);
        Assert.True(result.Accepted);
        Assert.Contains("<x:XData>" + raw + "</x:XData>", after);
        Assert.Contains("<c:Unknown>" + raw + "</c:Unknown>", after);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void CompilationIsAuthorityEvenWhenOfflineNamesLookLikeFrameworkControls()
    {
        var before = $"<Grid xmlns='{Presentation}'><Button/><Button/></Grid>";
        var compilation = CSharpCompilation.Create("MissingFramework", references: []);
        var (offline, _) = Format(before);
        var (unresolved, _) = Format(before, compilation);
        Assert.Contains(">\n    <Button", offline);
        Assert.Contains("><Button/><Button/></Grid>", unresolved);
    }

    [Fact]
    public void PendingMetadataCanDisableOfflineContentAssumptionsWhileFormattingOuterTrivia()
    {
        var before = $"<Grid xmlns = '{Presentation}'  Tag = 'outer'><Button  Content = 'A'/><Button/></Grid>";
        var options = new XamlFormattingOptions(UseKnownFrameworkContent: false);
        var (conservative, result) = Format(before, options: options);
        var standalone = Format(before, options: new(UseKnownFrameworkContent: true)).Text;
        Assert.Contains($"xmlns='{Presentation}' Tag='outer'", conservative);
        Assert.Contains("><Button  Content = 'A'/><Button/></Grid>", conservative);
        Assert.NotEmpty(result.Warnings);
        Assert.Empty(Service.Format(conservative, options: options).Edits);
        Assert.Contains(">\n    <Button Content='A' />\n    <Button />\n</Grid>", standalone);

        // The switch only controls unavailable metadata, not a supplied compiler model.
        var compiled = Format(before, Fixture.Value, options).Text;
        Assert.Contains(">\n    <Button Content='A' />\n    <Button />\n</Grid>", compiled);
    }

    [Fact]
    public void NamespaceAliasesResolveThroughActualCompilationMetadata()
    {
        var before = "<p:Grid xmlns:p='clr-namespace:System.Windows.Controls;assembly=PresentationFramework'><p:Button/><p:Button/></p:Grid>";
        var (after, _) = Format(before, Fixture.Value);
        Assert.Contains(">\n    <p:Button />\n    <p:Button />\n</p:Grid>", after);
    }

    [Fact]
    public void CompilerKnownCustomAndInheritedContentModelsFormatStructurally()
    {
        var before = $"<Grid {Ns}><c:Box><Button/><Button/></c:Box><c:InheritedPanel><Button/><Button/></c:InheritedPanel></Grid>";
        var (after, _) = Format(before, Fixture.Value);
        Assert.Contains("<c:Box>\n        <Button />\n        <Button />\n    </c:Box>", after);
        Assert.Contains("<c:InheritedPanel>\n        <Button />\n        <Button />\n    </c:InheritedPanel>", after);
    }

    [Fact]
    public void CustomWhitespaceSignificantCollectionAndInheritedInlineContentKeepAdjacency()
    {
        var before = $"<Grid {Ns}><c:InlineBox><Button/><Button/></c:InlineBox><c:InheritedText><Run>A</Run><Run>B</Run></c:InheritedText><c:Box><c:Box.Spaces><Button/><Button/></c:Box.Spaces></c:Box></Grid>";
        var (after, _) = Format(before, Fixture.Value);
        Assert.Contains("<c:InlineBox><Button /><Button /></c:InlineBox>", after);
        Assert.Contains("<c:InheritedText><Run>A</Run><Run>B</Run></c:InheritedText>", after);
        Assert.Contains("<c:Box.Spaces><Button /><Button /></c:Box.Spaces>", after);
    }

    [Fact]
    public void ChangedCompilerCollectionMetadataChangesFormattingWithoutLoadingUserCode()
    {
        var before = $"<c:Box {Ns}><Button/><Button/></c:Box>";
        var ordinary = Format(before, Fixture.Value).Text;
        var tree = Fixture.Value.SyntaxTrees.Single(tree => tree.FilePath == "Custom.cs");
        var changed = Fixture.Value.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(tree.GetText().ToString().Replace("public class Items :", "[WhitespaceSignificantCollection] public class Items :", StringComparison.Ordinal), path: tree.FilePath));
        var significant = Format(before, changed).Text;
        Assert.Contains(">\n    <Button", ordinary);
        Assert.Contains("><Button /><Button /></c:Box>", significant);
    }

    [Fact]
    public void CustomConvertersAndRawSerializationContractsAreOpaque()
    {
        string body = "<Button  Content = 'A'/><Button/>";
        var before = $"<Grid {Ns}><c:Converted>{body}</c:Converted><c:Raw>{body}</c:Raw></Grid>";
        var (after, _) = Format(before, Fixture.Value);
        Assert.Contains("<c:Converted>" + body + "</c:Converted>", after);
        Assert.Contains("<c:Raw>" + body + "</c:Raw>", after);
    }

    [Theory]
    [InlineData("<Grid><Button></Grid>")]
    [InlineData("<Grid Text='unfinished></Grid>")]
    [InlineData("<!DOCTYPE Grid [<!ENTITY value 'text'>]><Grid>&value;</Grid>")]
    [InlineData("<Grid Name='A' Name='B'/>")]
    public void MalformedOrDtdInputIsRejectedWithoutAnyEdits(string text)
    {
        var result = Service.Format(text);
        Assert.False(result.Accepted);
        Assert.Empty(result.Edits);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void BudgetsAndCancellationFailWithoutPartialFormatting()
    {
        foreach (string text in new[] { new string(' ', 1_000_001), string.Concat(Enumerable.Repeat("<Grid>", 130)) + string.Concat(Enumerable.Repeat("</Grid>", 130)), "<Grid>" + string.Concat(Enumerable.Repeat("<Button/>", 16384)) + "</Grid>" })
        {
            var result = Service.Format(text);
            Assert.False(result.Accepted);
            Assert.Empty(result.Edits);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Service.Format("<Grid/>", token: cancellation.Token));
        Assert.False(Service.Format("<Grid/>", options: new(NewLine: " ")).Accepted);
    }

    private static (string Text, XamlFormattingResult Result) Format(string before, Compilation? compilation = null, XamlFormattingOptions? options = null)
    {
        var result = Service.Format(before, compilation, options);
        Assert.True(result.Accepted, string.Join("\n", result.Warnings));
        for (int index = 1; index < result.Edits.Count; index++) Assert.True(result.Edits[index - 1].Start + result.Edits[index - 1].Length <= result.Edits[index].Start);
        string after = before;
        foreach (var edit in result.Edits.Reverse()) after = after.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        return (after, result);
    }
    private static string[] ElementsAndAttributes(string text) => XDocument.Parse(text, LoadOptions.PreserveWhitespace).Descendants()
        .Select(element => element.Name + "|" + string.Join("|", element.Attributes().Select(attribute => attribute.Name + "=" + attribute.Value))).ToArray();
    private static string[] TextNodes(XElement element) => element.DescendantNodes().OfType<XText>().Select(text => text.Value).ToArray();

    private static CSharpCompilation CreateCompilation()
    {
        string dotnetRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        string packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.WindowsDesktop.App.Ref");
        string pack = Directory.GetDirectories(packRoot).OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version) ? version : new Version()).First();
        string referenceDirectory = Directory.GetDirectories(Path.Combine(pack, "ref")).OrderByDescending(path => path, StringComparer.Ordinal).First();
        string[] wpf = Directory.GetFiles(referenceDirectory, "*.dll");
        var names = wpf.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !names.Contains(Path.GetFileName(path))).Concat(wpf).Select(path => MetadataReference.CreateFromFile(path));
        var source = CSharpSyntaxTree.ParseText("""
            using System.Windows.Markup;
            using System.Collections.Generic;
            namespace FormatFixture;
            public class Items : List<object> {}
            [WhitespaceSignificantCollection] public class Spaces : List<object> {}
            [ContentProperty(nameof(Items))] public class Box { public Items Items { get; } = new(); public Spaces Spaces { get; } = new(); }
            [ContentProperty(nameof(Items))] public class InlineBox { public Spaces Items { get; } = new(); }
            public class InheritedPanel : System.Windows.Controls.StackPanel {}
            public class InheritedText : System.Windows.Controls.TextBlock {}
            public class Unknown {}
            [System.ComponentModel.TypeConverter(typeof(Converter)), ContentProperty(nameof(Items))]
            public class Converted { public Items Items { get; } = new(); }
            public class Converter : System.ComponentModel.TypeConverter { public Converter() => throw new System.InvalidOperationException("must not run"); }
            [ContentProperty(nameof(Items))] public class Raw : System.Xml.Serialization.IXmlSerializable {
                public Items Items { get; } = new();
                public System.Xml.Schema.XmlSchema GetSchema() => null;
                public void ReadXml(System.Xml.XmlReader reader) => throw new System.InvalidOperationException("must not run");
                public void WriteXml(System.Xml.XmlWriter writer) => throw new System.InvalidOperationException("must not run");
            }
            """, path: "Custom.cs");
        var compilation = CSharpCompilation.Create("FormattingFixture", [source], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
