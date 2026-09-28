using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlLanguageServiceTests
{
    private const string Namespaces = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:vm=\"clr-namespace:Demo\" xmlns:model=\"clr-namespace:Domain;assembly=Domain\"";
    private const string DesignContext = "d:DataContext=\"{d:DesignInstance Type=vm:AppViewModel}\"";
    private const string FilePath = "CustomerView.xaml";
    private static readonly IReadOnlyList<MetadataReference> PlatformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    private static readonly Lazy<CSharpCompilation> FixtureCompilation = new(CreateCompilation);

    [Fact]
    public void DottedTypoIdentifiesTheMissingSegmentAndSuggestsARealMember()
    {
        var text = Window("""
            <Grid>
                <TextBlock Tag="A&amp;B 😀" Text="{Binding Selected.Nmae}" />
            </Grid>
            """);

        var issue = Assert.Single(Analyze(text));

        AssertSpan(issue, text, "Nmae");
        Assert.Contains("Nmae", issue.Message);
        Assert.Contains("Name", issue.Message);
    }

    [Fact]
    public void EntityEncodedBindingSegmentsReportTheirRawSourceSpan()
    {
        var text = Window("<TextBlock Tag=\"A&amp;B 😀\" Text=\"{Binding Selected.&#78;mae}\" />");

        var issue = Assert.Single(Analyze(text));

        AssertSpan(issue, text, "&#78;mae");
        Assert.Contains("Nmae", issue.Message);
        Assert.Contains("Name", issue.Message);
    }

    [Fact]
    public void NestedCompletionReplacesOnlyTheFinalSegmentInAnUnfinishedBinding()
    {
        var (text, position, result) = Complete($"<Window {Namespaces} {DesignContext}><TextBlock Text=\"{{Binding Selected.Na$$");

        Assert.NotNull(result);
        Assert.Equal(17, result.Version);
        Assert.Equal(position - 2, result.Start);
        Assert.Equal(2, result.Length);
        Assert.Equal("Na", text.Substring(result.Start, result.Length));
        Assert.Contains(result.Items, item => item.DisplayText == "Name" && item.InsertText == "Name");
        Assert.DoesNotContain(result.Items, item => item.DisplayText == "Title");
    }

    [Theory]
    [InlineData("Tit$$le", "Title", "Title")]
    [InlineData("Selected.Na$$me", "Name", "Name")]
    [InlineData("Sel$$ected.Name", "Selected", "Selected")]
    [InlineData("Selected.Na$$&#109;e", "Na&#109;e", "Name")]
    public void CompletionInsideAMemberReplacesTheWholeCurrentSegment(string markedPath, string rawSegment, string memberName)
    {
        var (text, _, result) = Complete(Window($"<TextBlock Text=\"{{Binding {markedPath}}}\" />"));

        Assert.NotNull(result);
        Assert.Equal(rawSegment, text.Substring(result.Start, result.Length));
        var item = Assert.Single(result.Items, candidate => candidate.DisplayText == memberName);
        var changed = text.Remove(result.Start, result.Length).Insert(result.Start, item.InsertText);
        var originalPath = markedPath.Replace("$$", "", StringComparison.Ordinal);
        var completedPath = originalPath.Replace(rawSegment, memberName, StringComparison.Ordinal);
        Assert.Contains($"Text=\"{{Binding {completedPath}}}\"", changed);
    }

    [Fact]
    public void NearestObjectDataContextWinsWithoutLeakingToItsSibling()
    {
        var text = Window("""
            <StackPanel>
                <Grid>
                    <Grid.DataContext><model:Customer /></Grid.DataContext>
                    <TextBlock Text="{Binding Nmae}" />
                </Grid>
                <TextBlock Text="{Binding Titl}" />
            </StackPanel>
            """);

        var issues = Analyze(text);

        Assert.Equal(2, issues.Count);
        var customerIssue = Assert.Single(issues, issue => issue.Start == text.IndexOf("Nmae", StringComparison.Ordinal));
        Assert.Contains("Name", customerIssue.Message);
        var rootIssue = Assert.Single(issues, issue => issue.Start == text.IndexOf("Titl}", StringComparison.Ordinal));
        Assert.Contains("Title", rootIssue.Message);
        AssertSpan(customerIssue, text, "Nmae");
        AssertSpan(rootIssue, text, "Titl}", expectedLength: 4);
    }

    [Fact]
    public void UnknownLocalDataContextStopsInheritedValidation()
    {
        var text = Window("""
            <StackPanel>
                <Grid DataContext="{DynamicResource RuntimeContext}">
                    <TextBlock Text="{Binding RuntimeOnly}" />
                </Grid>
                <TextBlock Text="{Binding Titl}" />
            </StackPanel>
            """);

        var issue = Assert.Single(Analyze(text));

        AssertSpan(issue, text, "Titl}", expectedLength: 4);
        Assert.Contains("Title", issue.Message);
    }

    [Fact]
    public void UnknownLocalDataContextOffersNoAncestorMemberCompletions()
    {
        var (_, _, result) = Complete(Window("""
            <Grid DataContext="{DynamicResource RuntimeContext}">
                <TextBlock Text="{Binding $$}" />
            </Grid>
            """));

        Assert.NotNull(result);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void DesignNamespacesAreResolvedByUriAndNestedTypeMarkupIsSupported()
    {
        const string text = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:lang="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:design="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:vm="clr-namespace:Demo"
                    design:DataContext="{design:DesignInstance Type={lang:Type vm:AppViewModel}, IsDesignTimeCreatable=False}">
                <TextBlock Text="{Binding Titl}" />
            </Window>
            """;

        var issue = Assert.Single(Analyze(text));

        AssertSpan(issue, text, "Titl}", expectedLength: 4);
        Assert.Contains("Title", issue.Message);
    }

    [Fact]
    public void InheritedPartialAndReferencedMembersUseCompilationSymbols()
    {
        // Extra is declared in a second partial-class source file. The Customer type
        // and its base class come from an emitted metadata reference, not regex input.
        var text = Window("""
            <StackPanel>
                <TextBlock Text="{Binding Status}" />
                <TextBlock Text="{Binding Extra}" />
                <TextBlock Text="{Binding Selected.Name}" />
                <TextBlock Text="{Binding Selected.Identifier}" />
                <TextBlock Text="{Binding Item.Name}" />
            </StackPanel>
            """);

        Assert.Empty(Analyze(text));

        var (_, _, result) = Complete(Window("<TextBlock Text=\"{Binding Item.Na$$}\" />"));
        Assert.NotNull(result);
        Assert.Contains(result.Items, item => item.DisplayText == "Name");
    }

    [Theory]
    [InlineData("{Binding Text, ElementName=Input}")]
    [InlineData("{Binding Path=Text, ElementName=Input}")]
    [InlineData("{Binding Name, Source={StaticResource RuntimeCustomer}}")]
    [InlineData("{Binding Source={StaticResource RuntimeCustomer}, Path=Name}")]
    [InlineData("{Binding ActualWidth, RelativeSource={RelativeSource Self}}")]
    [InlineData("{Binding ActualWidth, RelativeSource={RelativeSource AncestorType={x:Type Grid}}}")]
    public void ExplicitBindingSourcesAreNotValidatedAgainstTheRootContext(string binding)
    {
        var text = Window($"<TextBlock Text=\"{binding}\" />");

        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void TypedDataTemplateEstablishesItsOwnContext()
    {
        var text = Window("""
            <Window.Resources>
                <DataTemplate DataType="{x:Type model:Customer}">
                    <StackPanel>
                        <TextBlock Text="{Binding Name}" />
                        <TextBlock Text="{Binding Nmae}" />
                    </StackPanel>
                </DataTemplate>
            </Window.Resources>
            <TextBlock Text="{Binding Title}" />
            """);

        var issue = Assert.Single(Analyze(text));

        AssertSpan(issue, text, "Nmae");
        Assert.Contains("Name", issue.Message);
    }

    [Theory]
    [InlineData("ControlTemplate", "TargetType=\"Button\"")]
    [InlineData("DataTemplate", "")]
    public void TemplatesWithoutAKnownDataTypeDoNotInheritTheRootContext(string template, string attributes)
    {
        var text = Window($"<Window.Resources><{template} {attributes}><TextBlock Text=\"{{Binding RuntimeOnly}}\" /></{template}></Window.Resources>");

        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void ADataContextBindingResolvesAgainstItsIncomingContext()
    {
        var text = Window("""
            <StackPanel>
                <Grid DataContext="{Binding Selected}">
                    <TextBlock Text="{Binding Name}" />
                    <TextBlock Text="{Binding Nmae}" />
                </Grid>
                <TextBlock Text="{Binding Title}" />
            </StackPanel>
            """);

        var issue = Assert.Single(Analyze(text));

        AssertSpan(issue, text, "Nmae");
        Assert.Contains("Name", issue.Message);
    }

    [Theory]
    [InlineData("Runtime.RuntimeOnly")]
    [InlineData("Dynamic.RuntimeOnly")]
    public void RuntimeShapedMembersAreNotReportedAsMissingProperties(string bindingPath)
    {
        var text = Window($"<TextBlock Text=\"{{Binding {bindingPath}}}\" />");

        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void CollectionCurrencyPathsAreNotMistakenForMissingCollectionMembers()
    {
        var text = Window("<TextBlock Text=\"{Binding Customers.Name}\" />");

        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void AnUnresolvedBaseTypeDoesNotProduceMissingInheritedPropertyWarnings()
    {
        var compilation = FixtureCompilation.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
            namespace Demo;
            public class BrokenViewModel : MissingBaseType { }
            """, path: "BrokenViewModel.cs"));
        Assert.Contains(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0246");
        var text = Window("<TextBlock Text=\"{Binding InheritedProperty}\" />").Replace("vm:AppViewModel", "vm:BrokenViewModel", StringComparison.Ordinal);

        Assert.Empty(new XamlLanguageService().Analyze(FilePath, text, 17, compilation));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("RefreshCommand")]
    public void MissingGeneratorOutputDoesNotProduceWarningsOrInventCompletions(string bindingPath)
    {
        // Semantic marker declarations model generator input with its output unavailable.
        // This unit fixture does not execute the real Toolkit source generator.
        var compilation = FixtureCompilation.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
            namespace CommunityToolkit.Mvvm.ComponentModel
            {
                public sealed class ObservablePropertyAttribute : System.Attribute { }
            }
            namespace CommunityToolkit.Mvvm.Input
            {
                public sealed class RelayCommandAttribute : System.Attribute { }
            }
            namespace Demo
            {
                public partial class PendingViewModel
                {
                    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _name = "Ada";
                    [CommunityToolkit.Mvvm.Input.RelayCommand] private void Refresh() { }
                }
            }
            """, path: "PendingGenerator.cs"));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var text = Window($"<TextBlock Text=\"{{Binding {bindingPath}}}\" />").Replace("vm:AppViewModel", "vm:PendingViewModel", StringComparison.Ordinal);
        var service = new XamlLanguageService();

        Assert.Empty(service.Analyze(FilePath, text, 17, compilation));
        var position = text.IndexOf(bindingPath + "}", StringComparison.Ordinal) + bindingPath.Length;
        var result = service.Complete(text, position, 17, compilation);
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Items, item => item.DisplayText == bindingPath);
    }

    [Fact]
    public void AConvertedDataContextDoesNotReuseItsInputPropertyType()
    {
        var text = Window("""
            <Grid DataContext="{Binding Selected, Converter={StaticResource CustomerToPresentation}}">
                <TextBlock Text="{Binding PresentationOnly}" />
            </Grid>
            """);

        Assert.Empty(Analyze(text));
    }

    [Fact]
    public void AnInlineStyleDataContextSetterStopsAncestorInference()
    {
        var text = Window("""
            <StackPanel>
                <Grid>
                    <Grid.Style>
                        <Style TargetType="Grid">
                            <Setter Property="DataContext">
                                <Setter.Value><model:Customer /></Setter.Value>
                            </Setter>
                        </Style>
                    </Grid.Style>
                    <TextBlock Text="{Binding Name}" />
                </Grid>
                <TextBlock Text="{Binding Titl}" />
            </StackPanel>
            """);

        var issue = Assert.Single(Analyze(text));
        AssertSpan(issue, text, "Titl}", expectedLength: 4);
    }

    [Fact]
    public void CompletionOutsideABindingDefersToOtherXamlAssistance()
    {
        var (_, _, result) = Complete(Window("<TextBlock Te$$ />"));

        Assert.Null(result);
    }

    private static string Window(string body) => $"<Window {Namespaces} {DesignContext}>\n{body}\n</Window>";

    private static IReadOnlyList<WorkspaceDiagnostic> Analyze(string text) =>
        new XamlLanguageService().Analyze(FilePath, text, 17, FixtureCompilation.Value);

    private static (string Text, int Position, CompletionResult? Result) Complete(string markedText)
    {
        var position = markedText.IndexOf("$$", StringComparison.Ordinal);
        Assert.True(position >= 0, "The completion fixture needs a caret marker.");
        var text = markedText.Remove(position, 2);
        return (text, position, new XamlLanguageService().Complete(text, position, 17, FixtureCompilation.Value));
    }

    private static void AssertSpan(WorkspaceDiagnostic issue, string text, string expected, int? expectedLength = null)
    {
        var start = text.IndexOf(expected, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Equal(FilePath, issue.Path);
        Assert.Equal(start, issue.Start);
        Assert.Equal(expectedLength ?? expected.Length, issue.Length);
        Assert.Equal(text.AsSpan(0, start).Count('\n') + 1, issue.Line);
        Assert.Equal(start - text.LastIndexOf('\n', start), issue.Column);
    }

    private static CSharpCompilation CreateCompilation()
    {
        var domain = CSharpCompilation.Create("Domain", [CSharpSyntaxTree.ParseText("""
            namespace Domain;
            public class CustomerBase { public int Identifier { get; set; } }
            public class Customer : CustomerBase
            {
                public string Name { get; set; } = "Ada";
                public string City { get; set; } = "London";
            }
            """, path: "Customer.cs")], PlatformReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = domain.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));

        var compilation = CSharpCompilation.Create("App", [
            CSharpSyntaxTree.ParseText("""
                namespace Demo;
                public class BaseViewModel<T>
                {
                    public string Status { get; set; } = "Ready";
                    public T Item { get; set; } = default!;
                }
                public partial class AppViewModel : BaseViewModel<Domain.Customer>
                {
                    public string Title { get; set; } = "Customers";
                    public Domain.Customer Selected { get; set; } = new();
                    public System.Collections.ObjectModel.ObservableCollection<Domain.Customer> Customers { get; set; } = new();
                    public object Runtime { get; set; } = new();
                    public dynamic Dynamic { get; set; } = new object();
                }
                """, path: "AppViewModel.cs"),
            CSharpSyntaxTree.ParseText("""
                namespace Demo;
                public partial class AppViewModel { public string Extra { get; set; } = "More"; }
                """, path: "AppViewModel.Extra.cs")
        ], PlatformReferences.Append(MetadataReference.CreateFromImage(image.ToArray())), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
