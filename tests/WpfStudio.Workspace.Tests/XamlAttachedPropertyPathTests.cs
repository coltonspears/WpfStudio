using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlAttachedPropertyPathTests
{
    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:p='clr-namespace:AttachedFixture'";
    private static readonly Lazy<CSharpCompilation> Fixture = new(CreateCompilation);
    private static readonly XamlLanguageService Service = new();
    private static string View(string body, string context = "Model") => $"<Grid {Ns} d:DataContext='{{d:DesignInstance p:{context}}}'>{body}</Grid>";
    private static IReadOnlyList<WorkspaceDiagnostic> Analyze(string text) => Service.Analyze("View.xaml", text, 9, Fixture.Value);
    private static string Self(string path) => $"<TextBlock Text=\"{{Binding {path}, RelativeSource={{RelativeSource Self}}}}\"/>";

    [Theory]
    [InlineData("(Grid.Row)")]
    [InlineData("(p:Provider.Value).Name")]
    [InlineData("( p:Provider . Value ).Name")]
    [InlineData("(p:DerivedProvider.Value).Name")]
    [InlineData("(p:Provider.Items)[0].Name")]
    [InlineData("(p:Provider.Items)/Name")]
    public void AttachedPathsUseDeclaredValueTypesAndAcceptApplicableSources(string path)
    {
        Assert.Empty(Analyze(View(Self(path))));
        if (!path.EndsWith("Name", StringComparison.Ordinal)) return;
        string text = View(Self(path[..^4] + "Nmae"));
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Customer", issue.Message);
        Assert.Contains("Name", issue.Message);
    }

    [Fact]
    public void NestedPathsAndQuotedMarkupPathKeepTypedContinuation()
    {
        string text = View("<TextBlock Text=\"{Binding Path='Target.(p:Provider.Value).Nmae'}\"/>");
        var issue = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Customer", issue.Message);
    }

    [Fact]
    public void ObjectBindingsUseTheNamespaceInScopeAtTheirPathAttribute()
    {
        string text = View("<TextBlock><TextBlock.Text><Binding xmlns:p='clr-namespace:OtherFixture' Path='(p:Provider.Value).Nmae' RelativeSource='{RelativeSource Self}'/></TextBlock.Text></TextBlock>");
        var issue = Assert.Single(Analyze(text));
        Assert.Contains("Order", issue.Message);
        Assert.DoesNotContain("Customer", issue.Message);
    }

    [Fact]
    public void NamedAndTemplatedSourcesRetainTheirActualSourceTypes()
    {
        string text = View("""
            <TextBlock x:Name="Input"/>
            <TextBlock Text="{Binding (p:Provider.Value).Nmae, ElementName=Input}"/>
            <Grid.Resources><ControlTemplate x:Key="Control" TargetType="TextBlock">
              <TextBlock Text="{Binding (p:Provider.Value).Nmae, RelativeSource={RelativeSource TemplatedParent}}"/>
            </ControlTemplate></Grid.Resources>
            """);
        var issues = Analyze(text);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.Contains("Customer", issue.Message));
    }

    [Fact]
    public void AttachedDataContextAndItemsSourceFeedDescendantContexts()
    {
        string text = View("""
            <Grid DataContext="{Binding (p:Provider.Value), RelativeSource={RelativeSource Self}}">
              <TextBlock Text="{Binding Nmae}"/>
            </Grid>
            <ItemsControl ItemsSource="{Binding (p:Provider.Items), RelativeSource={RelativeSource Self}}">
              <ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding Nmae}"/></DataTemplate></ItemsControl.ItemTemplate>
            </ItemsControl>
            """);
        var issues = Analyze(text);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.Contains("Customer", issue.Message));
    }

    [Theory]
    [InlineData("(p:Provider.Va$$lue).Name", "Value", "Value")]
    [InlineData("(p:Provider.Va$$l&#117;e).Name", "Val&#117;e", "Value")]
    [InlineData("(p:Provider.Value).Na$$me", "Name", "Name")]
    [InlineData("(p:Provider.Items)[0].Na$$me", "Name", "Name")]
    [InlineData("(p:Pro$$vider.Value).Name", "p:Provider", "p:Provider")]
    [InlineData("(p:Pro$$v&#105;der.Value).Name", "p:Prov&#105;der", "p:Provider")]
    [InlineData("(Gr$$id.Row)", "Grid", "Grid")]
    public void CompletionReplacesOnlyTheCompleteRawOwnerOrMemberToken(string path, string replaced, string expected)
    {
        string marked = View(Self(path));
        int caret = marked.IndexOf("$$", StringComparison.Ordinal);
        string text = marked.Remove(caret, 2);
        var completion = Service.Complete(text, caret, 27, Fixture.Value);
        Assert.NotNull(completion);
        Assert.Equal(27, completion.Version);
        Assert.Equal(replaced, text.Substring(completion.Start, completion.Length));
        var item = Assert.Single(completion.Items, item => item.InsertText == expected);
        string changed = text.Remove(completion.Start, completion.Length).Insert(completion.Start, item.InsertText);
        Assert.Contains(expected, changed);
        Assert.Empty(Analyze(changed));
    }

    [Fact]
    public void ReadOnlyScalarAttachedPropertiesAreAvailableForReadingCompletion()
    {
        string marked = View(Self("(p:Provider.Co$$)"));
        int caret = marked.IndexOf("$$", StringComparison.Ordinal);
        var completion = Service.Complete(marked.Remove(caret, 2), caret, 1, Fixture.Value);
        Assert.Contains(completion!.Items, item => item.InsertText == "Count");
    }

    [Fact]
    public void OwnerGetterAndContinuationNavigateToSeparateRealSymbols()
    {
        string text = View(Self("(p:Provider.Val&#117;e).Name"));
        AssertDefinition("p:Provider", "Provider");
        AssertDefinition("Val&#117;e", "GetValue");
        AssertDefinition("Name", "Name");
        var hover = Service.GetHover("View.xaml", text, text.IndexOf("Val&#117;e", StringComparison.Ordinal) + 2, Fixture.Value);
        Assert.NotNull(hover);
        Assert.Equal("Val&#117;e", text.Substring(hover.Start, hover.Length));
        Assert.Contains("GetValue", hover.Text);

        void AssertDefinition(string authored, string declaration)
        {
            int position = text.IndexOf(authored, StringComparison.Ordinal) + 1;
            var location = Assert.Single(Service.GetDefinition("View.xaml", text, position, Fixture.Value));
            string source = Fixture.Value.SyntaxTrees.Single(tree => tree.FilePath == location.Path).GetText().ToString();
            Assert.Equal(declaration, source.Substring(location.Start, location.Length));
        }
    }

    [Theory]
    [InlineData("(p:Provider.Value).Nmae")]
    [InlineData("(p:Provider.WriteOnly).Nmae")]
    [InlineData("(p:Provider.NoRegistration).Nmae")]
    [InlineData("(p:Provider.Ambiguous).Nmae")]
    [InlineData("(p:Provider.NotRegistered).Nmae")]
    public void UnknownQualifiedMembersAndIncompatibleSourcesNeverBorrowTheOuterContext(string path)
    {
        // Model is a POCO, so even an otherwise valid attached property cannot apply.
        Assert.Empty(Analyze(View($"<TextBlock Text=\"{{Binding {path}}}\"/>")));
        if (!path.Contains(".Value)", StringComparison.Ordinal)) Assert.Empty(Analyze(View(Self(path))));
    }

    [Theory]
    [InlineData("()")]
    [InlineData("( )")]
    [InlineData("(0)")]
    [InlineData("((p:Provider.Value))")]
    [InlineData("(p:Provider.)")]
    [InlineData("(Name )")]
    [InlineData("( Name)")]
    [InlineData("(p:Provider.Value). ")]
    [InlineData("(p:Provider.Value)[(x:Int32)0]")]
    public void UnsupportedOrMalformedPathsNeverFabricateContinuationTypes(string path)
    {
        Assert.Empty(Analyze(View(Self(path + ".NotKnown"))));
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("  Name  ")]
    [InlineData("(  )")]
    [InlineData("(p:Provider.  )")]
    [InlineData("(p:Provider.Value).  ")]
    [InlineData("((")]
    [InlineData("[")]
    public void EveryCaretInWhitespaceOrPartialPathIsSafe(string path)
    {
        string text = View($"<TextBlock Text=\"{{Binding Path='{path}'}}\"/>");
        int start = text.IndexOf("Path='", StringComparison.Ordinal) + 6;
        for (int offset = 0; offset <= path.Length; offset++)
        {
            var completion = Service.Complete(text, start + offset, 1, Fixture.Value);
            if (completion is null) continue;
            Assert.InRange(completion.Start, 0, text.Length);
            Assert.InRange(completion.Length, 0, text.Length - completion.Start);
        }
    }

    [Fact]
    public void StaticClrPropertiesMayStartAPathButDoNotBypassDynamicSourcesOrFailedPrefixes()
    {
        string text = $"<Grid {Ns}><TextBlock Text='{{Binding (p:Settings.Current).Nmae}}'/></Grid>";
        Assert.Contains("Customer", Assert.Single(Analyze(text)).Message);
        Assert.Empty(Analyze(View("<TextBlock Text='{Binding (p:Settings.Current).Nmae}'/>", "DynamicModel")));
        string marked = View("<TextBlock Text='{Binding RuntimeValue.(p:Settings.Cu$$rrent)}'/>");
        int caret = marked.IndexOf("$$", StringComparison.Ordinal);
        Assert.Empty(Service.Complete(marked.Remove(caret, 2), caret, 1, Fixture.Value)!.Items);
    }

    [Fact]
    public void ParenthesizedSegmentsAreExplicitlyExcludedFromRenameWhileTypedContinuationRemains()
    {
        string text = View(Self("(p:Provider.Value).N&#97;me"));
        var result = Service.GetSymbolOccurrences(text, Fixture.Value);
        Assert.False(result.IsComplete);
        Assert.False(result.CoverageLimited);
        Assert.NotEmpty(result.Warnings);
        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal("Name", occurrence.Symbol.Name);
        Assert.Equal("Customer", occurrence.Symbol.ContainingType.Name);
        Assert.Equal("N&#97;me", text.Substring(occurrence.Start, occurrence.Length));

        var ordinary = Service.GetSymbolOccurrences(View("<TextBlock Text='{Binding (Selected).Name}'/>"), Fixture.Value);
        Assert.False(ordinary.IsComplete);
        Assert.Equal("Name", Assert.Single(ordinary.Occurrences).Symbol.Name);
    }

    private static CSharpCompilation CreateCompilation()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var source = CSharpSyntaxTree.ParseText("""
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows")]
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows.Controls")]
            namespace System.Windows.Markup
            {
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)]
                public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : System.Attribute { }
            }
            namespace System.Windows
            {
                public class DependencyObject { }
                public class DependencyProperty { }
                public class FrameworkElement : DependencyObject
                {
                    public object DataContext { get; set; }
                    public ResourceDictionary Resources { get; set; }
                }
                public class ResourceDictionary { }
                public class FrameworkTemplate { public ResourceDictionary Resources { get; set; } }
                public class DataTemplate : FrameworkTemplate { public object DataType { get; set; } }
            }
            namespace System.Windows.Controls
            {
                public class Grid : System.Windows.FrameworkElement
                {
                    public static readonly System.Windows.DependencyProperty RowProperty = new();
                    public static int GetRow(System.Windows.DependencyObject target) => 0;
                    public static void SetRow(System.Windows.DependencyObject target, int value) { }
                }
                public class TextBlock : System.Windows.FrameworkElement { public string Text { get; set; } }
                public class ItemsControl : System.Windows.FrameworkElement { public System.Collections.IEnumerable ItemsSource { get; set; } }
                public class ControlTemplate : System.Windows.FrameworkTemplate { public System.Type TargetType { get; set; } }
            }
            namespace AttachedFixture
            {
                public class Customer { public string Name { get; set; } }
                public class Model
                {
                    public Customer Selected { get; set; }
                    public System.Windows.FrameworkElement Target { get; set; }
                    public object RuntimeValue { get; set; }
                }
                public class DynamicModel : System.Dynamic.DynamicObject { }
                public static class Settings { public static Customer Current { get; } }
                public class Provider
                {
                    public static readonly System.Windows.DependencyProperty ValueProperty = new();
                    public static Customer GetValue(System.Windows.DependencyObject target) => null;
                    public static void SetValue(System.Windows.DependencyObject target, Customer value) { }
                    public static readonly System.Windows.DependencyProperty ItemsProperty = new();
                    public static System.Collections.Generic.List<Customer> GetItems(System.Windows.DependencyObject target) => null;
                    public static readonly System.Windows.DependencyProperty CountProperty = new();
                    public static int GetCount(System.Windows.DependencyObject target) => 0;
                    public static readonly System.Windows.DependencyProperty WriteOnlyProperty = new();
                    public static void SetWriteOnly(System.Windows.DependencyObject target, Customer value) { }
                    public static Customer GetNoRegistration(System.Windows.DependencyObject target) => null;
                    public static readonly System.Windows.DependencyProperty AmbiguousProperty = new();
                    public static Customer GetAmbiguous(System.Windows.DependencyObject target) => null;
                    public static Customer GetAmbiguous(System.Windows.FrameworkElement target) => null;
                }
                public class DerivedProvider : Provider { }
            }
            namespace OtherFixture
            {
                public class Order { public string Number { get; set; } }
                public class Provider
                {
                    public static readonly System.Windows.DependencyProperty ValueProperty = new();
                    public static Order GetValue(System.Windows.DependencyObject target) => null;
                }
            }
            """, path: "AttachedModels.cs");
        var compilation = CSharpCompilation.Create("AttachedFixture", [source], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
