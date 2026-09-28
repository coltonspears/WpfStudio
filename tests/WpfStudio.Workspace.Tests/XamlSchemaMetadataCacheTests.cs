using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlSchemaMetadataCacheTests
{
    private const string Uri = "urn:cache-fixture";

    [Fact]
    public void UnsavedMappingChangesAndGeneratedMappingsUseTheirOwnCompilationMetadata()
    {
        var original = CreateCompilation("First");
        var originalResolver = new SchemaTypeResolver(original);
        Assert.Equal("First.Widget", originalResolver.Resolve(Uri, "Widget")!.ToDisplayString());
        var mapping = original.SyntaxTrees.Single(tree => tree.FilePath == "Mapping.cs");
        var changed = original.ReplaceSyntaxTree(mapping, Mapping("Second"));
        Assert.Equal(original.Assembly.Identity, changed.Assembly.Identity);
        Assert.Equal("Second.Widget", new SchemaTypeResolver(changed).Resolve(Uri, "Widget")!.ToDisplayString());
        Assert.Equal("First.Widget", new SchemaTypeResolver(original).Resolve(Uri, "Widget")!.ToDisplayString());

        // Generator output can introduce a second legal namespace mapping. Neither
        // the existing URI index nor a previously successful type may leak into it.
        var generated = original.AddSyntaxTrees(Mapping("Second", "GeneratedMapping.g.cs"));
        Assert.Null(new SchemaTypeResolver(generated).Resolve(Uri, "Widget"));
        Assert.Equal(new[] { "First.Widget", "Second.Widget" }, new SchemaTypeResolver(generated).Types(Uri).Select(type => type.ToDisplayString()).Order());
        Assert.Equal("First.Widget", originalResolver.Resolve(Uri, "Widget")!.ToDisplayString());
    }

    [Fact]
    public void CanceledConstructionAndCanceledLookupsDoNotPoisonOtherRequests()
    {
        var compilation = CreateCompilation("First");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SchemaTypeResolver(compilation, canceled.Token));
        Assert.Equal("First.Widget", new SchemaTypeResolver(compilation).Resolve(Uri, "Widget")!.ToDisplayString());
        // A cache hit must still respect the new caller's cancellation.
        Assert.Throws<OperationCanceledException>(() => new SchemaTypeResolver(compilation, canceled.Token));

        using var later = new CancellationTokenSource();
        var request = new SchemaTypeResolver(compilation, later.Token);
        later.Cancel();
        Assert.Throws<OperationCanceledException>(() => request.Resolve(Uri, "Widget"));
        Assert.Equal("First.Widget", new SchemaTypeResolver(compilation).Resolve(Uri, "Widget")!.ToDisplayString());
    }

    [Fact]
    public async Task ConcurrentColdAndWarmCallersKeepTokensAndLookupStateIndependent()
    {
        var compilation = CreateCompilation("First");
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(number => Task.Run(() =>
        {
            using var cancellation = new CancellationTokenSource();
            var resolver = new SchemaTypeResolver(compilation, cancellation.Token);
            var first = resolver.Resolve(Uri, "Widget");
            Assert.Equal("First.Widget", first!.ToDisplayString());
            Assert.Single(resolver.Types(Uri));
            if (number % 2 == 0)
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => resolver.Resolve(Uri, "Widget"));
            }
            var independent = new SchemaTypeResolver(compilation);
            Assert.True(SymbolEqualityComparer.Default.Equals(first, independent.Resolve(Uri, "Widget")));
            return independent.IsKnownNamespace(Uri);
        })));
        Assert.All(results, result => Assert.True(result));
    }

    [Fact]
    public void ANewReferencedAssemblySnapshotDoesNotReuseOldNamespaceMetadata()
    {
        var first = CreateCompilation("First").WithAssemblyName("ReferencedMappings");
        var second = CreateCompilation("Second").WithAssemblyName("ReferencedMappings");
        static PortableExecutableReference Emit(CSharpCompilation compilation)
        {
            using var output = new MemoryStream();
            Assert.True(compilation.Emit(output).Success);
            return MetadataReference.CreateFromImage(output.ToArray());
        }
        var firstReference = Emit(first);
        var secondReference = Emit(second);
        var consumer = CSharpCompilation.Create("Consumer", references: References().Append(firstReference),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Equal("First.Widget", new SchemaTypeResolver(consumer).Resolve(Uri, "Widget")!.ToDisplayString());
        var changed = consumer.ReplaceReference(firstReference, secondReference);
        Assert.Equal("Second.Widget", new SchemaTypeResolver(changed).Resolve(Uri, "Widget")!.ToDisplayString());
        Assert.Equal("First.Widget", new SchemaTypeResolver(consumer).Resolve(Uri, "Widget")!.ToDisplayString());
    }

    private static CSharpCompilation CreateCompilation(string target) => CSharpCompilation.Create("CacheFixture",
        [Mapping(target), CSharpSyntaxTree.ParseText("""
            namespace System.Windows.Markup
            {
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)]
                public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : System.Attribute
                { public string AssemblyName { get; set; } }
            }
            namespace First { public class Widget { public string Name { get; set; } } }
            namespace Second { public class Widget { public string Title { get; set; } } }
            """, path: "Widgets.cs")], References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    private static SyntaxTree Mapping(string target, string path = "Mapping.cs") => CSharpSyntaxTree.ParseText(
        $"[assembly: System.Windows.Markup.XmlnsDefinition(\"{Uri}\", \"{target}\")]", path: path);
    private static IEnumerable<MetadataReference> References() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path));
}
