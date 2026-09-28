using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace.Tests;

public sealed partial class XamlNameServiceTests
{
    private const string PathName = "Views/Names.xaml";
    private const long Version = 17;
    private const string Namespaces = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:c='clr-namespace:NameFixture' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' mc:Ignorable='d'";
    private static readonly Lazy<CSharpCompilation> Fixture = new(CreateCompilation);
    private static readonly XamlNameService Service = new();

    [Theory]
    [InlineData("<TextBlock Text=\"{Binding Text, ElementName=Input}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding Text, ElementName='Input'}\"/>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding Path='Text' ElementName='Input'/></TextBlock.Text></TextBlock>")]
    public void ForwardDeclarationsResolveInlineQuotedAndObjectBindings(string consumer)
    {
        string text = Window($"<Grid>{consumer}<TextBox x:Name='Input'/></Grid>");
        Assert.Empty(Analyze(text));
        int reference = text.IndexOf("Input", StringComparison.Ordinal);
        var definition = Assert.Single(Service.GetDefinition(PathName, text, reference + 2, Fixture.Value));
        Assert.Equal(text.LastIndexOf("Input", StringComparison.Ordinal), definition.Start);
        Assert.Equal("Input", Slice(text, definition.Start, definition.Length));
        Assert.Equal(PathName, definition.Path);
    }

    [Fact]
    public void NamespaceAliasesAndFrameworkContentElementNamesAreResolvedByMetadata()
    {
        const string text = """
            <p:Window xmlns:p='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:lang='http://schemas.microsoft.com/winfx/2006/xaml'>
              <p:StackPanel>
                <p:RichTextBox><p:FlowDocument><p:Paragraph Name='Paragraph'/></p:FlowDocument></p:RichTextBox>
                <p:TextBox lang:Name='Input'/>
                <p:TextBlock Text='{p:Binding Text, ElementName=Input}'/>
                <p:TextBlock Tag='{p:Binding ElementName=Paragraph}'/>
              </p:StackPanel>
            </p:Window>
            """;
        Assert.Empty(Analyze(text));
        foreach (string name in new[] { "Input", "Paragraph" })
        {
            int position = text.LastIndexOf("ElementName=" + name, StringComparison.Ordinal) + "ElementName=".Length;
            var definition = Assert.Single(Service.GetDefinition(PathName, text, position, Fixture.Value));
            Assert.Equal(name, Slice(text, definition.Start, definition.Length));
            Assert.True(definition.Start < position);
        }
    }

    [Fact]
    public void NamesAreCaseSensitiveAndValidUnicodeIdentifiersRemainDistinct()
    {
        string text = Window("<StackPanel><TextBox x:Name='Input'/><TextBox x:Name='input'/><TextBox x:Name='Éditeur'/><TextBlock Tag='{Binding ElementName=input}'/></StackPanel>");
        Assert.Empty(Analyze(text));
        var definition = Assert.Single(Service.GetDefinition(PathName, text, text.LastIndexOf("input", StringComparison.Ordinal), Fixture.Value));
        Assert.Equal(text.IndexOf("input", StringComparison.Ordinal), definition.Start);
        Assert.Equal("input", Slice(text, definition.Start, definition.Length));
    }

    [Theory]
    [InlineData("_", true)]
    [InlineData("\u2163Value", true)] // Nl: letter number can start an identifier.
    [InlineData("A\u02B0", true)] // Lm: modifier letter can extend, but cannot start.
    [InlineData("\u02B0A", false)]
    [InlineData("A\u0301", true)] // Mn: nonspacing mark.
    [InlineData("A\u0903", true)] // Mc: spacing combining mark.
    [InlineData("A\u0661", true)] // Nd: decimal digit.
    [InlineData("A\u203F", false)] // Pc: connector other than underscore is not permitted.
    [InlineData("A\u200C", false)] // Cf: format characters are not WPF name characters.
    public void NameGrammarMatchesWpfRatherThanTheBroaderCSharpIdentifierRules(string name, bool valid)
    {
        // Primary grammar: https://source.dot.net/System.Xaml/System/Xaml/NameValidationHelper.cs.html
        string text = Window($"<TextBox x:Name='{name}'/>");
        var issues = Analyze(text).Where(issue => issue.Id == "XAMLNAME001").ToArray();
        if (valid) Assert.Empty(issues);
        else AssertSpan(text, Assert.Single(issues), name);
    }

