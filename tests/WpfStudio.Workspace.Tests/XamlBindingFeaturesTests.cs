using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlBindingFeaturesTests
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:vm=\"clr-namespace:Demo\"";
    private const string Context = "d:DataContext=\"{d:DesignInstance Type=vm:AppViewModel}\"";
    private static readonly Lazy<CSharpCompilation> Fixture = new(CreateCompilation);
    private static readonly XamlLanguageService Service = new();
    private static string Window(string body) => $"<Window {Ns} {Context}>\n{body}\n</Window>";
    private static IReadOnlyList<WorkspaceDiagnostic> Analyze(string text) => Service.Analyze("View.xaml", text, 7, Fixture.Value);

    [Fact]
    public void NamedSourcesUseTheirElementTypeIncludingForwardDeclarations()
    {
        var text = Window("""
            <Grid>
                <TextBlock Text="{Binding Text, ElementName=Input}" />
                <TextBlock Text="{Binding Tetx, ElementName=Input}" />
                <TextBox x:Name="Input" />
            </Grid>
            """);
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Tetx", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Text", issue.Message);
    }

    [Fact]
    public void ElementNameDoesNotEscapeItsTemplateAndSiblingTemplatesHaveSeparateScopes()
    {
        var text = Window("""
            <Window.Resources>
                <DataTemplate x:Key="First">
                    <TextBlock Text="{Binding RuntimeOnly, ElementName=Outer}" />
                </DataTemplate>
                <DataTemplate x:Key="Second">
                    <Grid><TextBox x:Name="Input" /><TextBlock Text="{Binding Tetx, ElementName=Input}" /></Grid>
                </DataTemplate>
                <DataTemplate x:Key="Third">
                    <Grid><TextBox x:Name="Input" /><TextBlock Text="{Binding Text, ElementName=Input}" /></Grid>
                </DataTemplate>
            </Window.Resources>
            <TextBox x:Name="Outer" />
            """);
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Tetx", text.Substring(issue.Start, issue.Length));
    }

    [Fact]
    public void DuplicateNamesDoNotChooseAnArbitrarySource()
    {
        Assert.Empty(Analyze(Window("<Grid><TextBox x:Name=\"Input\"/><Grid x:Name=\"Input\"/><TextBlock Text=\"{Binding RuntimeOnly, ElementName=Input}\"/></Grid>")));
    }

    [Fact]
    public void NamedDataContextPathsUseTheNamedElementsDeclaredContext()
    {
        var text = $"<Window {Ns} {Context} x:Class=\"Demo.RootWindow\" x:Name=\"Root\"><TextBlock Text=\"{{Binding DataContext.Selected.Nmae, ElementName=Root}}\" /></Window>";
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Customer", issue.Message);
    }

    [Fact]
    public void SelfAndAncestorLevelUseControlSymbolsAndTheSelectedAncestorsContext()
    {
        var text = Window("""
            <Grid d:DataContext="{d:DesignInstance vm:Customer}">
                <Grid d:DataContext="{d:DesignInstance vm:AppViewModel}">
                    <TextBlock Text="{Binding ActualWidth, RelativeSource={RelativeSource Self}}" />
                    <TextBlock Text="{Binding DataContext.Title, RelativeSource={RelativeSource AncestorType={x:Type Grid}}}" />
                    <TextBlock Text="{Binding DataContext.Nmae, RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type Grid}, AncestorLevel=2}}" />
                </Grid>
            </Grid>
            """);
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Customer", issue.Message);
    }

    [Fact]
    public void TemplatedParentUsesControlTemplateTargetTypeWithoutInventingItsDataContext()
    {
        var text = Window("""
            <Window.Resources>
                <ControlTemplate x:Key="Editor" TargetType="TextBox">
                    <Grid>
                        <TextBlock Text="{Binding Text, RelativeSource={RelativeSource TemplatedParent}}" />
                        <TextBlock Text="{Binding Tetx, RelativeSource={RelativeSource TemplatedParent}}" />
                        <TextBlock Text="{Binding DataContext.RuntimeOnly, RelativeSource={RelativeSource TemplatedParent}}" />
                    </Grid>
                </ControlTemplate>
            </Window.Resources>
            """);
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Tetx", text.Substring(issue.Start, issue.Length));
        Assert.Contains("TextBox", issue.Message);
    }

    [Theory]
    [InlineData("<Setter Property=\"Width\" Value=\"{Binding ActualWidth, RelativeSource={RelativeSource Self}}\" />")]
    [InlineData("<Setter Property=\"Width\"><Setter.Value><Binding Path=\"ActualWidth\" RelativeSource=\"{RelativeSource Self}\" /></Setter.Value></Setter>")]
    public void SetterSelfUsesTheStyledControlInsteadOfTheSetterObject(string setter)
    {
        var text = Window($"<Window.Resources><Style TargetType=\"{{x:Type Button}}\">{setter}</Style></Window.Resources>");
        Assert.Empty(Analyze(text));
        var invalid = text.Replace("ActualWidth", "ActualWidht", StringComparison.Ordinal);
        var issue = Assert.Single(Analyze(invalid));
        Assert.Contains("Button", issue.Message);
        Assert.Contains("ActualWidth", issue.Message);
        Assert.Equal("ActualWidht", invalid.Substring(issue.Start, issue.Length));
    }

    [Theory]
    [InlineData("<Style><Setter Property=\"Width\" Value=\"{Binding RuntimeOnly, RelativeSource={RelativeSource Self}}\" /></Style>")]
    [InlineData("<Style TargetType=\"Button\"><Setter TargetName=\"RuntimeChild\" Property=\"Width\" Value=\"{Binding RuntimeOnly, RelativeSource={RelativeSource Self}}\" /></Style>")]
    public void SetterSelfWithoutAKnownRuntimeTargetStaysUnknown(string style)
    {
        Assert.Empty(Analyze(Window($"<Window.Resources>{style}</Window.Resources>")));
    }

    [Fact]
    public void SetterTemplatedParentDoesNotUseTheTemplatesLexicalContainment()
    {
        var text = Window("""
            <Window.Resources><ControlTemplate TargetType="Grid">
                <ControlTemplate.Resources><Style TargetType="Button">
                    <Setter Property="Width" Value="{Binding RuntimeOnly, RelativeSource={RelativeSource TemplatedParent}}" />
                </Style></ControlTemplate.Resources>
            </ControlTemplate></Window.Resources>
            """);
        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void SetterAncestorsDoNotUseTheResourceDeclarationsLexicalParent()
    {
        var text = Window("""
            <Grid d:DataContext="{d:DesignInstance vm:Customer}">
                <Grid.Resources><Setter x:Key="SharedSetter" Property="Width"
                    Value="{Binding DataContext.Nmae, RelativeSource={RelativeSource AncestorType={x:Type Grid}}}" />
                </Grid.Resources>
            </Grid>
            """);
        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void LocalResourceSourcesRespectNearestDictionaryShadowing()
    {
        var text = Window("""
            <Window.Resources><vm:Customer x:Key="Source" /></Window.Resources>
            <Grid>
                <TextBlock Text="{Binding Name, Source={StaticResource Source}}" />
                <Grid>
                    <Grid.Resources><vm:AppViewModel x:Key="Source" /></Grid.Resources>
                    <TextBlock Text="{Binding Titl, Source={StaticResource Source}}" />
                </Grid>
            </Grid>
            """);
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Titl", text.Substring(issue.Start, issue.Length));
        Assert.Contains("AppViewModel", issue.Message);
    }

    [Fact]
    public void AnUnresolvedLaterMergedDictionaryDoesNotSelectAnEarlierResource()
    {
        var text = Window("""
            <Window.Resources>
                <ResourceDictionary><ResourceDictionary.MergedDictionaries>
                    <ResourceDictionary><vm:Customer x:Key="Source" /></ResourceDictionary>
                    <ResourceDictionary Source="External.xaml" />
                </ResourceDictionary.MergedDictionaries></ResourceDictionary>
            </Window.Resources>
            <TextBlock Text="{Binding RuntimeOnly, Source={StaticResource Source}}" />
            """);
        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void ObjectBindingSourcesAndRelativeSourcePropertyElementsAreResolved()
    {
        var text = Window("""
            <Grid>
                <TextBlock><TextBlock.Text><Binding Path="Nmae"><Binding.Source><vm:Customer /></Binding.Source></Binding></TextBlock.Text></TextBlock>
                <TextBlock><TextBlock.Text><Binding Path="ActualWidth"><Binding.RelativeSource><RelativeSource Mode="Self" /></Binding.RelativeSource></Binding></TextBlock.Text></TextBlock>
            </Grid>
            """);
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
    }

    [Fact]
    public void SourceOnlyDataContextBindingUsesTheObjectItReferences()
    {
        var text = Window("""
            <Window.Resources><vm:Customer x:Key="Customer" /></Window.Resources>
            <Grid DataContext="{Binding Source={StaticResource Customer}}"><TextBlock Text="{Binding Nmae}" /></Grid>
            """);
        var issue = Assert.Single(Analyze(text));
        Assert.Contains("Customer", issue.Message);
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
    }

    [Fact]
    public void DataContextCanReferenceAKnownLocalResourceDirectly()
    {
        var text = Window("<Window.Resources><vm:Customer x:Key=\"Customer\" /></Window.Resources><Grid DataContext=\"{StaticResource Customer}\"><TextBlock Text=\"{Binding Nmae}\" /></Grid>");
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Customer", issue.Message);
    }

    [Fact]
    public void APocoPropertyNamedDataContextKeepsItsDeclaredPropertyType()
    {
        var text = Window("<Window.Resources><vm:AppViewModel x:Key=\"Source\" /></Window.Resources><TextBlock Text=\"{Binding DataContext.Nmae, Source={StaticResource Source}}\" />");
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Customer", issue.Message);
    }

    [Fact]
    public void InvalidMultipleRelativeSourceObjectsStayUnknownInsteadOfThrowing()
    {
        var text = Window("<TextBlock><TextBlock.Text><Binding Path=\"RuntimeOnly\"><Binding.RelativeSource><RelativeSource Mode=\"Self\"/><RelativeSource Mode=\"Self\"/></Binding.RelativeSource></Binding></TextBlock.Text></TextBlock>");
        Assert.Empty(Analyze(text));
    }

    [Theory]
    [InlineData("Customers[0].Name")]
    [InlineData("Array[0].Name")]
    [InlineData("ByKey[primary].Name")]
    [InlineData("Customers/Name")]
    [InlineData("Customers.Name")]
    public void TypedCollectionsIndexersAndCurrencyPathsResolveTheirItems(string path)
    {
        Assert.Empty(Analyze(Window($"<TextBlock Text=\"{{Binding {path}}}\" />")));
        var typo = path.Replace("Name", "Nmae", StringComparison.Ordinal);
        var text = Window($"<TextBlock Text=\"{{Binding {typo}}}\" />");
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Customer", issue.Message);
    }

    [Theory]
    [InlineData("Customers[0].Na$$me")]
    [InlineData("Customers/Na$$me")]
    [InlineData("ByKey[primary].Na$$me")]
    public void AdvancedPathCompletionPreservesTheCurrentSegmentReplacementSpan(string path)
    {
        var marked = Window($"<TextBlock Text=\"{{Binding {path}}}\" />");
        var position = marked.IndexOf("$$", StringComparison.Ordinal);
        var text = marked.Remove(position, 2);
        var result = Service.Complete(text, position, 19, Fixture.Value);
        Assert.NotNull(result);
        Assert.Equal("Name", text.Substring(result.Start, result.Length));
        Assert.Contains(result.Items, item => item.DisplayText == "Name");
        Assert.Equal(19, result.Version);
    }

    [Fact]
    public void InlineItemTemplateInfersItsItemTypeFromTheOwnersItemsSource()
    {
        var text = Window("""
            <ItemsControl ItemsSource="{Binding Customers}">
                <ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding Nmae}" /></DataTemplate></ItemsControl.ItemTemplate>
            </ItemsControl>
            """);
        var issue = Assert.Single(Analyze(text));
        Assert.Contains("Customer", issue.Message);
    }

    [Fact]
    public void NavigationAndHoverResolveTheSamePropertySymbolAsValidation()
    {
        var text = Window("<TextBlock Text=\"{Binding Selected.Name}\" />");
        var position = text.IndexOf("Selected.Name", StringComparison.Ordinal) + "Selected.".Length + 1;
        var location = Assert.Single(Service.GetDefinition("View.xaml", text, position, Fixture.Value));
        var declaration = Fixture.Value.SyntaxTrees.Single(tree => tree.FilePath == location.Path).GetText().ToString(new Microsoft.CodeAnalysis.Text.TextSpan(location.Start, location.Length));
        Assert.Equal("Name", declaration);
        Assert.Equal("ViewModels.cs", location.Path);
        var hover = Service.GetHover("View.xaml", text, position, Fixture.Value);
        Assert.NotNull(hover);
        Assert.Equal("Name", text.Substring(hover.Start, hover.Length));
        Assert.Contains("Customer.Name", hover.Text);
        Assert.Contains("d:DesignInstance", hover.Text);
    }

    [Fact]
    public void ElementNameNavigationSelectsTheXamlNameDeclaration()
    {
        var text = Window("<Grid><TextBox x:Name=\"Input\"/><TextBlock Text=\"{Binding Text, ElementName=Input}\"/></Grid>");
        var position = text.LastIndexOf("Input", StringComparison.Ordinal) + 1;
        var location = Assert.Single(Service.GetDefinition("View.xaml", text, position, Fixture.Value));
        Assert.Equal("View.xaml", location.Path);
        Assert.Equal(text.IndexOf("Input", StringComparison.Ordinal), location.Start);
        Assert.Equal("Input", text.Substring(location.Start, location.Length));
    }

    [Theory]
    [InlineData("<TextBlock Text=\"{Binding Tetx, ElementName='  In&#112;ut  '}\"/>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding Path=\"Tetx\" ElementName=\"  In&#112;ut  \"/></TextBlock.Text></TextBlock>")]
    public void ElementNameInferenceAndNavigationShareRuntimeNameAliasesAndEntitySpans(string consumer)
    {
        var text = Window("<Grid><vm:AliasNamedControl Identifier=\"In&#112;ut\"/>" + consumer + "</Grid>");
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Tetx", text.Substring(issue.Start, issue.Length));
        Assert.Contains("AliasNamedControl", issue.Message);
        Assert.Contains("Text", issue.Message);

        var reference = text.LastIndexOf("In&#112;ut", StringComparison.Ordinal);
        // A caret within an encoded character must select the complete authored declaration.
        var location = Assert.Single(Service.GetDefinition("View.xaml", text, reference + 4, Fixture.Value));
        Assert.Equal(text.IndexOf("In&#112;ut", StringComparison.Ordinal), location.Start);
        Assert.Equal("In&#112;ut", text.Substring(location.Start, location.Length));
        var corrected = text.Replace("Tetx", "Text", StringComparison.Ordinal);
        Assert.Empty(Analyze(corrected));
        int inline = corrected.IndexOf("Text, ElementName", StringComparison.Ordinal);
        int propertyPosition = inline >= 0 ? inline + 1 : corrected.IndexOf("Path=\"Text\"", StringComparison.Ordinal) + 7;
        var property = Assert.Single(Service.GetDefinition("View.xaml", corrected, propertyPosition, Fixture.Value));
        Assert.Equal("ViewModels.cs", property.Path);
    }

    [Fact]
    public void ElementNameObjectAttributeDoesNotInterpretLiteralQuotesAsMarkupArgumentQuotes()
    {
        var text = Window("<Grid><TextBox x:Name=\"Input\"/><TextBlock><TextBlock.Text><Binding Path=\"RuntimeOnly\" ElementName=\"'Input'\"/></TextBlock.Text></TextBlock></Grid>");
        Assert.Empty(Analyze(text));
        Assert.Empty(Service.GetDefinition("View.xaml", text, text.LastIndexOf("Input", StringComparison.Ordinal) + 1, Fixture.Value));
    }

    [Theory]
    [InlineData("<TextBlock Text=\"{Binding RuntimeOnly, ElementName=Input, Source=other}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding RuntimeOnly, ElementName=Input, RelativeSource={RelativeSource Self}}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding RuntimeOnly, ElementName=Input, ElementName=Input}\"/>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding Path=\"RuntimeOnly\" ElementName=\"Input\" Source=\"other\"/></TextBlock.Text></TextBlock>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding Path=\"RuntimeOnly\" ElementName=\"Input\"><Binding.Source><vm:Customer/></Binding.Source></Binding></TextBlock.Text></TextBlock>")]
    public void ConflictingElementNameSelectorsDoNotProduceInferenceOrNavigation(string consumer)
    {
        var text = Window("<Grid><TextBox x:Name=\"Input\"/>" + consumer + "</Grid>");
        Assert.Empty(Analyze(text));
        Assert.Empty(Service.GetDefinition("View.xaml", text, text.LastIndexOf("Input", StringComparison.Ordinal) + 1, Fixture.Value));
    }

    [Theory]
    [InlineData("<TextBlock Text=\"{Binding RuntimeOnly, ElementName=Input}\"/>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding Path=\"RuntimeOnly\" ElementName=\"Input\"/></TextBlock.Text></TextBlock>")]
    public void AmbiguousFrameworkBindingMetadataDoesNotFallBackToLexicalElementNameResolution(string consumer)
    {
        var compilation = Fixture.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "Lookalike")]
            namespace Lookalike { public class Binding { } }
            """, path: "Lookalike.cs"));
        var text = Window("<Grid><TextBox x:Name=\"Input\"/>" + consumer + "</Grid>");
        Assert.Empty(Service.Analyze("View.xaml", text, 1, compilation));
        Assert.Empty(Service.GetDefinition("View.xaml", text, text.LastIndexOf("Input", StringComparison.Ordinal) + 1, compilation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElementNameNavigationWaitsForWellFormedXmlWhileMemberAssistanceRemainsTolerant(bool malformedClose)
    {
        var valid = Window("<Grid><TextBox x:Name=\"Input\"/><TextBlock Text=\"{Binding ElementName=Input, Path=Text}\"/></Grid>");
        var text = malformedClose ? valid.Replace("</Grid>", "</Border>", StringComparison.Ordinal) : valid[..valid.LastIndexOf("</Window>", StringComparison.Ordinal)];
        Assert.Empty(Service.GetDefinition("View.xaml", text, text.LastIndexOf("Input", StringComparison.Ordinal) + 1, Fixture.Value));

        int member = text.IndexOf("Path=Text", StringComparison.Ordinal) + "Path=".Length;
        var definition = Assert.Single(Service.GetDefinition("View.xaml", text, member + 1, Fixture.Value));
        Assert.Equal("Framework.cs", definition.Path);
        var completion = Service.Complete(text, member + 2, 12, Fixture.Value);
        Assert.NotNull(completion);
        Assert.Contains(completion.Items, item => item.DisplayText == "Text");
    }

    [Fact]
    public void ElementNameNavigationHonorsTheDocumentTextBudget()
    {
        var text = Window("<Grid><TextBox x:Name=\"Input\"/><TextBlock Text=\"{Binding Text, ElementName=Input}\"/><!--"
            + new string(' ', 1_000_001) + "--></Grid>");
        Assert.Empty(Service.GetDefinition("View.xaml", text, text.LastIndexOf("Input", StringComparison.Ordinal) + 1, Fixture.Value));
    }

    [Fact]
    public void ElementNameDoesNotTreatAnOrdinaryNamePropertyAsTheRuntimeAlias()
    {
        var text = Window("<Grid><vm:AliasNamedControl Name=\"Input\" Identifier=\"Actual\"/><TextBlock Text=\"{Binding RuntimeOnly, ElementName=Input}\"/></Grid>");
        Assert.Empty(Analyze(text));
        Assert.Empty(Service.GetDefinition("View.xaml", text, text.LastIndexOf("Input", StringComparison.Ordinal) + 1, Fixture.Value));

        var objectText = $"<vm:NamedObject {Ns} Name=\"Input\" Text=\"{{Binding RuntimeOnly, ElementName=Input}}\"/>";
        Assert.Empty(Analyze(objectText));
        Assert.Empty(Service.GetDefinition("View.xaml", objectText, objectText.LastIndexOf("Input", StringComparison.Ordinal) + 1, Fixture.Value));
    }

    [Fact]
    public void ConflictingAliasAndDirectiveDoNotProduceBindingInferenceOrNavigation()
    {
        var text = Window("<Grid><vm:AliasNamedControl x:Name=\"Input\" Identifier=\"Input\"/><TextBlock Text=\"{Binding RuntimeOnly, ElementName=Input}\"/></Grid>");
        Assert.Empty(Analyze(text));
        Assert.Empty(Service.GetDefinition("View.xaml", text, text.LastIndexOf("Input", StringComparison.Ordinal) + 1, Fixture.Value));
    }

    [Fact]
    public void CustomNamescopeContentDoesNotBorrowTheDocumentsElementNameSource()
    {
        var text = Window("<Grid><TextBox x:Name=\"Input\"/><vm:CustomScope><TextBlock Text=\"{Binding RuntimeOnly, ElementName=Input}\"/></vm:CustomScope></Grid>");
        Assert.Empty(Analyze(text));
        Assert.Empty(Service.GetDefinition("View.xaml", text, text.LastIndexOf("Input", StringComparison.Ordinal) + 1, Fixture.Value));
    }

    [Fact]
    public void TypoFixUsesRawEntitySpanAndTheExactDocumentVersionAndHash()
    {
        var text = Window("<TextBlock Text=\"{Binding Selected.&#78;mae}\" />");
        var position = text.IndexOf("&#78;mae", StringComparison.Ordinal) + 2;
        var action = Assert.Single(Service.GetCodeActions("View.xaml", text, position, 41, Fixture.Value));
        Assert.Equal(41, action.Edit.Version);
        Assert.Equal("View.xaml", action.Edit.Path);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), action.Edit.ExpectedTextHash);
        var edit = Assert.Single(action.Edit.Edits);
        Assert.Equal("&#78;mae", text.Substring(edit.Start, edit.Length));
        Assert.Equal("Name", edit.NewText);
        Assert.Empty(Analyze(text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText)));
    }

    [Fact]
    public void UnknownRuntimeSourcesDoNotOfferSpeculativeTypoFixes()
    {
        var text = Window("<TextBlock Text=\"{Binding Nmae, Source={DynamicResource Customer}}\" />");
        var position = text.IndexOf("Nmae", StringComparison.Ordinal) + 1;
        Assert.Empty(Service.GetCodeActions("View.xaml", text, position, 41, Fixture.Value));
        Assert.Empty(Service.GetDefinition("View.xaml", text, position, Fixture.Value));
        Assert.Contains("runtime", Service.GetHover("View.xaml", text, position, Fixture.Value)!.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static CSharpCompilation CreateCompilation()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var framework = CSharpSyntaxTree.ParseText("""
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows")]
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows.Controls")]
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows.Data")]
            namespace System.Windows.Markup
            {
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)]
                public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : System.Attribute { }
                [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
                public sealed class RuntimeNamePropertyAttribute(string name) : System.Attribute { }
                public interface INameScope { }
            }
            namespace System.Windows
            {
                [System.Windows.Markup.RuntimeNameProperty("Name")]
                public class FrameworkElement { public object DataContext { get; set; } public double ActualWidth { get; } public string Name { get; set; } public ResourceDictionary Resources { get; set; } }
                public class Window : FrameworkElement { public string Title { get; set; } }
                public class Setter { public string Property { get; set; } public object Value { get; set; } public string TargetName { get; set; } }
                public class ResourceDictionary : System.Windows.Markup.INameScope { }
                public class Style : System.Windows.Markup.INameScope { public ResourceDictionary Resources { get; set; } }
                public class FrameworkTemplate : System.Windows.Markup.INameScope { public ResourceDictionary Resources { get; set; } }
                public class DataTemplate : FrameworkTemplate { }
                public class HierarchicalDataTemplate : DataTemplate { }
            }
            namespace System.Windows.Controls
            {
                public class Grid : System.Windows.FrameworkElement { }
                public class Button : System.Windows.FrameworkElement { }
                public class TextBox : System.Windows.FrameworkElement { public string Text { get; set; } }
                public class TextBlock : System.Windows.FrameworkElement { public string Text { get; set; } }
                public class ItemsControl : System.Windows.FrameworkElement { public System.Collections.IEnumerable ItemsSource { get; set; } }
                public class ControlTemplate : System.Windows.FrameworkTemplate { }
                public class ItemsPanelTemplate : System.Windows.FrameworkTemplate { }
            }
            namespace System.Windows.Data { public class Binding { } }
            """, path: "Framework.cs");
        var models = CSharpSyntaxTree.ParseText("""
            namespace Demo;
            public class RootWindow : System.Windows.Window { public string ViewTitle { get; set; } }
            [System.Windows.Markup.RuntimeNameProperty("Identifier")]
            public class AliasNamedControl : System.Windows.FrameworkElement
            {
                public string Identifier { get; set; }
                public string Text { get; set; }
            }
            public class NamedObject { public string Name { get; set; } public string Text { get; set; } }
            public class CustomScope : System.Windows.Controls.Grid, System.Windows.Markup.INameScope { }
            public class Customer
            {
                /// <summary>The customer's display name.</summary>
                public string Name { get; set; }
                public int Age { get; set; }
            }
            public class AppViewModel
            {
                public string Title { get; set; }
                public Customer Selected { get; set; }
                public Customer DataContext { get; set; }
                public System.Collections.Generic.List<Customer> Customers { get; set; }
                public Customer[] Array { get; set; }
                public System.Collections.Generic.Dictionary<string, Customer> ByKey { get; set; }
            }
            """, path: "ViewModels.cs", options: new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose));
        var compilation = CSharpCompilation.Create("BindingFeatureFixture", [framework, models], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
