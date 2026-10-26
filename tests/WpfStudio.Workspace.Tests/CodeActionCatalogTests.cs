using Microsoft.CodeAnalysis.Text;
using Xunit.Abstractions;

namespace WpfStudio.Workspace.Tests;

public sealed class CodeActionCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void BuiltInProvidersAreDiscovered()
    {
        output.WriteLine($"{CodeActionCatalog.FixProviderCount} fix providers, {CodeActionCatalog.RefactoringProviderCount} refactoring providers");
        Assert.True(CodeActionCatalog.FixProviderCount > 50);
        Assert.True(CodeActionCatalog.RefactoringProviderCount > 30);
    }

    [Fact]
    public async Task MissingUsingIsFixedByAddingTheImport()
    {
        using var fixture = RoslynFixture.Create("class C { System.Text.StringBuilder a = new(); Str$$ingBuilder b = new(); }");
        var after = await ApplyAsync(fixture, "using System.Text;");
        Assert.StartsWith("using System.Text;", after);
    }

    [Fact]
    public async Task InterfaceCanBeImplemented()
    {
        using var fixture = RoslynFixture.Create("class C : System.IDis$$posable { }");
        var after = await ApplyAsync(fixture, "Implement interface");
        Assert.Contains("public void Dispose()", after);
    }

    [Fact]
    public async Task IfStatementCanBeInverted()
    {
        using var fixture = RoslynFixture.Create("class C { void M(bool ready) { i$$f (ready) { A(); } else { B(); } } void A() { } void B() { } }");
        var after = await ApplyAsync(fixture, "Invert if");
        Assert.Contains("if (!ready)", after);
    }

    [Fact]
    public async Task SelectionCanBeExtractedIntoAMethod()
    {
        const string source = "class C { int M(int a, int b) { var total = a * b + 1; return total; } }";
        using var fixture = RoslynFixture.Create(source);
        var start = source.IndexOf("a * b + 1", StringComparison.Ordinal);
        var candidates = await CodeActionCatalog.GetAsync(fixture.Document, new TextSpan(start, "a * b + 1".Length), CancellationToken.None);
        output.WriteLine(string.Join(Environment.NewLine, candidates.Select(c => c.Kind + ": " + c.Title)));
        var extract = Assert.Single(candidates, c => c.Title == "Extract method");
        var changes = await CodeActionCatalog.GetChangesAsync(extract.Action, fixture.Solution, CancellationToken.None);
        Assert.NotNull(changes);
        var text = Assert.Single(changes).After.ToString();
        output.WriteLine(text);
        Assert.Contains("var total = GetTotal(a, b);", text);
        Assert.Contains("static int GetTotal(int a, int b)", text);
    }

    [Fact]
    public async Task ActionsAreOrderedWithErrorFixesFirst()
    {
        using var fixture = RoslynFixture.Create("class C : System.IDis$$posable { }");
        var candidates = await CodeActionCatalog.GetAsync(fixture.Document, new TextSpan(fixture.Position, 0), CancellationToken.None);
        Assert.Equal("Fix", candidates[0].Kind);
        Assert.Equal(candidates.Count, candidates.Select(c => c.Title).Distinct().Count());
    }

    private async Task<string> ApplyAsync(RoslynFixture fixture, string title)
    {
        var candidates = await CodeActionCatalog.GetAsync(fixture.Document, new TextSpan(fixture.Position, 0), CancellationToken.None);
        output.WriteLine(string.Join(Environment.NewLine, candidates.Select(c => c.Kind + ": " + c.Title)));
        var chosen = candidates.First(c => c.Title.StartsWith(title, StringComparison.Ordinal));
        var changes = await CodeActionCatalog.GetChangesAsync(chosen.Action, fixture.Solution, CancellationToken.None);
        Assert.NotNull(changes);
        return Assert.Single(changes).After.ToString();
    }
}