    [Theory]
    [InlineData(513)]
    [InlineData(4096)]
    public void LongLegalNamesAreNotMisreportedAsInvalidIdentifiers(int length)
    {
        string name = "Element" + new string('x', length - "Element".Length);
        string text = Window($"<Grid><TextBox x:Name='{name}'/><TextBlock Tag='{{Binding ElementName={name}}}'/></Grid>");
        var analysis = Service.AnalyzeDetailed(PathName, text, Version, Fixture.Value);
        Assert.DoesNotContain(analysis.Diagnostics, issue => issue.Id is "XAMLNAME001" or "XAMLNAME004");
        if (!analysis.IsComplete) Assert.False(string.IsNullOrWhiteSpace(analysis.Status));
    }

    [Fact]
    public void CustomBindingLookalikesAreNotFrameworkElementNameReferences()
    {
        var (text, position) = Mark(Window("<Grid><Grid.Tag><c:Binding ElementName='Mis$$sing'/></Grid.Tag></Grid>"));
        Assert.Empty(Analyze(text));
        Assert.Null(Service.Complete(text, position, Version, Fixture.Value));
        Assert.Empty(Service.GetDefinition(PathName, text, position, Fixture.Value));
        Assert.Empty(Service.GetCodeActions(PathName, text, position, Version, Fixture.Value));
    }

    [Theory]
    [InlineData("<TextBlock Text=\"{Binding Text, ElementName= In$$putOld }\"/>", "InputOld")]
    [InlineData("<TextBlock Text=\"{Binding Text, ElementName='  In$$putOld  '}\"/>", "InputOld")]
    [InlineData("<TextBlock><TextBlock.Text><Binding ElementName='  In$$putOld  '/></TextBlock.Text></TextBlock>", "InputOld")]
    [InlineData("<TextBlock Text=\"{Binding ElementName=In&#112;$$ut}\"/>", "In&#112;ut")]
    public void CompletionReplacesTheWholeRawNameAndPreservesQuotesAndWhitespace(string consumer, string replaced)
    {
        var (text, position) = Mark(Window($"<Grid><TextBox x:Name='Input'/>{consumer}</Grid>"));
        var result = Service.Complete(text, position, Version, Fixture.Value);
        Assert.NotNull(result);
        Assert.Equal(Version, result.Version);
        Assert.Equal(replaced, Slice(text, result.Start, result.Length));
        var choice = Assert.Single(result.Items, item => item.InsertText == "Input");
        string updated = text.Remove(result.Start, result.Length).Insert(result.Start, choice.InsertText);
        Assert.Equal(text.Replace(replaced, "Input", StringComparison.Ordinal), updated);
        Assert.DoesNotContain(Analyze(updated), diagnostic => diagnostic.Id == "XAMLNAME004");
    }

    [Theory]
    [InlineData("<TextBlock Text=\"{Binding ElementName='  Input  '}\"/>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding ElementName='  Input  '/></TextBlock.Text></TextBlock>")]
    public void LookupTrimsElementNameWithoutIncludingWhitespaceInNavigation(string consumer)
    {
        string text = Window($"<Grid><TextBox x:Name='Input'/>{consumer}</Grid>");
        Assert.Empty(Analyze(text));
        int reference = text.LastIndexOf("Input", StringComparison.Ordinal);
        var definition = Assert.Single(Service.GetDefinition(PathName, text, reference + 1, Fixture.Value));
        Assert.Equal(text.IndexOf("Input", StringComparison.Ordinal), definition.Start);
        Assert.Equal(5, definition.Length);
    }

