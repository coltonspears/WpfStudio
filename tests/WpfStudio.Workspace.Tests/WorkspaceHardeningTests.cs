using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace.Tests;

public sealed class WorkspaceHardeningTests
{
    private static readonly IReadOnlyList<MetadataReference> PlatformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    [Fact]
    public void ParseExtensionHandlesBackslashEscapedQuotesAndBraces()
    {
        var quoted = XamlSyntax.ParseExtension(@"{Binding StringFormat='It\'s \{0:N2\}', Path=Value}");
        Assert.NotNull(quoted);
        Assert.True(quoted.IsComplete);
        Assert.Equal("Binding", quoted.Name);
        Assert.Equal(@"'It\'s \{0:N2\}'", quoted.Argument("StringFormat")?.Value);
        Assert.Equal("Value", quoted.Argument("Path")?.Value);

        var unquoted = XamlSyntax.ParseExtension(@"{Binding FallbackValue=\{None\}, Path=Value}");
        Assert.NotNull(unquoted);
        Assert.True(unquoted.IsComplete);
        Assert.Equal(@"\{None\}", unquoted.Argument("FallbackValue")?.Value);
        Assert.Equal("Value", unquoted.Argument("Path")?.Value);
    }

    [Fact]
    public void DecodedValueMapsUnescapedAmpersandBeforeEntityAndClampsSpanBounds()
    {
        var decoded = new XamlSyntax.DecodedValue("&a &amp;b", sourceStart: 10);
        Assert.Equal("&a &b", decoded.Text);
        // Unescaped '&', 'a', ' ' map to their own 1-char spans; '&amp;' maps to [13..18).
        Assert.Equal((10, 1), decoded.Span(0, 1));
        Assert.Equal((11, 1), decoded.Span(1, 1));
        Assert.Equal((12, 1), decoded.Span(2, 1));
        Assert.Equal((13, 5), decoded.Span(3, 1));
        Assert.Equal((18, 1), decoded.Span(4, 1));
        // Out-of-range lengths and starts clamp cleanly instead of throwing.
        Assert.Equal((13, 6), decoded.Span(3, 100));
        Assert.Equal((19, 0), decoded.Span(99, 5));
        Assert.Equal((10, 0), decoded.Span(0, 0));
    }

    [Fact]
    public async Task OrganizeUsingsDoesNotGroupSystemPrefixNamespacesWithSystem()
    {
        const string source = """
            namespace SystemX { public static class Helper { public static void Run() { } } }
            namespace Alpha { public static class Tool { public static void Run() { } } }
            """;
        const string consumer = """
            using SystemX;
            using Alpha;
            using System;
            using System.Text;
            class C
            {
                void M()
                {
                    Console.WriteLine(new StringBuilder());
                    Helper.Run();
                    Tool.Run();
                }
            }
            """;
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Refactor", "Refactor", LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: PlatformReferences));
        workspace.AddDocument(project.Id, "Namespaces.cs", SourceText.From(source));
        var document = workspace.AddDocument(project.Id, "Consumer.cs", SourceText.From(consumer));

        var updated = await CSharpRefactorings.ApplyAsync(document, 0, "OrganizeUsings");
        var text = (await updated.GetTextAsync()).ToString();
        int systemIdx = text.IndexOf("using System;", StringComparison.Ordinal);
        int systemTextIdx = text.IndexOf("using System.Text;", StringComparison.Ordinal);
        int alphaIdx = text.IndexOf("using Alpha;", StringComparison.Ordinal);
        int systemXIdx = text.IndexOf("using SystemX;", StringComparison.Ordinal);

        Assert.True(systemIdx >= 0 && systemTextIdx > systemIdx);
        Assert.True(alphaIdx > systemTextIdx);
        Assert.True(systemXIdx > alphaIdx);
    }

    [Fact]
    public async Task UseVarFormatsOnlyAnnotatedDeclarationAndPreservesUnrelatedSpacing()
    {
        const string unrelatedLine = "    void   Other (  int   a ,  int   b )  {  }";
        string source = "class C\n{\n" + unrelatedLine + "\n    void M() { int $$value = 42; }\n}\n";
        int position = source.IndexOf("$$", StringComparison.Ordinal);
        source = source.Replace("$$", "", StringComparison.Ordinal);

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Refactor", "Refactor", LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: PlatformReferences));
        var document = workspace.AddDocument(project.Id, "Source.cs", SourceText.From(source));

        var updated = await CSharpRefactorings.ApplyAsync(document, position, "UseVar");
        var text = (await updated.GetTextAsync()).ToString();
        Assert.Contains("var value = 42;", text);
        Assert.Contains(unrelatedLine, text);
    }
}
