using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Workspace.Xaml;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlDesignDataContextTests
{
    private const string Namespaces = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:lang='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:design='http://schemas.microsoft.com/expression/blend/2008' xmlns:vm='clr-namespace:Demo'";
    private static readonly Lazy<CSharpCompilation> Compilation = new(CreateCompilation);
    private static readonly XamlLanguageService Service = new();
    private static string Document(string body) => $"<Window {Namespaces} design:DataContext='{{design:DesignInstance vm:Parent}}'>{body}</Window>";
    private static IReadOnlyList<WorkspaceDiagnostic> Analyze(string text) => Service.Analyze("View.xaml", text, 1, Compilation.Value);

    [Theory]
    [InlineData("Grid.DataContext")]
    [InlineData("FrameworkElement.DataContext")]
    public void DesignObjectPropertyOverridesBothRuntimeAndAncestorContexts(string property)
    {
        string text = Document($"<Grid><Grid.DataContext><vm:Other/></Grid.DataContext><design:{property}><vm:Customer/></design:{property}><TextBlock Text='{{Binding Name}}'/><TextBlock Text='{{Binding Nmae}}'/></Grid><TextBlock Text='{{Binding Title}}'/>");
        var diagnostic = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(diagnostic.Start, diagnostic.Length));
        Assert.Contains("Customer", diagnostic.Message);
        Assert.Contains("Name", diagnostic.Message);
    }

    [Fact]
    public void DesignObjectContextDrivesCompletionAndSourceNavigation()
    {
        string text = Document("<Grid><design:Grid.DataContext><vm:Customer/></design:Grid.DataContext><TextBlock Text='{Binding Na}'/></Grid>");
        var completion = Service.Complete(text, text.IndexOf("Na}", StringComparison.Ordinal) + 2, 1, Compilation.Value);
        Assert.NotNull(completion);
        Assert.Contains(completion.Items, item => item.DisplayText == "Name");
        Assert.DoesNotContain(completion.Items, item => item.DisplayText == "Title");
        text = text.Replace("Na}", "Name}", StringComparison.Ordinal);
        var location = Assert.Single(Service.GetDefinition("View.xaml", text, text.IndexOf("Name}", StringComparison.Ordinal) + 1, Compilation.Value));
        Assert.Equal("Models.cs", location.Path);
    }

    [Theory]
    [InlineData("<design:Grid.DataContext/>")]
    [InlineData("<design:Grid.DataContext><vm:Customer/><vm:Other/></design:Grid.DataContext>")]
    [InlineData("<design:Grid.DataContext>mixed<vm:Customer/></design:Grid.DataContext>")]
    [InlineData("<design:Grid.DataContext><![CDATA[value]]><vm:Customer/></design:Grid.DataContext>")]
    [InlineData("<design:Grid.DataContext><Binding Path='Selected'/></design:Grid.DataContext>")]
    [InlineData("<design:Grid.DataContext><design:DesignInstance Type='vm:Customer'/></design:Grid.DataContext>")]
    [InlineData("<design:Grid.DataContext><lang:Null/></design:Grid.DataContext>")]
    [InlineData("<design:Grid.DataContext><vm:Unknown/></design:Grid.DataContext>")]
    [InlineData("<design:NotTheOwner.DataContext><vm:Customer/></design:NotTheOwner.DataContext>")]
    [InlineData("<design:DataContext><vm:Customer/></design:DataContext>")]
    [InlineData("<design:Grid.DataContext><vm:Customer/></design:Grid.DataContext><design:FrameworkElement.DataContext><vm:Other/></design:FrameworkElement.DataContext>")]
    public void UnsupportedLocalDesignValuesDoNotBorrowRuntimeOrInheritedTypes(string declaration)
    {
        string text = Document($"<Grid DataContext='runtime string'>{declaration}<TextBlock Text='{{Binding RuntimeOnly}}'/></Grid>");
        Assert.Empty(Analyze(text));
        var completion = Service.Complete(text, text.IndexOf("RuntimeOnly}", StringComparison.Ordinal) + 11, 1, Compilation.Value);
        Assert.NotNull(completion);
        Assert.Empty(completion.Items);
    }

    [Fact]
    public void ConflictingDesignAttributeAndPropertyAreUnknown()
    {
        string text = Document("<Grid design:DataContext='{design:DesignInstance vm:Parent}'><design:Grid.DataContext><vm:Customer/></design:Grid.DataContext><TextBlock Text='{Binding RuntimeOnly}'/></Grid>");
        Assert.Empty(Analyze(text));
    }

    [Theory]
    [InlineData("<Grid><design:Grid.DataContext><Binding Path='Title'/></design:Grid.DataContext></Grid>")]
    [InlineData("<TextBlock design:Text='{Binding Title}'/>")]
    [InlineData("<design:Grid><TextBlock Text='{Binding Title}'/></design:Grid>")]
    [InlineData("<extra:Grid xmlns:extra='urn:ignored' xmlns:compat='http://schemas.openxmlformats.org/markup-compatibility/2006' compat:Ignorable='extra'><TextBlock Text='{Binding Title}'/></extra:Grid>")]
    public void IgnoredBindingsDoNotInventAncestorDiagnosticsCompletionOrNavigation(string body)
    {
        string text = Document(body + "<TextBlock Text='{Binding Ttile}'/>");
        int position = text.IndexOf("Title", StringComparison.Ordinal) + 3;
        var completion = Service.Complete(text, position, 1, Compilation.Value);
        Assert.NotNull(completion);
        Assert.Empty(completion.Items);
        Assert.Null(Service.GetHover("View.xaml", text, position, Compilation.Value));
        Assert.Empty(Service.GetDefinition("View.xaml", text, position, Compilation.Value));
        Assert.Empty(Service.GetCodeActions("View.xaml", text, position, 1, Compilation.Value));
        var runtime = Assert.Single(Analyze(text));
        Assert.Equal("Ttile", text.Substring(runtime.Start, runtime.Length));
        var misspelledDesign = text.Replace("Title", "RuntimeOnly", StringComparison.Ordinal);
        Assert.Single(Analyze(misspelledDesign));
    }

    [Fact]
    public void UnrelatedNamespaceDoesNotReplaceInheritedContext()
    {
        string text = Document("<Grid xmlns:d='urn:other'><d:Grid.DataContext><vm:Customer/></d:Grid.DataContext><TextBlock Text='{Binding Name}'/></Grid>");
        var diagnostic = Assert.Single(Analyze(text));
        Assert.Contains("Parent", diagnostic.Message);
    }

    [Fact]
    public void DesignListHasCollectionMembersAndDeclaredItemMembers()
    {
        string text = Document("<Grid design:DataContext='{design:DesignInstance Type={lang:Type vm:Customer}, CreateList=True}'><TextBlock Text='{Binding Count}'/><TextBlock Text='{Binding [0].Name}'/><TextBlock Text='{Binding /Name}'/><TextBlock Text='{Binding Name}'/><TextBlock Text='{Binding [0].Nmae}'/></Grid>");
        var diagnostic = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(diagnostic.Start, diagnostic.Length));
        Assert.Contains("Customer", diagnostic.Message);
        var completion = Service.Complete(text, text.IndexOf("Count}", StringComparison.Ordinal) + 2, 1, Compilation.Value);
        Assert.NotNull(completion);
        Assert.Contains(completion.Items, item => item.DisplayText == "Count");
    }

    [Fact]
    public void DesignListItemsSourceProvidesInlineTemplateItemType()
    {
        string text = Document("<ItemsControl design:DataContext='{design:DesignInstance vm:Customer, CreateList=True}' ItemsSource='{Binding}'><ItemsControl.ItemTemplate><DataTemplate><TextBlock Text='{Binding Name}'/><TextBlock Text='{Binding Nmae}'/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>");
        var diagnostic = Assert.Single(Analyze(text));
        Assert.Equal("Nmae", text.Substring(diagnostic.Start, diagnostic.Length));
        Assert.Contains("Customer", diagnostic.Message);
    }

    [Fact]
    public void ExplicitFalseKeepsTheDeclaredObjectShape()
    {
        string text = Document("<Grid design:DataContext='{design:DesignInstance vm:Customer, CreateList=False, IsDesignTimeCreatable=True}'><TextBlock Text='{Binding Name}'/><TextBlock Text='{Binding Count}'/></Grid>");
        var diagnostic = Assert.Single(Analyze(text));
        Assert.Equal("Count", text.Substring(diagnostic.Start, diagnostic.Length));
        Assert.Contains("Customer", diagnostic.Message);
    }

    [Theory]
    [InlineData("vm:Customer, CreateList=Maybe")]
    [InlineData("vm:Customer, CreateList={Binding Flag}")]
    [InlineData("vm:Customer, CreateList=True, CreateList=False")]
    [InlineData("vm:Customer, Type=vm:Parent")]
    [InlineData("vm:Customer, IsDesignTimeCreatable=Maybe")]
    [InlineData("vm:Customer, UnknownOption=True")]
    [InlineData("CreateList=True")]
    public void InvalidDesignInstanceOptionsAreUnknownBoundaries(string options)
    {
        Assert.Empty(Analyze(Document($"<Grid design:DataContext='{{design:DesignInstance {options}}}'><TextBlock Text='{{Binding RuntimeOnly}}'/></Grid>")));
    }

    private static CSharpCompilation CreateCompilation()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var source = CSharpSyntaxTree.ParseText("""
            [assembly:System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows")]
            namespace System.Windows.Markup {
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)]
                public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : System.Attribute { }
            }
            namespace System.Windows {
                public class FrameworkElement { public object DataContext { get; set; } }
                public class Window : FrameworkElement { }
                public class Grid : FrameworkElement { }
                public class TextBlock : FrameworkElement { public string Text { get; set; } }
                public class ItemsControl : FrameworkElement { public System.Collections.IEnumerable ItemsSource { get; set; } }
            }
            namespace Demo {
                public class Parent { public string Title { get; set; } }
                public class Other { public string OtherName { get; set; } }
                public class Customer { public string Name { get; set; } }
            }
            """, path: "Models.cs");
        return CSharpCompilation.Create("DesignDataFixture", [source], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