    [Theory]
    [InlineData("123Bad")]
    [InlineData("bad-name")]
    [InlineData("two names")]
    public void InvalidDeclarationsHaveExactValueDiagnostics(string name)
    {
        string text = Window($"<TextBox x:Name='{name}'/>");
        var issue = Assert.Single(Analyze(text), diagnostic => diagnostic.Id == "XAMLNAME001");
        Assert.Equal("Error", issue.Severity);
        AssertSpan(text, issue, name);
    }

    [Fact]
    public void DuplicateDeclarationsNeverSelectAnArbitraryDefinitionOrSuggestion()
    {
        var (text, position) = Mark(Window("<Grid><TextBox x:Name='Input'/><Button Name='Input'/><TextBlock Text='{Binding ElementName=In$$put}'/></Grid>"));
        var issues = Analyze(text).Where(diagnostic => diagnostic.Id == "XAMLNAME002").ToArray();
        Assert.NotEmpty(issues);
        Assert.All(issues, issue => Assert.Equal("Input", Slice(text, issue.Start, issue.Length)));
        Assert.Empty(Service.GetDefinition(PathName, text, position, Fixture.Value));
        Assert.DoesNotContain(Service.Complete(text, position, Version, Fixture.Value)?.Items ?? [], item => item.InsertText == "Input");
        Assert.Empty(Service.GetCodeActions(PathName, text, position, Version, Fixture.Value));
    }

    [Theory]
    [InlineData("Input")]
    [InlineData("Other")]
    public void SimultaneousNameAndXNameAreConflictingEvenWhenTheirValuesMatch(string alias)
    {
        string text = Window($"<Grid><TextBox x:Name='Input' Name='{alias}'/><TextBlock Text='{{Binding ElementName=Input}}'/></Grid>");
        Assert.Contains(Analyze(text), issue => issue.Id == "XAMLNAME003" && issue.Severity == "Error");
        Assert.Empty(Service.GetDefinition(PathName, text, text.LastIndexOf("Input", StringComparison.Ordinal), Fixture.Value));
    }

    [Fact]
    public void SiblingTemplatesAndTheRootKeepTheirSameNamedDeclarationsSeparate()
    {
        string text = Window("""
            <Window.Resources>
              <DataTemplate x:Key='First'><StackPanel><TextBox x:Name='Input'/><TextBlock Text='{Binding ElementName=Input}'/></StackPanel></DataTemplate>
              <ControlTemplate x:Key='Second' TargetType='Button'><StackPanel><TextBox Name='Input'/><TextBlock Text='{Binding ElementName=Input}'/></StackPanel></ControlTemplate>
            </Window.Resources>
            <StackPanel><TextBox x:Name='Input'/><TextBlock Text='{Binding ElementName=Input}'/></StackPanel>
            """);
        Assert.Empty(Analyze(text));
        var definitions = new List<int>();
        for (int start = 0; (start = text.IndexOf("ElementName=Input", start, StringComparison.Ordinal)) >= 0; start++)
        {
            var definition = Assert.Single(Service.GetDefinition(PathName, text, start + "ElementName=".Length, Fixture.Value));
            Assert.True(definition.Start < start);
            definitions.Add(definition.Start);
        }
        Assert.Equal(3, definitions.Distinct().Count());
    }

