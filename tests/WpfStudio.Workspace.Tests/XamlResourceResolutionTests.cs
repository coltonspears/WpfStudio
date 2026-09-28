using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlResourceResolutionTests
{
    private const string SourcePath = "C:/Project/Views/View.xaml";
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:vm=\"clr-namespace:Demo\"";
    private static readonly Lazy<CSharpCompilation> Fixture = new(CreateCompilation);
    private static readonly XamlLanguageService Service = new();
    internal static CSharpCompilation SemanticFixture => Fixture.Value;
    private static string Assembly => Fixture.Value.Assembly.Identity.GetDisplayName();
    private static string Library => Fixture.Value.SourceModule.ReferencedAssemblySymbols.Single(assembly => assembly.Name == "ResourcesLib").Identity.GetDisplayName();
    private static string Dictionary(string body) => $"<ResourceDictionary {Ns}>{body}</ResourceDictionary>";
    private static string Use(string path = "Nmae", string key = "Model") => $"<TextBlock Text=\"{{Binding Path={path}, Source={{StaticResource {key}}}}}\"/>";
    private static string View(string dictionary, string? body = null) => $"<Window {Ns} d:DataContext=\"{{d:DesignInstance vm:Customer}}\"><Window.Resources><ResourceDictionary>{dictionary}</ResourceDictionary></Window.Resources>{body ?? Use()}</Window>";
    private static string Merges(params string[] sources) => "<ResourceDictionary.MergedDictionaries>" + string.Concat(sources.Select(source => $"<ResourceDictionary Source=\"{source}\"/>")) + "</ResourceDictionary.MergedDictionaries>";
    private static XamlResourceDocument Doc(string logical, string? text, string? assembly = null, string kind = "Page", long? version = null) =>
        new("C:/Project/" + logical, text, assembly ?? Assembly, logical, kind, text is null ? "The dictionary is locked or missing." : null, version);
    private static XamlResourceContext Context(string text, params XamlResourceDocument[] documents) =>
        new(SourcePath, Assembly, new[] { new XamlResourceDocument(SourcePath, text, Assembly, "Views/View.xaml", "Page") }.Concat(documents).ToArray());
    private static IReadOnlyList<WorkspaceDiagnostic> Analyze(string text, XamlResourceContext? context = null) => Service.Analyze(SourcePath, text, 5, Fixture.Value, resources: context);
    private static void CustomerTypo(string text, XamlResourceContext? context = null)
    {
        var issue = Assert.Single(Analyze(text, context));
        Assert.Equal("Nmae", text.Substring(issue.Start, issue.Length));
        Assert.Contains("Customer", issue.Message);
        Assert.Contains("Name", issue.Message);
    }

    [Fact]
    public void ExternalDictionaryFeedsTheSameCompletionDiagnosticsHoverDefinitionAndFix()
    {
        var marked = View(Merges("../Models.xaml"), Use("Na$$me"));
        int caret = marked.IndexOf("$$", StringComparison.Ordinal);
        var text = marked.Remove(caret, 2);
        var external = Doc("Models.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>"), version: 23);
        var context = Context(text, external);
        var completion = Service.Complete(text, caret, 17, Fixture.Value, resources: context);
        Assert.NotNull(completion);
        Assert.Equal("Name", text.Substring(completion.Start, completion.Length));
        Assert.Contains(completion.Items, item => item.InsertText == "Name");
        Assert.Empty(Analyze(text, context));
        var definition = Assert.Single(Service.GetDefinition(SourcePath, text, caret, Fixture.Value, resources: context));
        Assert.Equal("Models.cs", definition.Path);
        Assert.Contains("Customer.Name", Service.GetHover(SourcePath, text, caret, Fixture.Value, resources: context)!.Text);
        var occurrences = Service.GetSymbolOccurrences(text, Fixture.Value, resources: context);
        Assert.True(occurrences.IsComplete);
        Assert.Equal("Demo.Customer", Assert.Single(occurrences.Occurrences).Symbol.ContainingType.ToDisplayString());

        var typo = text.Replace("Path=Name", "Path=&#78;mae", StringComparison.Ordinal);
        int position = typo.IndexOf("&#78;mae", StringComparison.Ordinal) + 2;
        var issue = Assert.Single(Analyze(typo, Context(typo, external)));
        Assert.Equal("&#78;mae", typo.Substring(issue.Start, issue.Length));
        var action = Assert.Single(Service.GetCodeActions(SourcePath, typo, position, 18, Fixture.Value, resources: Context(typo, external)));
        var edit = Assert.Single(action.Edit.Edits);
        Assert.Equal("Name", edit.NewText);
        var guard = Assert.Single(action.AdditionalEdits!);
        Assert.Equal(external.Path, guard.Path);
        Assert.Equal(23, guard.Version);
        Assert.Empty(guard.Edits);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(external.Text!))), guard.ExpectedTextHash);
    }

    [Fact]
    public void EachOperationUsesItsOwnImmutableDictionarySnapshot()
    {
        var text = View(Merges("../Models.xaml"), Use("Name"));
        var customer = Context(text, Doc("Models.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>")));
        var order = Context(text, Doc("Models.xaml", Dictionary("<vm:Order x:Key=\"Model\"/>")));
        Assert.Empty(Analyze(text, customer));
        Assert.Contains("Order", Assert.Single(Analyze(text, order)).Message);
        Assert.Empty(Analyze(text, customer));
    }

    [Fact]
    public void ExternalResourceDataContextTypesUnqualifiedChildBindings()
    {
        var text = View(Merges("../Models.xaml"), "<TextBlock DataContext=\"{StaticResource Model}\" Text=\"{Binding Titl}\"/>");
        var context = Context(text, Doc("Models.xaml", Dictionary("<vm:Order x:Key=\"Model\"/>")));
        var issue = Assert.Single(Analyze(text, context));
        Assert.Contains("Order", issue.Message);
        Assert.Contains("Title", issue.Message);
    }

    [Fact]
    public void SharedAcyclicMergeBranchesAreNotMisreportedAsCycles()
    {
        var text = View(Merges("../Known.xaml", "../A.xaml", "../B.xaml"));
        var context = Context(text, Doc("Known.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>")),
            Doc("A.xaml", Dictionary(Merges("Empty.xaml"))), Doc("B.xaml", Dictionary(Merges("Empty.xaml"))), Doc("Empty.xaml", Dictionary("")));
        CustomerTypo(text, context);
    }

    [Fact]
    public void DirectKeysWinAndMergedKeysAreSearchedLastToFirst()
    {
        var customer = Doc("Customer.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>"));
        var order = Doc("Order.xaml", Dictionary("<vm:Order x:Key=\"Model\"/>"));
        var text = View(Merges("../Order.xaml", "../Customer.xaml"));
        CustomerTypo(text, Context(text, customer, order));
        var reversed = View(Merges("../Customer.xaml", "../Order.xaml"), Use("Title"));
        Assert.Empty(Analyze(reversed, Context(reversed, customer, order)));
        var direct = View(Merges("../Missing.xaml") + "<vm:Customer x:Key=\"Model\"/>");
        CustomerTypo(direct, Context(direct, Doc("Missing.xaml", null)));
    }

    [Fact]
    public void UnavailableHigherPrecedenceDictionaryBlocksEarlierAndInheritedGuesses()
    {
        var text = View(Merges("../Known.xaml", "../Missing.xaml"));
        var context = Context(text, Doc("Known.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>")), Doc("Missing.xaml", null));
        Assert.Empty(Analyze(text, context));
        var hover = Service.GetHover(SourcePath, text, text.IndexOf("Nmae", StringComparison.Ordinal), Fixture.Value, resources: context);
        Assert.Contains("locked", hover!.Text);
        var occurrences = Service.GetSymbolOccurrences(text, Fixture.Value, resources: context);
        Assert.False(occurrences.IsComplete);
        Assert.Empty(occurrences.Occurrences);
    }

    [Fact]
    public void EmptyHigherPrecedenceDictionariesAreIncludedInFixGuards()
    {
        var text = View(Merges("../Known.xaml", "../Empty.xaml"));
        var known = Doc("Known.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>"));
        var empty = Doc("Empty.xaml", Dictionary("<vm:Order x:Key=\"Other\"/>"), version: 42);
        var unrelated = Doc("Unrelated.xaml", null);
        var action = Assert.Single(Service.GetCodeActions(SourcePath, text, text.IndexOf("Nmae", StringComparison.Ordinal), 4, Fixture.Value,
            resources: Context(text, known, empty, unrelated)));
        Assert.Equal(new[] { empty.Path, known.Path }.Order(), action.AdditionalEdits!.Select(edit => edit.Path).Order());
        Assert.Equal(42, action.AdditionalEdits!.Single(edit => edit.Path == empty.Path).Version);
    }

    [Fact]
    public void UnrelatedUnavailableInventoryDoesNotDisableAnEntirelyLocalFix()
    {
        var text = View("<vm:Customer x:Key=\"Model\"/>");
        var original = Context(text, Doc("Unrelated.xaml", null));
        var context = new XamlResourceContext(SourcePath, Assembly, original.Documents, IsComplete: false);
        CustomerTypo(text, context);
        var action = Assert.Single(Service.GetCodeActions(SourcePath, text, text.IndexOf("Nmae", StringComparison.Ordinal), 4, Fixture.Value, resources: context));
        Assert.Null(action.AdditionalEdits);
        var external = View(Merges("../Known.xaml"));
        var incomplete = new XamlResourceContext(SourcePath, Assembly, Context(external, Doc("Known.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>"))).Documents, IsComplete: false);
        Assert.Empty(Analyze(external, incomplete));
        Assert.True(Service.GetSymbolOccurrences(external, Fixture.Value, resources: incomplete).CoverageLimited);
    }

    [Theory]
    [InlineData("../Linked/Models.xaml")]
    [InlineData("/Linked/Models.xaml")]
    [InlineData("pack://application:,,,/Linked/Models.xaml")]
    [InlineData("../linked/models.xaml")]
    public void EvaluatedLogicalPathRatherThanPhysicalDirectoryResolvesLinkedResources(string uri)
    {
        var text = View(Merges(uri));
        var linked = new XamlResourceDocument("C:/Shared/NotTheResourcePath.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>"), Assembly, "Linked/Models.xaml", "Resource");
        CustomerTypo(text, Context(text, linked));
    }

    [Fact]
    public void NestedRelativeSourcesRemainInTheirDeclaringDictionaryDirectory()
    {
        var text = View(Merges("../Theme/First.xaml"));
        var first = Doc("Theme/First.xaml", Dictionary(Merges("Data/Models.xaml")));
        var nested = Doc("Theme/Data/Models.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>"));
        CustomerTypo(text, Context(text, first, nested));
    }

    [Fact]
    public void DictionaryNamespaceRedeclarationsDoNotUseTheConsumersPrefixMapping()
    {
        var text = View(Merges("../Models.xaml"), Use("Title"));
        var dictionary = Dictionary("<vm:Order xmlns:vm=\"clr-namespace:Demo\" x:Key=\"Model\"/>")
            .Replace("xmlns:vm=\"clr-namespace:Demo\"", "xmlns:vm=\"clr-namespace:Unavailable\"", StringComparison.Ordinal);
        // Redeclare only the inner object, leaving the dictionary's prefix different.
        dictionary = dictionary.Replace("<vm:Order xmlns:vm=\"clr-namespace:Unavailable\"", "<vm:Order xmlns:vm=\"clr-namespace:Demo\"", StringComparison.Ordinal);
        var context = Context(text, Doc("Models.xaml", dictionary));
        Assert.Empty(Analyze(text, context));
        var occurrence = Assert.Single(Service.GetSymbolOccurrences(text, Fixture.Value, resources: context).Occurrences);
        Assert.Equal("Order", occurrence.Symbol.ContainingType.Name);
    }

    [Theory]
    [InlineData("https://example.test/Models.xaml")]
    [InlineData("//server/Models.xaml")]
    [InlineData("pack://siteoforigin:,,,/Models.xaml")]
    [InlineData("/ResourcesLib;v1.0.0.0;component/Models.xaml")]
    public void UnsupportedUriKindsDoNotFallBackToAnUnrelatedEvaluatedPath(string uri)
    {
        var text = View(Merges(uri));
        Assert.Empty(Analyze(text, Context(text, Doc("Models.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>")))));
    }

    [Fact]
    public void ReferencedAssemblyUsesItsOwnUnqualifiedClrNamespaceAndNestedRelativeUris()
    {
        var text = View(Merges("/ResourcesLib;component/Themes/First.xaml"), Use("Code"));
        var first = Doc("Themes/First.xaml", Dictionary(Merges("Models.xaml")), Library);
        var model = Doc("Themes/Models.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>"), Library);
        var context = Context(text, first, model);
        Assert.Empty(Analyze(text, context));
        var occurrence = Assert.Single(Service.GetSymbolOccurrences(text, Fixture.Value, resources: context).Occurrences);
        Assert.Equal("ResourcesLib", occurrence.Symbol.ContainingAssembly.Name);
        var wrong = text.Replace("Path=Code", "Path=Name", StringComparison.Ordinal);
        Assert.Contains("Customer", Assert.Single(Analyze(wrong, Context(wrong, first, model))).Message);
    }

    [Fact]
    public void ApplicationResourcesAreAnExplicitFallbackAndLocalUnknownsShadowThem()
    {
        var app = Doc("App.xaml", $"<Application {Ns}><Application.Resources><vm:Customer x:Key=\"Model\"/></Application.Resources></Application>", kind: "ApplicationDefinition");
        var text = View("");
        Assert.Empty(Analyze(text, Context(text, app)));
        var context = Context(text, app);
        CustomerTypo(text, new(SourcePath, Assembly, context.Documents, app.Path));
        CustomerTypo(text, new(SourcePath, Assembly, context.Documents, app.Path, IsComplete: false));
        var blocked = View(Merges("../Missing.xaml"));
        var blockedContext = Context(blocked, app, Doc("Missing.xaml", null));
        Assert.Empty(Analyze(blocked, new(SourcePath, Assembly, blockedContext.Documents, app.Path)));
    }

    [Theory]
    [InlineData("<vm:Customer x:Key=\"Model\"/><vm:Order x:Key=\"Model\"/>")]
    [InlineData("<vm:Customer x:Key=\"Model\"/><vm:Order x:Key=\"{x:Static vm:Keys.Model}\"/>")]
    [InlineData("<vm:Customer x:Key=\"Model\" x:FactoryMethod=\"Create\"/>")]
    [InlineData("<vm:Provider x:Key=\"Model\"/>")]
    [InlineData("<vm:RuntimeExtension x:Key=\"Model\"/>")]
    public void AmbiguousOrRuntimeProvidedResourceValuesDoNotBorrowOuterDataContext(string entries)
    {
        var text = View(entries);
        Assert.Empty(Analyze(text, Context(text)));
        Assert.False(Service.GetSymbolOccurrences(text, Fixture.Value, resources: Context(text)).IsComplete);
    }

    [Fact]
    public void ObjectStaticResourceAliasesPreserveOrderingAndBindingSourceObjectSupport()
    {
        var entries = "<vm:Customer x:Key=\"Original\"/><StaticResource x:Key=\"Model\" ResourceKey=\"Original\"/>";
        var text = View(entries, "<TextBlock><TextBlock.Text><Binding Path=\"Nmae\"><Binding.Source><StaticResource ResourceKey=\"Model\"/></Binding.Source></Binding></TextBlock.Text></TextBlock>");
        CustomerTypo(text, Context(text));
        var forward = View("<StaticResource x:Key=\"Model\" ResourceKey=\"Original\"/><vm:Customer x:Key=\"Original\"/>");
        Assert.Empty(Analyze(forward, Context(forward)));
        Assert.Contains("forward", Service.GetHover(SourcePath, forward, forward.IndexOf("Nmae", StringComparison.Ordinal), Fixture.Value, resources: Context(forward))!.Text);
    }

    [Fact]
    public void LiteralQuotesInObjectKeysAreNotMarkupArgumentDelimiters()
    {
        var text = View("<vm:Order x:Key=\"'Model'\"/><vm:Customer x:Key=\"Model\"/>",
            "<TextBlock><TextBlock.Text><Binding Path=\"Title\"><Binding.Source><StaticResource ResourceKey=\"'Model'\"/></Binding.Source></Binding></TextBlock.Text></TextBlock>");
        Assert.Empty(Analyze(text, Context(text)));
        var occurrence = Assert.Single(Service.GetSymbolOccurrences(text, Fixture.Value, resources: Context(text)).Occurrences);
        Assert.Equal("Order", occurrence.Symbol.ContainingType.Name);
        var quotedArgument = View("<vm:Customer x:Key=\"Model\"/>", Use(key: "'Model'"));
        CustomerTypo(quotedArgument, Context(quotedArgument));
    }

    [Fact]
    public void KnownBasedOnResourcesPrecedeContainingDictionaryResources()
    {
        var text = View("""
            <vm:Order x:Key="Model"/>
            <Style x:Key="Base" TargetType="TextBlock"><Style.Resources><vm:Customer x:Key="Model"/></Style.Resources></Style>
            <Style x:Key="Derived" TargetType="TextBlock" BasedOn="{StaticResource Base}">
                <Setter Property="Text" Value="{Binding Nmae, Source={StaticResource Model}}"/>
            </Style>
            """, "");
        CustomerTypo(text, Context(text));
        var unavailable = text.Replace("BasedOn=\"{StaticResource Base}\"", "BasedOn=\"{DynamicResource Base}\"", StringComparison.Ordinal);
        Assert.Empty(Analyze(unavailable, Context(unavailable)));
    }

    [Fact]
    public void BaseStyleResourcesAttributeBlocksAnUnrelatedContainingDictionaryMatch()
    {
        var text = View("""
            <vm:Order x:Key="Model"/>
            <ResourceDictionary x:Key="BaseResources"><vm:Customer x:Key="Model"/></ResourceDictionary>
            <Style x:Key="Base" Resources="{StaticResource BaseResources}"/>
            <Style x:Key="Derived" BasedOn="{StaticResource Base}">
                <Setter Property="Text" Value="{Binding Name, Source={StaticResource Model}}"/>
            </Style>
            """, "");
        Assert.Empty(Analyze(text, Context(text)));
        var occurrences = Service.GetSymbolOccurrences(text, Fixture.Value, resources: Context(text));
        Assert.False(occurrences.IsComplete);
        Assert.Empty(occurrences.Occurrences);
    }

    [Theory]
    [InlineData("<ResourceDictionary Source=\"../Missing.xaml\"><vm:Customer x:Key=\"Model\"/></ResourceDictionary>")]
    [InlineData("<ResourceDictionary xml:base=\"../Elsewhere/\"><vm:Customer x:Key=\"Model\"/></ResourceDictionary>")]
    public void MixedSourceAndExplicitBaseAreConservativeBoundaries(string dictionary)
    {
        var text = $"<Window {Ns} d:DataContext=\"{{d:DesignInstance vm:Customer}}\"><Window.Resources>{dictionary}</Window.Resources>{Use()}</Window>";
        Assert.Empty(Analyze(text, Context(text)));
    }

    [Fact]
    public void MissingMalformedDuplicatedAndCyclicSourcesRemainExplicitlyUnknown()
    {
        var text = View(Merges("../Models.xaml"));
        var valid = Doc("Models.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>"));
        foreach (var documents in new[]
        {
            new[] { Doc("Models.xaml", null) },
            new[] { Doc("Models.xaml", "<ResourceDictionary>") },
            new[] { valid, valid with { Path = "C:/Another/Models.xaml" } },
            new[] { Doc("Models.xaml", Dictionary(Merges("Other.xaml"))), Doc("Other.xaml", Dictionary(Merges("Models.xaml"))) }
        })
        {
            var context = Context(text, documents);
            Assert.Empty(Analyze(text, context));
            Assert.False(Service.GetSymbolOccurrences(text, Fixture.Value, resources: context).IsComplete);
        }
    }

    [Theory]
    [InlineData("<ResourceDictionary xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:vm=\"clr-namespace:Demo\"><vm:Customer x:Key=\"Model\"/></ResourceDictionary>")]
    [InlineData("<ResourceDictionary xmlns=\"clr-namespace:Fake\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:vm=\"clr-namespace:Demo\"><vm:Customer x:Key=\"Model\"/></ResourceDictionary>")]
    public void ImportedRootMustResolveToTheFrameworkDictionaryType(string dictionary)
    {
        var text = View(Merges("../Models.xaml"));
        var compilation = Fixture.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Fake; public class ResourceDictionary { }"));
        var context = Context(text, Doc("Models.xaml", dictionary));
        Assert.Empty(Service.Analyze(SourcePath, text, 1, compilation, resources: context));
        Assert.False(Service.GetSymbolOccurrences(text, compilation, resources: context).IsComplete);
    }

    [Fact]
    public void AmbiguousPresentationNamespaceDoesNotChooseAFrameworkDictionaryBySpelling()
    {
        var text = View(Merges("../Models.xaml"));
        var compilation = Fixture.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "Fake")]
            namespace Fake { public class ResourceDictionary { } }
            """));
        var context = Context(text, Doc("Models.xaml", Dictionary("<vm:Customer x:Key=\"Model\"/>")));
        Assert.Empty(Service.Analyze(SourcePath, text, 1, compilation, resources: context));
    }

    [Fact]
    public void SameMetadataNameInAnUnrelatedAssemblyIsNotTheFrameworkDictionaryType()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        static PortableExecutableReference Emit(CSharpCompilation compilation)
        {
            using var stream = new MemoryStream();
            Assert.True(compilation.Emit(stream).Success);
            return MetadataReference.CreateFromImage(stream.ToArray());
        }
        var framework = Emit(CSharpCompilation.Create("PresentationFramework", [Fixture.Value.SyntaxTrees.Single(tree => tree.FilePath == "Framework.cs")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        var pretender = Emit(CSharpCompilation.Create("Pretender", [CSharpSyntaxTree.ParseText("namespace System.Windows; public class ResourceDictionary { }")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        var compilation = CSharpCompilation.Create("ResourceIdentityApp", [Fixture.Value.SyntaxTrees.Single(tree => tree.FilePath == "Models.cs")],
            references.Concat(new[] { framework, pretender }), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var text = View(Merges("../Models.xaml"));
        var assembly = compilation.Assembly.Identity.GetDisplayName();
        XamlResourceContext ResourceContext(string dictionary) => new(SourcePath, assembly,
            [new(SourcePath, text, assembly, "Views/View.xaml", "Page"), new("C:/Project/Models.xaml", dictionary, assembly, "Models.xaml", "Page")]);
        var invalid = $"<fake:ResourceDictionary {Ns} xmlns:fake=\"clr-namespace:System.Windows;assembly=Pretender\"><vm:Customer x:Key=\"Model\"/></fake:ResourceDictionary>";
        Assert.Empty(Service.Analyze(SourcePath, text, 1, compilation, resources: ResourceContext(invalid)));
        Assert.Single(Service.Analyze(SourcePath, text, 1, compilation, resources: ResourceContext(Dictionary("<vm:Customer x:Key=\"Model\"/>"))));
    }

    [Fact]
    public void FrameworkClrAliasesAreValidButUnqualifiedMergedObjectsAreUnknown()
    {
        var text = View(Merges("../Models.xaml"));
        var dictionary = $"<w:ResourceDictionary {Ns} xmlns:w=\"clr-namespace:System.Windows;assembly=ResourceApp\"><vm:Customer x:Key=\"Model\"/></w:ResourceDictionary>";
        CustomerTypo(text, Context(text, Doc("Models.xaml", dictionary)));
        var badMerge = Dictionary("<ResourceDictionary.MergedDictionaries><ResourceDictionary xmlns=\"\"><vm:Customer x:Key=\"Model\"/></ResourceDictionary></ResourceDictionary.MergedDictionaries>");
        Assert.Empty(Analyze(text, Context(text, Doc("Models.xaml", badMerge))));
    }

    [Fact]
    public void ResourceTextAndDepthBudgetsProduceLimitedReferenceCoverage()
    {
        var text = View(Merges("../Models.xaml"));
        var oversized = Context(text, Doc("Models.xaml", new string(' ', 1_000_001)));
        Assert.Empty(Analyze(text, oversized));
        Assert.True(Service.GetSymbolOccurrences(text, Fixture.Value, resources: oversized).CoverageLimited);
        var documents = Enumerable.Range(0, 70).Select(index => Doc(index == 0 ? "Models.xaml" : $"{index}.xaml",
            Dictionary(index == 69 ? "<vm:Customer x:Key=\"Model\"/>" : Merges($"{index + 1}.xaml")))).ToArray();
        Assert.True(Service.GetSymbolOccurrences(text, Fixture.Value, resources: Context(text, documents)).CoverageLimited);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Service.Analyze(SourcePath, text, 1, Fixture.Value, cancellation.Token, Context(text, documents)));
    }

    private static CSharpCompilation CreateCompilation()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var library = CSharpCompilation.Create("ResourcesLib", [CSharpSyntaxTree.ParseText("namespace Demo; public class Customer { public string Code { get; set; } }")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        Assert.True(library.Emit(output).Success);
        var framework = CSharpSyntaxTree.ParseText("""
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows")]
            [assembly: System.Windows.Markup.XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "System.Windows.Controls")]
            namespace System.Windows.Markup
            {
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)]
                public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : System.Attribute { }
                public class MarkupExtension { }
            }
            namespace System.Windows
            {
                public class ResourceDictionary { }
                public class FrameworkElement { public object DataContext { get; set; } public ResourceDictionary Resources { get; } }
                public class Window : FrameworkElement { }
                public class Application { public ResourceDictionary Resources { get; } }
                public class Style { public ResourceDictionary Resources { get; } }
                public class Setter { public string Property { get; set; } public object Value { get; set; } }
            }
            namespace System.Windows.Controls { public class TextBlock : System.Windows.FrameworkElement { public string Text { get; set; } } }
            namespace System.Windows.Data { public class DataSourceProvider { } }
            """, path: "Framework.cs");
        var model = CSharpSyntaxTree.ParseText("""
            namespace Demo;
            public class Customer { public string Name { get; set; } }
            public class Order { public string Title { get; set; } }
            public class Provider : System.Windows.Data.DataSourceProvider { public string Name { get; set; } }
            public class RuntimeExtension : System.Windows.Markup.MarkupExtension { public string Name { get; set; } }
            """, path: "Models.cs");
        var compilation = CSharpCompilation.Create("ResourceApp", [framework, model], references.Append(MetadataReference.CreateFromImage(output.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
