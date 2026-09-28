using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlSchemaTests
{
    private const string Namespaces = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:c=\"clr-namespace:SchemaFixture\" xmlns:w=\"urn:widgets\"";
    private static readonly Lazy<CSharpCompilation> Fixture = new(CreateCompilation);

    [Fact]
    public void FrameworkMetadataRecognizesPropertiesEventsAttachedMembersAndReadOnlyCollectionElements()
    {
        var text = Window("""
            <Grid Button.Click="OnAnyButtonClick">
                <Grid.RowDefinitions><RowDefinition Height="Auto" /></Grid.RowDefinitions>
                <StackPanel Grid.Row="0">
                    <StackPanel.Children>
                        <Button Content="Save" Click="SaveClick" IsEnabled="True" />
                        <TextBlock c:BadgeProperties.Badge="New" Text="Hello" />
                    </StackPanel.Children>
                </StackPanel>
            </Grid>
            """);

        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void CustomAttributesUseRoslynSymbolsAndReportTheRawAttributeName()
    {
        var text = Window("<c:CustomerControl Tag=\"A&amp;B 😀\" Captoin=\"Hello\" />");

        var issue = Assert.Single(Analyze(text));

        Assert.Equal("XAMLSCHEMA002", issue.Id);
        AssertSpan(issue, text, "Captoin");
        Assert.Contains("Caption", issue.Message);
    }

    [Fact]
    public void UnknownTypesInKnownNamespacesAreReportedAtTheirName()
    {
        var text = Window("<c:CustmoerControl />");

        var issue = Assert.Single(Analyze(text));

        Assert.Equal("XAMLSCHEMA001", issue.Id);
        AssertSpan(issue, text, "c:CustmoerControl");
    }

    [Fact]
    public void XmlnsDefinitionMappingsAreReadFromReferencedAssemblyMetadata()
    {
        var text = Window("<w:ReferenceWidget Caption=\"Ready\" />");

        Assert.Empty(Analyze(text));
        var (_, result) = Complete(Window("<w:Ref$$erenceWidget />"));
        Assert.Contains(result.Items, item => item.DisplayText == "w:ReferenceWidget");
    }

    [Theory]
    [InlineData("<Bu$$tton />", "Button", "Button")]
    [InlineData("<c:Cust$$omerControl />", "c:CustomerControl", "c:CustomerControl")]
    [InlineData("<c:CustomerControl Cap$$tion=\"Ready\" />", "Caption", "Caption")]
    [InlineData("<c:CustomerControl Comp$$leted=\"OnCompleted\" />", "Completed", "Completed")]
    [InlineData("<TextBlock Grid.R$$ow=\"1\" />", "Grid.Row", "Grid.Row")]
    [InlineData("<TextBlock c:BadgeProperties.Ba$$dge=\"A\" />", "c:BadgeProperties.Badge", "c:BadgeProperties.Badge")]
    public void TypeAttributeEventAndAttachedCompletionsReplaceTheWholeToken(string body, string expectedSpan, string expectedItem)
    {
        var (text, result) = Complete(Window(body));

        Assert.Equal(expectedSpan, text.Substring(result.Start, result.Length));
        Assert.Contains(result.Items, item => item.DisplayText == expectedItem && item.InsertText == expectedItem);
        Assert.Equal(9, result.Version);
    }

    [Theory]
    [InlineData("IsReady", "Tr$$ue", "True")]
    [InlineData("Tone", "Oc$$ean", "Ocean")]
    public void BooleanAndEnumValueCompletionUsesThePropertyType(string property, string markedValue, string expected)
    {
        var (text, result) = Complete(Window($"<c:CustomerControl {property}=\"{markedValue}\" />"));

        Assert.Equal(expected, text.Substring(result.Start, result.Length));
        Assert.Equal(expected, Assert.Single(result.Items).InsertText);
    }

    [Fact]
    public void PropertyElementCompletionIncludesReadOnlyCollections()
    {
        var (text, result) = Complete(Window("<Grid><Grid.RowD$$efinitions /></Grid>"));

        Assert.Equal("Grid.RowDefinitions", text.Substring(result.Start, result.Length));
        Assert.Contains(result.Items, item => item.InsertText == "Grid.RowDefinitions");
    }

    [Fact]
    public void NamespaceValuesIncludeSourceAndMetadataMappings()
    {
        var (_, result) = Complete("<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:custom=\"urn:w$$\" />");

        Assert.Contains(result.Items, item => item.InsertText == "urn:widgets");
        var (_, clr) = Complete("<Window xmlns:custom=\"clr-namespace:SchemaF$$\" />");
        Assert.Contains(clr.Items, item => item.InsertText == "clr-namespace:SchemaFixture");
    }

    [Fact]
    public void IgnorableAndDesignNamespacesAndDirectivesDoNotCreateSchemaErrors()
    {
        var text = $$"""
            <Window {{Namespaces}}
                    xmlns:design="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    xmlns:future="urn:widgets" mc:Ignorable="design future"
                    x:Class="Demo.MainWindow" design:UnknownHint="Anything">
                <future:FutureControl UnknownProperty="Anything" />
                <c:CustomerControl x:Name="customer" design:UnknownHint="Anything" Caption="{custom:ArbitraryExtension}" />
            </Window>
            """;

        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void AReadOnlyPropertyAllowsPropertyElementUsageButNotAnAttributeAssignment()
    {
        var text = Window("<c:CustomerControl ReadOnly=\"1\" />");

        var issue = Assert.Single(Analyze(text));

        Assert.Equal("XAMLSCHEMA003", issue.Id);
        AssertSpan(issue, text, "ReadOnly");
    }

    [Fact]
    public void UnknownPropertyElementsReportTheMemberRatherThanTheWholeElement()
    {
        var text = Window("<Grid><Grid.RowDefintions /></Grid>");

        var issue = Assert.Single(Analyze(text));

        AssertSpan(issue, text, "RowDefintions");
    }

    [Fact]
    public void MalformedXmlReportsItsActualSourcePositionWhileCompletionRemainsAvailable()
    {
        var text = Window("<Grid><Button></Grid>");
        var issue = Assert.Single(Analyze(text));

        Assert.Equal("XAMLSYNTAX001", issue.Id);
        Assert.Equal("Error", issue.Severity);
        Assert.InRange(issue.Start, 0, text.Length);
        Assert.Equal(text.AsSpan(0, issue.Start).Count('\n') + 1, issue.Line);
        var (_, result) = Complete($"<Window {Namespaces}><c:CustomerControl Cap$$");
        Assert.Contains(result.Items, item => item.DisplayText == "Caption");
    }

    [Fact]
    public void SourceDefinitionsAndHoverResolveCustomProperties()
    {
        var text = Window("<c:CustomerControl Caption=\"Hello\" />");
        var position = text.IndexOf("Caption", StringComparison.Ordinal) + 2;
        var service = new XamlSchemaService();

        var location = Assert.Single(service.GetDefinition("View.xaml", text, position, Fixture.Value));
        Assert.Equal("SchemaControls.cs", location.Path);
        Assert.Equal("Caption", location.DisplayText);
        var hover = service.GetHover("View.xaml", text, position, Fixture.Value);
        Assert.NotNull(hover);
        Assert.Equal("Caption", text.Substring(hover.Start, hover.Length));
        Assert.Contains("CustomerControl.Caption", hover.Text);
    }

    [Fact]
    public void BindingValueCompletionIsLeftToTheBindingLanguageService()
    {
        var marked = Window("<TextBlock Text=\"{Binding Na$$me}\" />");
        var position = marked.IndexOf("$$", StringComparison.Ordinal);

        Assert.Null(new XamlSchemaService().Complete(marked.Replace("$$", "", StringComparison.Ordinal), position, 1, Fixture.Value));
    }

    [Fact]
    public void CompletionAfterAClosedTagDoesNotOfferAttributesInTextContent()
    {
        var text = $"<Window {Namespaces}><TextBlock />";

        Assert.Null(new XamlSchemaService().Complete(text, text.Length, 1, Fixture.Value));
    }

    [Fact]
    public void MissingWpfReferencesDoNotCauseMassFrameworkTypeErrors()
    {
        var compilation = CSharpCompilation.Create("Plain", references: PlatformReferences(), options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var text = "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"Hello\" /></Window>";

        Assert.Empty(new XamlSchemaService().Analyze("View.xaml", text, 1, compilation));
    }

    private static string Window(string body) => $"<Window {Namespaces}>\n{body}\n</Window>";
    [Fact]
    public void GetterOnlyAttachedCollectionsAreValidPropertyElements()
    {
        Assert.Empty(Analyze(Window("<Button><c:BadgeProperties.Behaviors><x:String>Behavior</x:String></c:BadgeProperties.Behaviors></Button>")));
        var issue = Assert.Single(Analyze(Window("<Button c:BadgeProperties.Behaviors=\"Behavior\"/>")));
        Assert.Equal("XAMLSCHEMA003", issue.Id);
        var (_, elements) = Complete(Window("<Button><c:BadgeProperties.Behav$$ /></Button>"));
        Assert.Contains(elements.Items, item => item.InsertText == "c:BadgeProperties.Behaviors");
        var (_, attributes) = Complete(Window("<Button c:BadgeProperties.Behav$$ />"));
        Assert.DoesNotContain(attributes.Items, item => item.InsertText == "c:BadgeProperties.Behaviors");
    }

    private static IReadOnlyList<WorkspaceDiagnostic> Analyze(string text) => new XamlSchemaService().Analyze("View.xaml", text, 1, Fixture.Value);
    private static (string Text, CompletionResult Result) Complete(string marked)
    {
        var position = marked.IndexOf("$$", StringComparison.Ordinal);
        Assert.True(position >= 0);
        var text = marked.Remove(position, 2);
        var result = new XamlSchemaService().Complete(text, position, 9, Fixture.Value);
        Assert.NotNull(result);
        return (text, result);
    }
    private static void AssertSpan(WorkspaceDiagnostic diagnostic, string text, string expected)
    {
        Assert.Equal(expected, text.Substring(diagnostic.Start, diagnostic.Length));
        Assert.Equal(text.IndexOf(expected, StringComparison.Ordinal), diagnostic.Start);
        Assert.Equal(text.AsSpan(0, diagnostic.Start).Count('\n') + 1, diagnostic.Line);
        Assert.Equal(diagnostic.Start - text.LastIndexOf('\n', diagnostic.Start), diagnostic.Column);
    }
    private static IReadOnlyList<MetadataReference> PlatformReferences() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    private static CSharpCompilation CreateCompilation()
    {
        // Read framework reference metadata, never load or instantiate WPF/project types.
        var dotnetRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.WindowsDesktop.App.Ref");
        var pack = Directory.GetDirectories(packRoot).OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version) ? version : new Version()).First();
        var referenceDirectory = Directory.GetDirectories(Path.Combine(pack, "ref")).OrderByDescending(path => path, StringComparer.Ordinal).First();
        var wpfPaths = Directory.GetFiles(referenceDirectory, "*.dll");
        var names = wpfPaths.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = PlatformReferences().Where(reference => !names.Contains(Path.GetFileName(reference.Display)))
            .Concat(wpfPaths.Select(path => MetadataReference.CreateFromFile(path))).ToArray();
        var library = CSharpCompilation.Create("SchemaWidgets", [CSharpSyntaxTree.ParseText("""
            [assembly: System.Windows.Markup.XmlnsDefinition("urn:widgets", "ReferenceWidgets")]
            namespace ReferenceWidgets;
            public class ReferenceWidget : System.Windows.Controls.Control
            {
                public string Caption { get; set; } = "Ready";
            }
            """, path: "ReferenceWidget.cs")], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = library.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var compilation = CSharpCompilation.Create("SchemaApp", [CSharpSyntaxTree.ParseText("""
            namespace SchemaFixture;
            public enum Palette { Sunset, Ocean }
            public class CustomerControl : System.Windows.Controls.Control
            {
                public string Caption { get; set; } = "Ready";
                public Palette Tone { get; set; }
                public bool IsReady { get; set; }
                public int ReadOnly => 1;
                public event System.EventHandler? Completed;
            }
            public static class BadgeProperties
            {
                public static string GetBadge(System.Windows.DependencyObject target) => "";
                public static void SetBadge(System.Windows.DependencyObject target, string value) { }
                public static System.Collections.Generic.List<string> GetBehaviors(System.Windows.DependencyObject target) => new();
            }
            """, path: "SchemaControls.cs")], references.Append(MetadataReference.CreateFromImage(output.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