    [Theory]
    [InlineData("DataTemplate")]
    [InlineData("ControlTemplate")]
    [InlineData("ItemsPanelTemplate")]
    public void TemplateObjectNamesNeverCollideWithOrMasqueradeAsTheirContentNames(string template)
    {
        string text = Window($"<Grid><Grid.Tag><{template} x:Name='Shared'><StackPanel><TextBox x:Name='Shared'/><TextBlock Tag='{{Binding ElementName=Shared}}'/></StackPanel></{template}></Grid.Tag><TextBlock Tag='{{Binding ElementName=Shared}}'/></Grid>");
        Assert.Empty(Analyze(text));
        int inner = text.IndexOf("ElementName=Shared", StringComparison.Ordinal) + "ElementName=".Length;
        int outer = text.LastIndexOf("ElementName=Shared", StringComparison.Ordinal) + "ElementName=".Length;
        var innerDefinition = Assert.Single(Service.GetDefinition(PathName, text, inner, Fixture.Value));
        var outerDefinitions = Service.GetDefinition(PathName, text, outer, Fixture.Value);
        Assert.Equal(text.IndexOf("<TextBox x:Name='Shared'", StringComparison.Ordinal) + "<TextBox x:Name='".Length, innerDefinition.Start);
        // Withholding the template object's registration is conservative; resolving
        // its content declaration from the containing scope would be incorrect.
        Assert.True(outerDefinitions.Count <= 1);
        Assert.All(outerDefinitions, definition => Assert.Equal(text.IndexOf("Shared", StringComparison.Ordinal), definition.Start));
    }

    [Fact]
    public void ATemplateObjectsOwnNameIsNotADeclarationInItsContentScope()
    {
        string text = Window("<Grid><Grid.Tag><DataTemplate x:Name='TemplateObject'><TextBlock Tag='{Binding ElementName=TemplateObject}'/></DataTemplate></Grid.Tag></Grid>");
        int reference = text.LastIndexOf("TemplateObject", StringComparison.Ordinal);
        Assert.DoesNotContain(Analyze(text), issue => issue.Id == "XAMLNAME004");
        Assert.Empty(Service.GetDefinition(PathName, text, reference, Fixture.Value));
        Assert.DoesNotContain(Service.Complete(text, reference, Version, Fixture.Value)?.Items ?? [], item => item.InsertText == "TemplateObject");
    }

    [Theory]
    [InlineData("DataTemplate")]
    [InlineData("ControlTemplate")]
    public void UnresolvedTemplateFallbackDoesNotBecomeAMissingNameOrTypoFix(string template)
    {
        string text = Window($"<Window.Resources><{template} x:Key='Template'><TextBlock Text='{{Binding ElementName=Outre}}'/></{template}></Window.Resources><TextBox x:Name='Outer'/>");
        int position = text.IndexOf("Outre", StringComparison.Ordinal);
        Assert.DoesNotContain(Analyze(text), issue => issue.Id == "XAMLNAME004");
        Assert.Empty(Service.GetDefinition(PathName, text, position, Fixture.Value));
        Assert.Empty(Service.GetCodeActions(PathName, text, position, Version, Fixture.Value));
    }

    [Fact]
    public void CustomRuntimeNameAliasAndItsInheritanceAreNamesButAnUnrelatedNamePropertyIsNot()
    {
        var (text, position) = Mark(Window("<Grid><Grid.Tag><c:InheritedNamed Identifier='NamedObject'/></Grid.Tag><TextBlock Tag='{Binding ElementName=Named$$Object}'/></Grid>"));
        Assert.Empty(Analyze(text));
        var definition = Assert.Single(Service.GetDefinition(PathName, text, position, Fixture.Value));
        Assert.Equal(text.IndexOf("NamedObject", StringComparison.Ordinal), definition.Start);
        Assert.Contains(Service.Complete(text, position, Version, Fixture.Value)!.Items, item => item.InsertText == "NamedObject");

        string unrelated = Window("<Grid><Grid.Tag><c:PlainNamed Name='NotAName'/></Grid.Tag><TextBlock Tag='{Binding ElementName=NotAName}'/></Grid>");
        int reference = unrelated.LastIndexOf("NotAName", StringComparison.Ordinal);
        Assert.Empty(Service.GetDefinition(PathName, unrelated, reference, Fixture.Value));
        Assert.Contains(Analyze(unrelated), issue => issue.Id == "XAMLNAME004" && issue.Start == reference);
    }

