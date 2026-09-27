using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace WpfStudio.Workspace.Tests;

public sealed class CSharpRefactoringTests
{
    private static readonly IReadOnlyList<MetadataReference> PlatformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    [Theory]
    [InlineData("int $$number = 42;", "var number = 42;", "UseVar")]
    [InlineData("var $$number = 42;", "int number = 42;", "UseExplicitType")]
    [InlineData("var $$name = \"hello\";", "string name = \"hello\";", "UseExplicitType")]
    [InlineData("string $$name = \"hello\";", "var name = \"hello\";", "UseVar")]
    public async Task LocalTypeChangesPreserveCompilableMeaning(string declaration, string expected, string action)
    {
        using var fixture = Create("class C { void M() { " + declaration + " } }");
        var result = await CSharpRefactorings.ApplyAsync(fixture.Document, fixture.Position, action);
        Assert.Contains(expected, (await result.GetTextAsync()).ToString());
        var compilation = await result.Project.GetCompilationAsync();
        Assert.DoesNotContain(compilation!.GetDiagnostics(), x => x.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("const int $$number = 42;")]
    [InlineData("long $$number = 42;")]
    [InlineData("object $$value = \"text\";")]
    [InlineData("(int First, int Second) $$value = (1, 2);")]
    [InlineData("C $$value = new();")]
    [InlineData("int[] $$values = [1, 2, 3];")]
    [InlineData("C $$value = true ? new() : new();")]
    [InlineData("int[] $$value = true ? [1] : [2];")]
    [InlineData("C $$value = default;")]
    [InlineData("int $$first = 1, second = 2;")]
    [InlineData("int other = 0; ref int $$value = ref other;")]
    [InlineData("int $$value = ;")]
    [InlineData("int $$value = 1")]
    public async Task UnsafeVarConversionsAreRejected(string declaration)
    {
        using var fixture = Create("class C { void M() { " + declaration + " } }");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CSharpRefactorings.ApplyAsync(fixture.Document, fixture.Position, "UseVar"));
    }

    [Theory]
    [InlineData("var $$value = new { Name = \"test\" };")]
    [InlineData("var $$value = new[] { new { Name = \"test\" } };")]
    [InlineData("int other = 0; ref var $$value = ref other;")]
    public async Task AnonymousAndRefLocalsCannotReceiveAnExplicitReplacement(string declaration)
    {
        using var fixture = Create("class C { void M() { " + declaration + " } }");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CSharpRefactorings.ApplyAsync(fixture.Document, fixture.Position, "UseExplicitType"));
    }

    [Fact]
    public async Task ATypeNamedVarCannotChangeTheBindingOfTheEditedDeclaration()
    {
        using var fixture = Create("class var { } class C { void M() { int $$value = 42; } }");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CSharpRefactorings.ApplyAsync(fixture.Document, fixture.Position, "UseVar"));
    }

    [Fact]
    public async Task OrganizeUsingsSortsUsedImportsAndRemovesUnusedOnes()
    {
        using var fixture = Create("using System.Text;\nusing System.Collections;\nusing System;\nclass C { void M() { $$Console.WriteLine(new StringBuilder()); } }");
        var result = (await (await CSharpRefactorings.ApplyAsync(fixture.Document, fixture.Position, "OrganizeUsings")).GetTextAsync()).ToString();
        Assert.DoesNotContain("using System.Collections;", result);
        Assert.True(result.IndexOf("using System;", StringComparison.Ordinal) < result.IndexOf("using System.Text;", StringComparison.Ordinal));
        Assert.Contains("Console.WriteLine", result);
    }

    [Theory]
    [InlineData("// Preserve this grouping\nusing System;\nclass C { void M() { $$Console.WriteLine(); } }")]
    [InlineData("#if DEBUG\nusing System;\n#endif\nclass C { void M() { $$System.Console.WriteLine(); } }")]
    [InlineData("using System;\n#if RELEASE\nclass C { void M() { Console.WriteLine(); } }\n#endif\nclass $$D { }")]
    public async Task OrganizeUsingsDoesNotMoveCommentsOrDirectives(string source)
    {
        using var fixture = Create(source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CSharpRefactorings.ApplyAsync(fixture.Document, fixture.Position, "OrganizeUsings"));
    }

    [Fact]
    public async Task FieldsAreNotMistakenForLocals()
    {
        using var fixture = Create("class C { int $$value = 42; }");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CSharpRefactorings.ApplyAsync(fixture.Document, fixture.Position, "UseVar"));
    }

    [Fact]
    public async Task CancellationStopsAnalysis()
    {
        using var fixture = Create("class C { void M() { int $$value = 42; } }");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CSharpRefactorings.ApplyAsync(fixture.Document, fixture.Position, "UseVar", cancellation.Token));
    }

    private static Fixture Create(string source)
    {
        var position = source.IndexOf("$$", StringComparison.Ordinal);
        source = source.Replace("$$", "", StringComparison.Ordinal);
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Refactor", "Refactor", LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: ["DEBUG"]),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), metadataReferences: PlatformReferences));
        var document = workspace.AddDocument(project.Id, "Source.cs", SourceText.From(source));
        return new(workspace, document, position);
    }

    private sealed record Fixture(AdhocWorkspace Workspace, Document Document, int Position) : IDisposable
    {
        public void Dispose() => Workspace.Dispose();
    }
}
