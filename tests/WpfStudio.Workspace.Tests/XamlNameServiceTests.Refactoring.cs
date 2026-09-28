using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed partial class XamlNameServiceTests
{
    [Fact]
    public void NameOccurrencesKeepExactEntitiesAndPointToOneAuthoredDeclaration()
    {
        string text = Window("<Grid><TextBox x:Name='In&#112;ut'/><TextBlock Text=\"{Binding ElementName='  I&#110;put  ', Path=Text}\"/><TextBlock><TextBlock.Text><Binding ElementName='Input'/></TextBlock.Text></TextBlock></Grid>");
        var result = Service.GetNameOccurrences(text, Fixture.Value);
        Assert.True(result.IsComplete, string.Join(" ", result.Warnings));
        Assert.False(result.CoverageLimited);
        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("Input", declaration.Name);
        Assert.Equal("In&#112;ut", Slice(text, declaration.Start, declaration.Length));
        Assert.Equal("x:Name", declaration.AttributeName);
        Assert.True(declaration.IsRootScope);
        Assert.Equal("page", declaration.ScopeKind);
        Assert.Null(declaration.RootClass);
        Assert.Equal("System.Windows.Controls.TextBox", declaration.ElementType.ToDisplayString());
        Assert.Equal(3, result.Occurrences.Count);
        Assert.All(result.Occurrences, occurrence => Assert.Same(declaration, occurrence.Declaration));
        var entity = Assert.Single(result.Occurrences, occurrence => Slice(text, occurrence.Start, occurrence.Length) == "I&#110;put");
        Assert.Equal("ElementName", entity.Kind);
        Assert.Same(entity, result.GetTarget(entity.Start + 3));
        Assert.Null(result.GetTarget(text.IndexOf("Path=Text", StringComparison.Ordinal) + 6));
    }

    [Fact]
    public void NameOccurrencesSeparateIdenticalPageAndTemplateNames()
    {
        string text = Window("""
            <Window.Resources>
              <DataTemplate x:Key='First'><StackPanel><TextBox x:Name='Input'/><TextBlock Text='{Binding ElementName=Input}'/></StackPanel></DataTemplate>
              <ControlTemplate x:Key='Second' TargetType='Button'><StackPanel><TextBox Name='Input'/><TextBlock Text='{Binding ElementName=Input}'/></StackPanel></ControlTemplate>
            </Window.Resources>
            <Grid><TextBox Name='Input'/><TextBlock Text='{Binding ElementName=Input}'/></Grid>
            """);
        var result = Service.GetNameOccurrences(text, Fixture.Value);
        Assert.True(result.IsComplete, string.Join(" ", result.Warnings));
        Assert.Equal(3, result.Declarations.Count);
        Assert.Equal(3, result.Declarations.Select(declaration => declaration.ScopeStart).Distinct().Count());
        Assert.Single(result.Declarations, declaration => declaration.IsRootScope);
        Assert.Equal(2, result.Declarations.Count(declaration => declaration.ScopeKind == "template" && !declaration.IsRootScope));
        foreach (var declaration in result.Declarations)
        {
            var pair = result.Occurrences.Where(occurrence => occurrence.Declaration == declaration).ToArray();
            Assert.Equal(2, pair.Length);
            Assert.Equal("Declaration", pair[0].Kind);
            Assert.Equal("ElementName", pair[1].Kind);
        }
    }

    [Fact]
    public void NameOccurrenceRootMetadataUsesTheValidatedActualClassAndRuntimeAlias()
    {
        string text = $"<Window {Namespaces} x:Class='NameFixture.RuntimeNamedWindow' Identifier='View'><TextBlock Tag='{{Binding ElementName=View}}'/></Window>";
        var result = Service.GetNameOccurrences(text, Fixture.Value);
        Assert.True(result.IsComplete, string.Join(" ", result.Warnings));
        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("Identifier", declaration.AttributeName);
        Assert.Equal("NameFixture.RuntimeNamedWindow", declaration.RootClass);
        Assert.Equal(declaration.RootClass, declaration.RootType?.ToDisplayString());
        Assert.Equal(declaration.RootClass, declaration.ElementType.ToDisplayString());
        Assert.True(declaration.IsRootScope);
    }

    [Fact]
    public void RenamingEntityTokensPreservesUntouchedNamesAndTheirReferences()
    {
        string text = Window("<Grid><TextBox x:Name='In&#112;ut'/><TextBlock Text=\"{Binding ElementName='  I&#110;put  '}\"/><Button Name='Other'/><TextBlock Tag='{Binding ElementName=Other}'/></Grid>");
        var before = Service.GetNameOccurrences(text, Fixture.Value);
        var selected = Assert.Single(before.Declarations, declaration => declaration.Name == "Input");
        var edits = NameEdits(before, selected, "CustomerInput");
        string changed = ApplyNameEdits(text, edits);
        var after = Service.GetNameOccurrences(changed, Fixture.Value);
        var validation = Service.ValidateRename(before, after, selected, "CustomerInput", edits);
        Assert.True(validation.Success, validation.Error);
        Assert.Contains("ElementName='  CustomerInput  '", changed);
        Assert.Contains("Name='Other'", changed);
        Assert.Equal(2, after.Occurrences.Count(occurrence => occurrence.Declaration.Name == "Other"));
    }

    [Fact]
    public void TemplateRenameDoesNotTouchTheSameSpelledPageName()
    {
        string text = Window("<Window.Resources><DataTemplate x:Key='Item'><Grid><TextBox x:Name='Input'/><TextBlock Tag='{Binding ElementName=Input}'/></Grid></DataTemplate></Window.Resources><Grid><TextBox x:Name='Input'/><TextBlock Tag='{Binding ElementName=Input}'/></Grid>");
        var before = Service.GetNameOccurrences(text, Fixture.Value);
        var selected = Assert.Single(before.Declarations, declaration => declaration.ScopeKind == "template");
        var edits = NameEdits(before, selected, "TemplateInput");
        string changed = ApplyNameEdits(text, edits);
        var after = Service.GetNameOccurrences(changed, Fixture.Value);
        var validation = Service.ValidateRename(before, after, selected, "TemplateInput", edits);
        Assert.True(validation.Success, validation.Error);
        Assert.Equal(2, edits.Count);
        Assert.Contains("<Grid><TextBox x:Name='Input'/><TextBlock Tag='{Binding ElementName=Input}'/></Grid></Window>", changed.Replace("\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public void NameRenameRefusesCollisionsAndOmittedReferences()
    {
        string text = Window("<Grid><TextBox x:Name='Input'/><Button x:Name='Other'/><TextBlock Tag='{Binding ElementName=Input}'/></Grid>");
        var before = Service.GetNameOccurrences(text, Fixture.Value);
        var selected = Assert.Single(before.Declarations, declaration => declaration.Name == "Input");
        var collisionEdits = NameEdits(before, selected, "Other");
        var collision = Service.GetNameOccurrences(ApplyNameEdits(text, collisionEdits), Fixture.Value);
        Assert.False(Service.ValidateRename(before, collision, selected, "Other", collisionEdits).Success);
        var edits = NameEdits(before, selected, "Renamed");
        var after = Service.GetNameOccurrences(ApplyNameEdits(text, edits), Fixture.Value);
        Assert.False(Service.ValidateRename(before, after, selected, "Renamed", edits.Take(1).ToArray()).Success);
    }

    [Fact]
    public void NameRenameValidatesUntouchedReferenceIdentities()
    {
        string text = Window("<Grid><TextBox x:Name='Input'/><TextBox x:Name='First'/><TextBox x:Name='Other'/><TextBlock Tag='{Binding ElementName=Input}'/><TextBlock Tag='{Binding ElementName=First}'/></Grid>");
        var before = Service.GetNameOccurrences(text, Fixture.Value);
        var selected = Assert.Single(before.Declarations, declaration => declaration.Name == "Input");
        var edits = NameEdits(before, selected, "LongerInput");
        string changed = ApplyNameEdits(text, edits).Replace("ElementName=First", "ElementName=Other", StringComparison.Ordinal);
        var after = Service.GetNameOccurrences(changed, Fixture.Value);
        Assert.True(after.IsComplete, string.Join(" ", after.Warnings));
        var validation = Service.ValidateRename(before, after, selected, "LongerInput", edits);
        Assert.False(validation.Success);
        Assert.Contains("untouched", validation.Error);
    }

    [Theory]
    [InlineData("<TextBlock Tag='{x:Reference Input}'/>")]
    [InlineData("<TextBlock Tag='{Binding Source={x:Reference Input}}'/>")]
    [InlineData("<TextBlock><TextBlock.Tag><x:Reference Name='Input'/></TextBlock.Tag></TextBlock>")]
    [InlineData("<Setter TargetName='Input' Property='Width' Value='30'/>")]
    [InlineData("<Setter Setter.TargetName='Input' Property='Width' Value='30'/>")]
    [InlineData("<Setter Property='Width' Value='30'><Setter.TargetName>Input</Setter.TargetName></Setter>")]
    [InlineData("<Trigger SourceName='Input' Property='IsEnabled' Value='True'/>")]
    [InlineData("<Trigger Trigger.SourceName='Input' Property='IsEnabled' Value='True'/>")]
    [InlineData("<Trigger Property='IsEnabled' Value='True'><Trigger.SourceName>Input</Trigger.SourceName></Trigger>")]
    [InlineData("<Condition SourceName='Input' Property='IsEnabled' Value='True'/>")]
    [InlineData("<EventTrigger SourceName='Input' RoutedEvent='Button.Click'/>")]
    [InlineData("<Storyboard Storyboard.TargetName='Input'/>")]
    [InlineData("<TextBox><TextBox.Name>Another</TextBox.Name></TextBox>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding><Binding.ElementName>Input</Binding.ElementName></Binding></TextBlock.Text></TextBlock>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding Binding.ElementName='Input'/></TextBlock.Text></TextBlock>")]
    [InlineData("<TextBlock Text='{Binding Binding.ElementName=Input, Path=Text}'/>")]
    [InlineData("<TextBox FrameworkElement.Name='Input'/>")]
    [InlineData("<TextBlock Tag='{Binding ElementName=Input, Source={x:Null}}'/>")]
    [InlineData("<TextBlock Tag='{Binding ConverterParameter={Binding ElementName=Input}}'/>")]
    [InlineData("<c:CustomScope><TextBlock Tag='{Binding ElementName=Input}'/></c:CustomScope>")]
    [InlineData("<x:Code><![CDATA[void Use() { Input.Focus(); }]]></x:Code>")]
    public void UnsupportedNameConsumersKeepRefactoringCoverageExplicit(string content)
    {
        string text = Window("<Grid><TextBox x:Name='Input'/><TextBlock Tag='{Binding ElementName=Input}'/>" + content + "</Grid>");
        var result = Service.GetNameOccurrences(text, Fixture.Value);
        Assert.False(result.IsComplete);
        Assert.False(result.CoverageLimited);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void UnrelatedCustomTargetNameStringsAreNotInventedNameReferences()
    {
        var compilation = Fixture.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace NameFixture; public class OrdinaryTarget : System.Windows.DependencyObject { public string TargetName { get; set; } = \"\"; }"));
        string text = Window("<Grid><TextBox x:Name='Input'/><c:OrdinaryTarget TargetName='Input'/><TextBlock Tag='{Binding ElementName=Input}'/></Grid>");
        var result = Service.GetNameOccurrences(text, compilation);
        Assert.True(result.IsComplete, string.Join(" ", result.Warnings));
        Assert.Equal(2, result.Occurrences.Count);
    }

    [Fact]
    public void QualifiedRuntimeNameDeclarationsWithholdFalseMissingNameCertainty()
    {
        string text = Window("<Grid><TextBox FrameworkElement.Name='Input'/><TextBlock Tag='{Binding ElementName=Input}'/></Grid>");
        Assert.DoesNotContain(Analyze(text), diagnostic => diagnostic.Id == "XAMLNAME004");
        var result = Service.GetNameOccurrences(text, Fixture.Value);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Warnings, warning => warning.Contains("Qualified runtime-name", StringComparison.Ordinal));
    }

    [Fact]
    public void UnresolvedReferencesRemainSeparateFromProvenMatches()
    {
        string text = Window("<Grid><TextBox x:Name='Input'/><TextBlock Tag='{Binding ElementName=Input}'/><TextBlock Tag='{Binding ElementName=Missing}'/></Grid>");
        var result = Service.GetNameOccurrences(text, Fixture.Value);
        Assert.False(result.IsComplete);
        Assert.False(result.CoverageLimited);
        Assert.Equal(2, result.Occurrences.Count);
        Assert.Null(result.GetTarget(text.IndexOf("ElementName=Missing", StringComparison.Ordinal) + 14));
    }

    [Fact]
    public void IgnoredAndRawDataNamesAreExcludedWithoutInventingCoverageGaps()
    {
        string text = Window("<Grid><TextBox x:Name='Input'/><TextBlock Tag='{Binding ElementName=Input}'/><d:Grid x:Name='Input' TargetName='Input'/><x:XData><TextBlock Name='Input' Tag='{Binding ElementName=DataOnly}'/><x:Code>raw data</x:Code></x:XData></Grid>");
        var result = Service.GetNameOccurrences(text, Fixture.Value);
        Assert.True(result.IsComplete, string.Join(" ", result.Warnings));
        Assert.Equal(2, result.Occurrences.Count);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("characters")]
    [InlineData("extensions")]
    public void NameRefactoringReportsHardCoverageLimits(string kind)
    {
        string text = kind switch
        {
            "malformed" => Window("<TextBox x:Name='Input'>"),
            "characters" => Window("<!--" + new string('x', 1_000_000) + "-->"),
            _ => Window("<TextBlock Tag='" + string.Concat(Enumerable.Repeat("{Binding ConverterParameter=", 66)) + "{x:Null}" + new string('}', 66) + "'/>")
        };
        var result = Service.GetNameOccurrences(text, Fixture.Value);
        Assert.False(result.IsComplete);
        Assert.True(result.CoverageLimited);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void NameOccurrenceAndRenameValidationHonorCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Service.GetNameOccurrences(Window("<TextBox x:Name='Input'/>"), Fixture.Value, cancellation.Token));
        var before = Service.GetNameOccurrences(Window("<TextBox x:Name='Input'/>"), Fixture.Value);
        Assert.ThrowsAny<OperationCanceledException>(() => Service.ValidateRename(before, before, Assert.Single(before.Declarations), "Other", [], cancellation.Token));
    }

    private static IReadOnlyList<TextEdit> NameEdits(XamlNameOccurrenceResult result, XamlNameDeclaration declaration, string name)
        => result.Occurrences.Where(occurrence => occurrence.Declaration == declaration).Select(occurrence => new TextEdit(occurrence.Start, occurrence.Length, name)).ToArray();
    private static string ApplyNameEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        return text;
    }
}