    [Theory]
    [InlineData("<c:CustomScope><TextBlock Text='{Binding ElementName=Ou$$ter}'/></c:CustomScope>")]
    [InlineData("<Grid NameScope.NameScope='{x:Null}'><TextBlock Text='{Binding ElementName=Ou$$ter}'/></Grid>")]
    [InlineData("<Grid><NameScope.NameScope><NameScope/></NameScope.NameScope><TextBlock Text='{Binding ElementName=Ou$$ter}'/></Grid>")]
    public void UnknownNamescopeBoundariesDoNotBorrowAnOuterDeclaration(string body)
    {
        var (text, position) = Mark(Window($"<StackPanel><TextBox x:Name='Outer'/>{body}</StackPanel>"));
        Assert.DoesNotContain(Analyze(text), issue => issue.Id == "XAMLNAME004");
        Assert.Empty(Service.GetDefinition(PathName, text, position, Fixture.Value));
        Assert.DoesNotContain(Service.Complete(text, position, Version, Fixture.Value)?.Items ?? [], item => item.InsertText == "Outer");
        Assert.Empty(Service.GetCodeActions(PathName, text, position, Version, Fixture.Value));
    }

    [Fact]
    public void AnUnresolvedCustomElementSubtreeCannotBorrowThePageNamescope()
    {
        var (text, position) = Mark(Window("<StackPanel><TextBox x:Name='Outer'/><missing:Unknown xmlns:missing='clr-namespace:Unavailable'><TextBlock Tag='{Binding ElementName=Ou$$ter}'/></missing:Unknown></StackPanel>"));
        Assert.DoesNotContain(Analyze(text), issue => issue.Id == "XAMLNAME004");
        Assert.Empty(Service.GetDefinition(PathName, text, position, Fixture.Value));
        Assert.DoesNotContain(Service.Complete(text, position, Version, Fixture.Value)?.Items ?? [], item => item.InsertText == "Outer");
        Assert.Empty(Service.GetCodeActions(PathName, text, position, Version, Fixture.Value));
    }

    [Theory]
    [InlineData("CustomScopeWindow")]
    [InlineData("MetadataScopeWindow")]
    public void RootXClassNamescopeMetadataCannotBeHiddenByItsLexicalWindowType(string rootType)
    {
        var (text, position) = Mark($"<Window {Namespaces} x:Class='NameFixture.{rootType}'><Grid><TextBox x:Name='Outer'/><TextBlock Tag='{{Binding ElementName=Ou$$ter}}'/></Grid></Window>");
        Assert.DoesNotContain(Analyze(text), issue => issue.Id == "XAMLNAME004");
        Assert.Empty(Service.GetDefinition(PathName, text, position, Fixture.Value));
        Assert.DoesNotContain(Service.Complete(text, position, Version, Fixture.Value)?.Items ?? [], item => item.InsertText == "Outer");
    }

    [Fact]
    public void RootXClassSuppliesItsRuntimeNameAlias()
    {
        string text = $"<Window {Namespaces} x:Class='NameFixture.RuntimeNamedWindow' Identifier='RootView'><TextBlock Tag='{{Binding ElementName=RootView}}'/></Window>";
        Assert.Empty(Analyze(text));
        var definition = Assert.Single(Service.GetDefinition(PathName, text, text.LastIndexOf("RootView", StringComparison.Ordinal), Fixture.Value));
        Assert.Equal(text.IndexOf("RootView", StringComparison.Ordinal), definition.Start);
        Assert.Equal("RootView", Slice(text, definition.Start, definition.Length));
    }

    [Fact]
    public void ACanonicalLookingRuntimeNameAttributeFromProjectCodeDoesNotCreateNames()
    {
        var compilation = WithMetadataLookalikes();
        string text = Window("<Grid><Grid.Tag><c:FakeRuntimeNamed Alias='NotRegistered'/></Grid.Tag><TextBlock Tag='{Binding ElementName=NotRegistered}'/></Grid>");
        int reference = text.LastIndexOf("NotRegistered", StringComparison.Ordinal);
        Assert.Empty(Service.GetDefinition(PathName, text, reference, compilation));
        Assert.DoesNotContain(Service.Complete(text, reference, Version, compilation)?.Items ?? [], item => item.InsertText == "NotRegistered");
    }

