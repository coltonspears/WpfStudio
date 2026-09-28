using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlSymbolOccurrenceTests
{
    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:c='clr-namespace:OccurrenceFixture'";
    private static readonly Lazy<CSharpCompilation> Fixture = new(CreateCompilation);
    private static string Window(string body, string context = "Customer") => $"<Window {Ns} x:Class='OccurrenceFixture.View' d:DataContext='{{d:DesignInstance c:{context}}}'>{body}</Window>";
    private static XamlSymbolOccurrenceResult Bindings(string text) => new XamlLanguageService().GetSymbolOccurrences(text, Fixture.Value);
    private static XamlSymbolOccurrenceResult Events(string text) => new XamlEventService().GetSymbolOccurrences(text, Fixture.Value);

    [Fact]
    public void IdenticalPropertySpellingRetainsUnrelatedDeclarationIdentity()
    {
        var text = Window("<Grid><TextBlock Text='{Binding Name}'/><Grid d:DataContext='{d:DesignInstance c:Order}'><TextBlock Text='{Binding Name}'/></Grid></Grid>");
        var result = Bindings(text);
        Assert.True(result.IsComplete);
        Assert.False(result.CoverageLimited);
        Assert.Equal(2, result.Occurrences.Count);
        Assert.False(SymbolEqualityComparer.Default.Equals(result.Occurrences[0].Symbol, result.Occurrences[1].Symbol));
        Assert.Equal(new[] { "Customer", "Order" }, result.Occurrences.Select(occurrence => occurrence.Symbol.ContainingType.Name));
        Assert.All(result.Occurrences, occurrence => Assert.Equal("Name", text.Substring(occurrence.Start, occurrence.Length)));
        Assert.Contains(result.Warnings, warning => warning.Contains("d:DesignInstance", StringComparison.Ordinal));
    }

    [Fact]
    public void TypedDataTemplateUsesItsOwnPropertyDeclaration()
    {
        var text = Window("<Window.Resources><DataTemplate x:Key='Order' DataType='{x:Type c:Order}'><TextBlock Text='{Binding Name}'/></DataTemplate></Window.Resources><TextBlock Text='{Binding Name}'/>");
        var result = Bindings(text);
        Assert.True(result.IsComplete);
        Assert.Equal(new[] { "Order", "Customer" }, result.Occurrences.Select(occurrence => occurrence.Symbol.ContainingType.Name));
    }

    [Fact]
    public void InheritedPartialGeneratedAndReferencedPropertiesKeepActualSymbols()
    {
        var result = Bindings(Window("<TextBlock Text='{Binding Title}'/><TextBlock Text='{Binding GeneratedValue}'/><TextBlock Text='{Binding External.Code}'/>"));
        Assert.True(result.IsComplete);
        Assert.Equal(new[] { "Title", "GeneratedValue", "External", "Code" }, result.Occurrences.Select(occurrence => occurrence.Symbol.Name));
        Assert.Equal("BaseModel", result.Occurrences[0].Symbol.ContainingType.Name);
        Assert.EndsWith("Customer.g.cs", result.Occurrences[1].Symbol.Locations.Single().SourceTree!.FilePath);
        Assert.All(result.Occurrences[^1].Symbol.Locations, location => Assert.True(location.IsInMetadata));
    }

    [Fact]
    public void ConstructedGenericPropertiesMapToOriginalDefinition()
    {
        var result = Bindings(Window("<TextBlock Text='{Binding Box.Value.Name}'/>", "Model"));
        Assert.True(result.IsComplete);
        var value = result.Occurrences.Single(occurrence => occurrence.Symbol.Name == "Value").Symbol;
        Assert.True(SymbolEqualityComparer.Default.Equals(value, Fixture.Value.GetTypeByMetadataName("OccurrenceFixture.Box`1")!.GetMembers("Value").Single()));
    }

    [Fact]
    public void NamesAndRelativeSourcesResolvePropertiesWithoutTreatingNamesAsOccurrences()
    {
        var text = Window("""
            <Grid>
              <TextBox x:Name="Editor"/>
              <TextBlock Text="{Binding Text, ElementName=Editor}"/>
              <TextBlock Text="{Binding ActualWidth, RelativeSource={RelativeSource Self}}"/>
              <TextBlock Text="{Binding DataContext.Name, RelativeSource={RelativeSource AncestorType={x:Type Grid}}}"/>
            </Grid>
            """);
        var result = Bindings(text);
        Assert.True(result.IsComplete);
        Assert.Equal(new[] { "Text", "ActualWidth", "DataContext", "Name" }, result.Occurrences.Select(occurrence => occurrence.Symbol.Name));
        Assert.DoesNotContain(result.Occurrences, occurrence => text.Substring(occurrence.Start, occurrence.Length) == "Editor");
    }

    [Fact]
    public void TemplatedParentAndStaticResourceUseDeclaredSources()
    {
        var text = Window("""
            <Window.Resources>
              <c:Order x:Key="Order"/>
              <ControlTemplate x:Key="Editor" TargetType="TextBox"><TextBlock Text="{Binding Text, RelativeSource={RelativeSource TemplatedParent}}"/></ControlTemplate>
            </Window.Resources>
            <TextBlock Text="{Binding Name, Source={StaticResource Order}}"/>
            """);
        var result = Bindings(text);
        Assert.True(result.IsComplete);
        Assert.Equal(new[] { "TextBox", "Order" }, result.Occurrences.Select(occurrence => occurrence.Symbol.ContainingType.Name));
    }

    [Fact]
    public void EntityEncodedSegmentsReturnFullRawUtf16Spans()
    {
        var text = Window("<TextBlock Tag='😀 &amp; text' Text='{Binding N&#97;me}'/>");
        var occurrence = Assert.Single(Bindings(text).Occurrences);
        Assert.Equal("Name", occurrence.Symbol.Name);
        Assert.Equal("N&#97;me", text.Substring(occurrence.Start, occurrence.Length));
        Assert.Equal(text.IndexOf("N&#97;me", StringComparison.Ordinal), occurrence.Start);
        Assert.Equal("BindingProperty", occurrence.Kind);
    }

    [Fact]
    public void IndexersAndCollectionCurrencyExcludeLiteralKeysAndIndexerSymbols()
    {
        var text = Window("<TextBlock Text='{Binding ByKey[Name].Name}'/><TextBlock Text='{Binding Customers/Name}'/>", "Model");
        var result = Bindings(text);
        Assert.True(result.IsComplete);
        Assert.Equal(new[] { "ByKey", "Name", "Customers", "Name" }, result.Occurrences.Select(occurrence => occurrence.Symbol.Name));
        Assert.DoesNotContain(result.Occurrences, occurrence => occurrence.Symbol is IPropertySymbol { IsIndexer: true });
        Assert.DoesNotContain(result.Occurrences, occurrence => occurrence.Start == text.IndexOf("[Name]", StringComparison.Ordinal) + 1);
    }

    [Fact]
    public void CommentsLiteralTextAndIgnoredDesignAttributesAreNotReferences()
    {
        var text = Window("""
            <Grid xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" xmlns:i="clr-namespace:OccurrenceFixture" mc:Ignorable="i">
              <!-- <TextBlock Text="{Binding Name}"/> -->
              <TextBlock Text="Name" d:Text="{Binding Name}"/>
              <i:Order Name="{Binding Name}"/>
              <TextBlock Text="{Binding Name}"/>
            </Grid>
            """);
        var result = Bindings(text);
        Assert.True(result.IsComplete);
        Assert.Single(result.Occurrences);
        Assert.Equal(text.LastIndexOf("Name}", StringComparison.Ordinal), result.Occurrences[0].Start);
    }

    [Theory]
    [InlineData("<TextBlock Text='{Binding Name, Source={DynamicResource Unknown}}'/>")]
    [InlineData("<Grid DataContext='{Binding Runtime}'><TextBlock Text='{Binding Name}'/></Grid>")]
    [InlineData("<ControlTemplate TargetType='TextBox'><TextBlock Text='{Binding Name}'/></ControlTemplate>")]
    [InlineData("<TextBlock Text='{Binding (Grid.Row)}'/>")]
    [InlineData("<TextBlock Text='{TemplateBinding Text}'/>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding><Binding.Path>Name</Binding.Path></Binding></TextBlock.Text></TextBlock>")]
    public void UnknownAndUnsupportedPathsReportPartialSemanticCoverage(string body)
    {
        var result = Bindings(Window(body));
        Assert.False(result.IsComplete);
        Assert.False(result.CoverageLimited);
        Assert.NotEmpty(result.Warnings);
        Assert.Empty(result.Occurrences);
    }

    [Fact]
    public void MissingContinuationRetainsOnlyProvenPrefixSymbols()
    {
        var result = Bindings(Window("<TextBlock Text='{Binding Selected.Missing.Name}'/>", "Model"));
        Assert.False(result.IsComplete);
        Assert.False(result.CoverageLimited);
        Assert.Equal("Selected", Assert.Single(result.Occurrences).Symbol.Name);
    }

    [Fact]
    public void DataContextAssignmentUsesIncomingSourceThenChildUsesItsResult()
    {
        var result = Bindings(Window("<Grid DataContext='{Binding Selected}'><TextBlock Text='{Binding Name}'/></Grid>", "Model"));
        Assert.True(result.IsComplete);
        Assert.Equal(new[] { "Model", "Customer" }, result.Occurrences.Select(occurrence => occurrence.Symbol.ContainingType.Name));
        Assert.Equal(new[] { "Selected", "Name" }, result.Occurrences.Select(occurrence => occurrence.Symbol.Name));
    }

    [Fact]
    public void NamedSourcesInsideIgnoredContentCannotEstablishReferences()
    {
        var text = Window("<Grid xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' xmlns:i='clr-namespace:OccurrenceFixture' mc:Ignorable='i'><i:Order x:Name='Ignored'/><TextBlock Text='{Binding Name, ElementName=Ignored}'/></Grid>");
        var result = Bindings(text);
        Assert.False(result.IsComplete);
        Assert.Empty(result.Occurrences);
    }

    [Fact]
    public void EventOccurrencesSelectTheActualOverloadAndPreserveEntitySpan()
    {
        var text = Window("<Button Click='  Ha&#110;dle  '/><TextBlock Text='Handle'/>");
        var result = Events(text);
        Assert.True(result.IsComplete, string.Join("\n", result.Warnings));
        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal("EventHandler", occurrence.Kind);
        Assert.Equal("Ha&#110;dle", text.Substring(occurrence.Start, occurrence.Length));
        var method = Assert.IsAssignableFrom<IMethodSymbol>(occurrence.Symbol);
        Assert.Equal("System.EventArgs", method.Parameters[1].Type.ToDisplayString());
        Assert.Same(Fixture.Value.SyntaxTrees.Single(tree => tree.FilePath == "View.xaml.cs"), method.Locations.Single().SourceTree);
    }

    [Theory]
    [InlineData("<Button Click='Absent'/>")]
    [InlineData("<Button Click='StaticHandle'/>")]
    [InlineData("<Button Click='{DynamicResource Handler}'/>")]
    [InlineData("<c:MissingWidget Click='Handle'/>")]
    public void UnresolvedEventContextsDoNotInventOccurrences(string body)
    {
        var result = Events(Window(body));
        Assert.False(result.IsComplete);
        Assert.False(result.CoverageLimited);
        Assert.Empty(result.Occurrences);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void NoRootClassIsOnlyIncompleteWhenAnEventReferenceNeedsIt()
    {
        const string declaration = "x:Class='OccurrenceFixture.View'";
        Assert.True(Events(Window("<TextBlock Text='{Binding Name}'/>").Replace(declaration, "")).IsComplete);
        var result = Events(Window("<Button Click='Handle'/>").Replace(declaration, ""));
        Assert.False(result.IsComplete);
        Assert.Empty(result.Occurrences);
    }

    [Fact]
    public void MalformedXmlNeverProducesRenameOccurrences()
    {
        var text = Window("<TextBlock Text='{Binding Name}'><Button Click='Handle'/>");
        foreach (var result in new[] { Bindings(text), Events(text) })
        {
            Assert.False(result.IsComplete);
            Assert.True(result.CoverageLimited);
            Assert.Empty(result.Occurrences);
        }
    }

    [Fact]
    public void TextDepthAndPathBudgetsAreExplicitHardCoverageLimits()
    {
        foreach (var text in new[] { new string(' ', 1_000_001), Window(string.Concat(Enumerable.Repeat("<Grid>", 260)) + string.Concat(Enumerable.Repeat("</Grid>", 260))) })
        {
            Assert.True(Bindings(text).CoverageLimited);
            Assert.True(Events(text).CoverageLimited);
        }
        var bounded = Bindings(Window(string.Concat(Enumerable.Repeat("<TextBlock Text='{Binding Name}'/>", 4097))));
        Assert.False(bounded.IsComplete);
        Assert.True(bounded.CoverageLimited);
        Assert.Equal(4096, bounded.Occurrences.Count);
    }

    [Fact]
    public void EventProbeBudgetIsDistinctFromUnresolvedSemanticCoverage()
    {
        var text = Window(string.Concat(Enumerable.Range(0, 513).Select(index => $"<Button Click='Missing{index}'/>")));
        var result = Events(text);
        Assert.False(result.IsComplete);
        Assert.True(result.CoverageLimited);
        Assert.Empty(result.Occurrences);
    }

    [Fact]
    public void CyclicIndexerPathsConsumeIndependentWalkBudgetWithoutMemberOccurrences()
    {
        string path = "Loop" + string.Concat(Enumerable.Repeat("[0]", 32768));
        var result = Bindings(Window($"<TextBlock Text='{{Binding {path}}}'/>", "Model"));
        Assert.False(result.IsComplete);
        Assert.True(result.CoverageLimited);
        Assert.Equal("Loop", Assert.Single(result.Occurrences).Symbol.Name);
        Assert.Contains(result.Warnings, warning => warning.Contains("binding-segment", StringComparison.Ordinal));
    }

    [Fact]
    public void CanceledAnalysisDoesNotReturnPartialRenameData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new XamlLanguageService().GetSymbolOccurrences(Window(""), Fixture.Value, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new XamlEventService().GetSymbolOccurrences(Window(""), Fixture.Value, cancellation.Token));
    }

    private static CSharpCompilation CreateCompilation()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var external = CSharpCompilation.Create("OccurrenceExternal", [CSharpSyntaxTree.ParseText("namespace ExternalModel; public class Entity { public string Code { get; set; } }")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        Assert.True(external.Emit(output).Success);
        var framework = CSharpSyntaxTree.ParseText("""
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows")]
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows.Controls")]
            namespace System.Windows.Markup {
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)]
                public sealed class XmlnsDefinitionAttribute(string uri, string ns) : System.Attribute {}
            }
            namespace System.Windows {
                public class FrameworkElement { public object DataContext { get; set; } public string Name { get; set; } public double ActualWidth { get; } public object Tag { get; set; } }
                public class Window : FrameworkElement {}
                public class DataTemplate : FrameworkElement { public object DataType { get; set; } }
                public class ControlTemplate : FrameworkElement { public System.Type TargetType { get; set; } }
            }
            namespace System.Windows.Controls {
                public class Grid : System.Windows.FrameworkElement {}
                public class TextBlock : System.Windows.FrameworkElement { public string Text { get; set; } }
                public class TextBox : System.Windows.FrameworkElement { public string Text { get; set; } }
                public class Button : System.Windows.FrameworkElement { public event System.EventHandler Click; }
            }
            """, path: "Framework.cs");
        var models = CSharpSyntaxTree.ParseText("""
            namespace OccurrenceFixture;
            public class BaseModel { public string Title { get; set; } }
            public partial class Customer : BaseModel { public string Name { get; set; } public ExternalModel.Entity External { get; set; } }
            public class Order { public string Name { get; set; } }
            public class Box<T> { public T Value { get; set; } }
            public class IndexLoop { public IndexLoop this[int index] => throw new System.InvalidOperationException(); }
            public class Model {
                public Customer Selected { get; set; }
                public System.Collections.Generic.List<Customer> Customers { get; set; }
                public System.Collections.Generic.Dictionary<string, Customer> ByKey { get; set; }
                public Box<Customer> Box { get; set; }
                public IndexLoop Loop { get; set; }
            }
            """, path: "Models.cs");
        var generated = CSharpSyntaxTree.ParseText("namespace OccurrenceFixture; public partial class Customer { public string GeneratedValue { get; set; } }", path: "Customer.g.cs");
        var view = CSharpSyntaxTree.ParseText("""
            namespace OccurrenceFixture;
            public partial class View : System.Windows.Window {
                private void Handle(object sender, System.EventArgs args) {}
                private void Handle(object sender, string args) {}
                private static void StaticHandle(object sender, System.EventArgs args) {}
            }
            """, path: "View.xaml.cs");
        var compilation = CSharpCompilation.Create("OccurrenceApp", [framework, models, generated, view], references.Append(MetadataReference.CreateFromImage(output.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