    [Theory]
    [InlineData("FakeInterfaceScope")]
    [InlineData("FakeAttributeScope")]
    public void CanonicalLookingProjectMetadataDoesNotInventAFrameworkNamescopeBoundary(string type)
    {
        var compilation = WithMetadataLookalikes();
        string text = Window($"<StackPanel><TextBox x:Name='Outer'/><c:{type}><TextBlock Tag='{{Binding ElementName=Outer}}'/></c:{type}></StackPanel>");
        var definition = Assert.Single(Service.GetDefinition(PathName, text, text.LastIndexOf("Outer", StringComparison.Ordinal), compilation));
        Assert.Equal(text.IndexOf("Outer", StringComparison.Ordinal), definition.Start);
        Assert.DoesNotContain(Analyze(text, compilation), issue => issue.Id == "XAMLNAME004");
    }

    [Fact]
    public void ResourceEntriesAndIgnoredOrDesignNamesDoNotLeakIntoTheOrdinaryScope()
    {
        var (text, position) = Mark(Window("""
            <Window.Resources><TextBox x:Key='Resource' x:Name='ResourceName'/></Window.Resources>
            <StackPanel><TextBox x:Name='Visible'/><TextBox d:Name='DesignName'/>
              <d:Ignored><TextBox x:Name='IgnoredName'/></d:Ignored>
              <TextBlock Text='{Binding ElementName=$$}'/>
            </StackPanel>
            """));
        var completion = Service.Complete(text, position, Version, Fixture.Value);
        Assert.NotNull(completion);
        Assert.Contains(completion.Items, item => item.InsertText == "Visible");
        Assert.DoesNotContain(completion.Items, item => item.InsertText is "ResourceName" or "DesignName" or "IgnoredName");
    }

    [Fact]
    public void TypoFixUsesTheRawEntitySpanAndGuardsTheEntireDocumentSnapshot()
    {
        string text = Window("<Grid><TextBox x:Name='Input'/><TextBlock Text=\"{Binding ElementName='  In&#112;tu  '}\"/></Grid>");
        int position = text.IndexOf("In&#112;tu", StringComparison.Ordinal) + 3;
        var issue = Assert.Single(Analyze(text), diagnostic => diagnostic.Id == "XAMLNAME004");
        Assert.Equal("Warning", issue.Severity);
        AssertSpan(text, issue, "In&#112;tu");
        var action = Assert.Single(Service.GetCodeActions(PathName, text, position, Version, Fixture.Value));
        Assert.Equal(PathName, action.Edit.Path); Assert.Equal(Version, action.Edit.Version);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), action.Edit.ExpectedTextHash);
        var edit = Assert.Single(action.Edit.Edits);
        Assert.Equal("In&#112;tu", Slice(text, edit.Start, edit.Length));
        Assert.Equal("Input", edit.NewText);
        string updated = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        Assert.Contains("ElementName='  Input  '", updated);
        Assert.Empty(Analyze(updated));
    }

    [Fact]
    public void EquallyPlausibleNamesDoNotProduceAnArbitrarySpellingFix()
    {
        string text = Window("<Grid><TextBox x:Name='Input'/><TextBox x:Name='Inpot'/><TextBlock Tag='{Binding ElementName=Inpt}'/></Grid>");
        int position = text.IndexOf("Inpt", StringComparison.Ordinal);
        Assert.Contains(Analyze(text), diagnostic => diagnostic.Id == "XAMLNAME004");
        Assert.Empty(Service.GetCodeActions(PathName, text, position, Version, Fixture.Value));
    }

    [Fact]
    public void HoverAndDefinitionIdentifyTheNameDeclarationRatherThanThePathMember()
    {
        string text = Window("<Grid>\n<TextBox x:Name='In&#112;ut'/>\n<TextBlock Text='{Binding Text, ElementName=Input}'/></Grid>");
        int position = text.LastIndexOf("Input", StringComparison.Ordinal) + 2;
        var hover = Service.GetHover(PathName, text, position, Fixture.Value);
        Assert.NotNull(hover);
        Assert.Equal("Input", Slice(text, hover.Start, hover.Length));
        Assert.Contains("Input", hover.Text);
        Assert.Contains("TextBox", hover.Text);
        var definition = Assert.Single(Service.GetDefinition(PathName, text, position, Fixture.Value));
        Assert.Equal("In&#112;ut", Slice(text, definition.Start, definition.Length));
        Assert.Equal(text.AsSpan(0, definition.Start).Count('\n') + 1, definition.Line);
        Assert.Equal(definition.Start - text.LastIndexOf('\n', definition.Start), definition.Column);
        Assert.Null(Service.Complete(text, text.IndexOf("Binding Text", StringComparison.Ordinal) + 10, Version, Fixture.Value));
    }

    [Fact]
    public void MissingFrameworkMetadataDoesNotInventScopeDiagnosticsOrFrameworkCandidates()
    {
        var compilation = CSharpCompilation.Create("Plain", references: PlatformReferences(), options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var (text, position) = Mark(Window("<Grid><TextBox x:Name='Input'/><TextBlock Text='{Binding ElementName=In$$pt}'/></Grid>"));
        Assert.DoesNotContain(Analyze(text, compilation), issue => issue.Id == "XAMLNAME004");
        Assert.Empty(Service.GetDefinition(PathName, text, position, compilation));
        Assert.Empty(Service.GetCodeActions(PathName, text, position, Version, compilation));
    }

    [Fact]
    public void CanceledRequestsNeverReturnPartialEditorData()
    {
        string text = Window("<TextBlock Text='{Binding ElementName=Missing}'/>");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Service.Analyze(PathName, text, Version, Fixture.Value, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => Service.Complete(text, 0, Version, Fixture.Value, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => Service.GetDefinition(PathName, text, 0, Fixture.Value, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => Service.GetHover(PathName, text, 0, Fixture.Value, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => Service.GetCodeActions(PathName, text, 0, Version, Fixture.Value, cancellation.Token));
    }

    [Theory]
    [InlineData("characters")]
    [InlineData("depth")]
    [InlineData("declarations")]
    public void BudgetExhaustionDoesNotReturnUnsafePartialNameAssistance(string budget)
    {
        string padding = budget switch
        {
            "characters" => "<!--" + new string('x', 1_000_000) + "-->",
            "depth" => string.Concat(Enumerable.Repeat("<Grid>", 300)) + string.Concat(Enumerable.Repeat("</Grid>", 300)),
            _ => string.Concat(Enumerable.Range(0, 8200).Select(index => $"<TextBox x:Name='Item{index}'/>"))
        };
        string text = Window("<Grid><TextBox x:Name='Input'/><TextBlock Text='{Binding ElementName=Inpt}'/>" + padding + "</Grid>");
        int position = text.IndexOf("Inpt", StringComparison.Ordinal);
        var analysis = Service.AnalyzeDetailed(PathName, text, Version, Fixture.Value);
        Assert.False(analysis.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(analysis.Status));
        Assert.DoesNotContain(analysis.Diagnostics, issue => issue.Id == "XAMLNAME004");
        Assert.Empty(Service.Complete(text, position, Version, Fixture.Value)?.Items ?? []);
        Assert.Empty(Service.GetCodeActions(PathName, text, position, Version, Fixture.Value));
    }

    private static string Window(string body) => $"<Window {Namespaces}>\n{body}\n</Window>";
    private static string Slice(string text, int start, int length) => text.Substring(start, length);
    private static IReadOnlyList<WorkspaceDiagnostic> Analyze(string text, Compilation? compilation = null) => Service.Analyze(PathName, text, Version, compilation ?? Fixture.Value);
    private static (string Text, int Position) Mark(string text)
    {
        int position = text.IndexOf("$$", StringComparison.Ordinal); Assert.True(position >= 0);
        return (text.Remove(position, 2), position);
    }
    private static void AssertSpan(string text, WorkspaceDiagnostic issue, string expected)
    {
        Assert.Equal(expected, Slice(text, issue.Start, issue.Length));
        Assert.Equal(PathName, issue.Path);
        Assert.Equal(text.AsSpan(0, issue.Start).Count('\n') + 1, issue.Line);
        Assert.Equal(issue.Start - text.LastIndexOf('\n', issue.Start), issue.Column);
    }
    private static IEnumerable<MetadataReference> PlatformReferences() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
    private static CSharpCompilation CreateCompilation()
    {
        // Metadata-only fixture: these controls and application accessors are never executed.
        var dotnetRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.WindowsDesktop.App.Ref");
        var pack = Directory.GetDirectories(packRoot).OrderByDescending(path => System.Version.TryParse(Path.GetFileName(path), out var version) ? version : new System.Version()).First();
        var referenceDirectory = Directory.GetDirectories(Path.Combine(pack, "ref")).OrderByDescending(path => path, StringComparer.Ordinal).First();
        var wpfPaths = Directory.GetFiles(referenceDirectory, "*.dll");
        var names = wpfPaths.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = PlatformReferences().Where(reference => !names.Contains(Path.GetFileName(reference.Display)))
            .Concat(wpfPaths.Select(path => MetadataReference.CreateFromFile(path)));
        var compilation = CSharpCompilation.Create("NameFixture", [CSharpSyntaxTree.ParseText("""
            namespace NameFixture;
            [System.Windows.Markup.RuntimeNameProperty("Identifier")]
            public class RuntimeNamed : System.Windows.DependencyObject { public string Identifier { get; set; } = ""; }
            public class InheritedNamed : RuntimeNamed { }
            public class PlainNamed : System.Windows.DependencyObject { public string Name { get; set; } = ""; }
            public class Binding : System.Windows.DependencyObject { public string ElementName { get; set; } = ""; }
            [System.Windows.Markup.RuntimeNameProperty("Identifier")]
            public class RuntimeNamedWindowBase : System.Windows.Window { public string Identifier { get; set; } = ""; }
            public partial class RuntimeNamedWindow : RuntimeNamedWindowBase { }
            [System.Windows.Markup.NameScopeProperty("Scope")]
            public partial class MetadataScopeWindow : System.Windows.Window { public System.Windows.Markup.INameScope Scope { get; set; } = null!; }
            public partial class CustomScopeWindow : System.Windows.Window, System.Windows.Markup.INameScope
            {
                public object FindName(string name) => null!;
                public void RegisterName(string name, object value) { }
                public void UnregisterName(string name) { }
            }
            public class CustomScope : System.Windows.Controls.Grid, System.Windows.Markup.INameScope
            {
                public object FindName(string name) => null!;
                public void RegisterName(string name, object value) { }
                public void UnregisterName(string name) { }
            }
            """, path: "NameFixture.cs")], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }

    private static CSharpCompilation WithMetadataLookalikes()
    {
        var compilation = Fixture.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
            namespace System.Windows.Markup
            {
                public sealed class RuntimeNamePropertyAttribute : System.Attribute { public RuntimeNamePropertyAttribute(string name) { } }
                public sealed class NameScopePropertyAttribute : System.Attribute { public NameScopePropertyAttribute(string name) { } }
                public interface INameScope { }
            }
            namespace NameFixture
            {
                [System.Windows.Markup.RuntimeNameProperty("Alias")]
                public class FakeRuntimeNamed : System.Windows.DependencyObject { public string Alias { get; set; } = ""; }
                public class FakeInterfaceScope : System.Windows.Controls.Grid, System.Windows.Markup.INameScope { }
                [System.Windows.Markup.NameScopeProperty("Scope")]
                public class FakeAttributeScope : System.Windows.Controls.Grid { public object Scope { get; set; } = null!; }
            }
            """, path: "MetadataLookalikes.cs"));
        // CS0436 warnings are intentional: source declarations shadow the framework
        // spelling, while the actual metadata identities remain different.
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
