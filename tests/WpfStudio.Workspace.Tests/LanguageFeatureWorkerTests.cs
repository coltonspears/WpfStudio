using System.Diagnostics;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

/// <summary>End-to-end worker requests against a real restored class library loaded through MSBuild.</summary>
public sealed class LanguageFeatureWorkerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WpfStudio-LanguageFeatures-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(_directory, "Library.csproj");
    private string Source => Path.Combine(_directory, "Orders.cs");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Source, "namespace Shop;\npublic interface IOrderStore { void Save(); }\npublic sealed class OrderStore : IOrderStore { public void Save() { } }\npublic class Report { StringBuilder text = new(); }\n");
        using var restore = Process.Start(new ProcessStartInfo("dotnet", "restore --nologo -v q") { WorkingDirectory = _directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await restore.WaitForExitAsync();
        Assert.Equal(0, restore.ExitCode);
    }

    public Task DisposeAsync() { try { Directory.Delete(_directory, true); } catch (IOException) { } return Task.CompletedTask; }

    [Fact]
    public async Task QuickActionsListAndComputeReviewedEdits()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = await File.ReadAllTextAsync(Source);
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        var caret = text.IndexOf("StringBuilder", StringComparison.Ordinal) + 3;
        var actions = await engine.GetCodeActionsAsync(new(Source, caret, 0, 1));
        var addUsing = Assert.Single(actions.Actions, action => action.Title == "using System.Text;");
        Assert.Equal("Fix", addUsing.Kind);
        var edit = await engine.ApplyCodeActionAsync(new(Source, 1, addUsing.Id));
        var document = Assert.Single(edit.Documents);
        Assert.Equal(1, document.Version);
        Assert.StartsWith("using System.Text;", Apply(text, document.Edits));

        // A newer buffer invalidates the listed actions.
        await engine.UpdateDocumentAsync(new(Source, text + " ", 2, Analyze: false));
        var stale = await Assert.ThrowsAnyAsync<Exception>(() => engine.ApplyCodeActionAsync(new(Source, 2, addUsing.Id)));
        Assert.Contains("no longer available", stale.Message);
    }

    [Fact]
    public async Task QuickInfoShowsTheClassifiedSignatureAndDocumentation()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = "namespace Shop;\npublic sealed class Pricing\n{\n    /// <summary>Adds tax to a net amount.</summary>\n    /// <param name=\"net\">The amount before tax.</param>\n    /// <returns>The gross amount.</returns>\n    public decimal Gross(decimal net) => net * 1.2m;\n    public decimal Total => Gross(10m);\n}\n";
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        var call = text.LastIndexOf("Gross", StringComparison.Ordinal) + 2;
        var info = await engine.GetQuickInfoAsync(new(Source, call, 1));
        Assert.NotNull(info);
        Assert.Equal(text.LastIndexOf("Gross", StringComparison.Ordinal), info.Start);
        Assert.Contains("Method", info.Tags);
        var description = Assert.Single(info.Sections, section => section.Kind == "Description");
        Assert.Equal("decimal Pricing.Gross(decimal net)", string.Concat(description.Runs.Select(run => run.Text)));
        Assert.Contains(description.Runs, run => run.Tag == "Keyword" && run.Text == "decimal");
        Assert.Contains(description.Runs, run => run.Tag == "Method" && run.Text == "Gross");
        Assert.Contains(info.Sections, section => section.Kind == "DocumentationComments" && string.Concat(section.Runs.Select(run => run.Text)).Contains("Adds tax to a net amount."));
        Assert.Contains(info.Sections, section => section.Kind == "ReturnsDocumentationComments");
        // Whitespace has nothing to describe.
        Assert.Null(await engine.GetQuickInfoAsync(new(Source, text.IndexOf("    public decimal Total", StringComparison.Ordinal) + 1, 1)));
    }

    [Fact]
    public async Task CompletionCommitAcceptsEveryPathSpellingTheWorkerAccepts()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = "namespace Shop;\npublic class Lookup { public int Total; public int Read() => To; }\n";
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        var position = text.IndexOf("=> To;", StringComparison.Ordinal) + "=> To".Length;
        var forwardSlashes = Source.Replace('\\', '/');
        var completions = await engine.GetCompletionsAsync(new DocumentPositionRequest(forwardSlashes, position, 1));
        var total = Assert.Single(completions.Items, item => item.DisplayText == "Total");
        var edit = await engine.GetCompletionEditAsync(new CompletionEditRequest(forwardSlashes, 1, total.Id));
        Assert.NotNull(edit);
        Assert.Equal("Total", edit.NewText);
    }

    [Fact]
    public async Task DefinitionMappedByLineDirectiveFallsBackWhenItsFileCannotBeRead()
    {
        var mapped = Path.Combine(_directory, "Mapped.xaml");
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = $"namespace Shop;\npublic class Page\n{{\n#line 1 \"{mapped}\"\n    public int Field;\n#line default\n    public int Read() => Field;\n}}\n";
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        var usage = new DocumentPositionRequest(Source, text.LastIndexOf("Field", StringComparison.Ordinal) + 1, 1);

        // Readable: the directive's target, with an offset in that file rather than the C#.
        await File.WriteAllTextAsync(mapped, "<Grid />\n");
        var target = Assert.Single(await engine.GetDefinitionAsync(usage));
        Assert.Equal(mapped, target.Path, ignoreCase: true);
        Assert.Equal(1, target.Line);
        Assert.InRange(target.Start, 0, "<Grid />".Length);

        // Renamed after the last build.
        File.Delete(mapped);
        AssertFallsBackToDeclaration(Assert.Single(await engine.GetDefinitionAsync(usage)));

        // Exists but cannot be read: the worker must still answer, with the C# holding the declaration.
        await File.WriteAllTextAsync(mapped, "<Grid />\n");
        using var locked = new FileStream(mapped, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AssertFallsBackToDeclaration(Assert.Single(await engine.GetDefinitionAsync(usage)));

        void AssertFallsBackToDeclaration(SourceLocation location)
        {
            Assert.NotEqual(mapped, location.Path, StringComparer.OrdinalIgnoreCase);
            Assert.True(File.Exists(location.Path));
            Assert.Equal(text.IndexOf("Field;", StringComparison.Ordinal), location.Start);
        }
    }

    [Fact]
    public async Task EditsReportFileDiagnosticsAtOnceAndProjectWideOnesWhenTheCompilationIsDone()
    {
        await File.WriteAllTextAsync(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><OutputType>Exe</OutputType></PropertyGroup></Project>");
        var other = Path.Combine(_directory, "Other.cs");
        await File.WriteAllTextAsync(other, "namespace Shop;\npublic class Other { public int Broken() => \"elsewhere\"; }\n");
        var text = "namespace Shop;\npublic class Report\n{\n    private int _unused;\n    private int _assigned = 1;\n    public int Bad() => \"text\";\n}\n";
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        using var pushes = new SemaphoreSlim(0);
        var pushed = 0;
        engine.ProjectDiagnosticsChanged += (_, _) => { Interlocked.Increment(ref pushed); pushes.Release(); };
        var invalidations = 0;
        engine.SemanticStateChanged += (_, _) => Interlocked.Increment(ref invalidations);
        engine.NameProjectionChanged += (_, _) => Interlocked.Increment(ref invalidations);
        async Task<IReadOnlyList<WorkspaceDiagnostic>> SettledAsync(string current, long version)
        {
            Assert.True(await pushes.WaitAsync(TimeSpan.FromMinutes(3)));
            return (await engine.UpdateDocumentAsync(new(Source, current, version))).Diagnostics;
        }

        // Only this file was bound, so what needs every method body or has no file is not known yet.
        var first = (await engine.UpdateDocumentAsync(new(Source, text, 1))).Diagnostics;
        Assert.Contains(first, diagnostic => diagnostic.Id == "CS0029" && diagnostic.Severity == "Error");
        Assert.DoesNotContain(first, diagnostic => diagnostic.Id is "CS0169" or "CS0414" or "CS5001");

        var settled = await SettledAsync(text, 1);
        Assert.Contains(settled, diagnostic => diagnostic.Id == "CS0029");
        var unused = Assert.Single(settled, diagnostic => diagnostic.Id == "CS0169");
        Assert.Equal(Source, unused.Path, ignoreCase: true);
        Assert.Equal(4, unused.Line);
        Assert.Equal(text.IndexOf("_unused", StringComparison.Ordinal), unused.Start);
        Assert.Single(settled, diagnostic => diagnostic.Id == "CS0414");
        Assert.Null(Assert.Single(settled, diagnostic => diagnostic.Id == "CS5001").Path);
        // Other files' diagnostics stay out, as they always have.
        Assert.DoesNotContain(settled, diagnostic => diagnostic.Message.Contains("elsewhere") || (diagnostic.Path?.EndsWith("Other.cs", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.Equal(settled.Count, settled.Distinct().Count());
        // The push is not a semantic change, and the complete reply must not start another pass.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Volatile.Read(ref invalidations));
        Assert.Equal(1, Volatile.Read(ref pushed));
        Assert.Equal(settled, (await engine.UpdateDocumentAsync(new(Source, text, 1))).Diagnostics);

        // An edit elsewhere in the file moves them at once, and a pass that agrees has nothing to push.
        var moved = text.Replace("    private int _unused;", "    // A note.\n    private int _unused;", StringComparison.Ordinal);
        var carried = (await engine.UpdateDocumentAsync(new(Source, moved, 2))).Diagnostics;
        var carriedUnused = Assert.Single(carried, diagnostic => diagnostic.Id == "CS0169");
        Assert.Equal(5, carriedUnused.Line);
        Assert.Equal(17, carriedUnused.Column);
        Assert.Equal(moved.IndexOf("_unused", StringComparison.Ordinal), carriedUnused.Start);
        Assert.Contains(carried, diagnostic => diagnostic.Id == "CS0414" && diagnostic.Start == moved.IndexOf("_assigned", StringComparison.Ordinal));
        Assert.Contains(carried, diagnostic => diagnostic.Id == "CS5001");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Volatile.Read(ref pushed));
        Assert.True(carried.ToHashSet().SetEquals((await engine.UpdateDocumentAsync(new(Source, moved, 2))).Diagnostics));

        // An edit touching one drops it until the pass has the new wording.
        var renamed = moved.Replace("_unused;", "_unusedNow;", StringComparison.Ordinal);
        var touched = (await engine.UpdateDocumentAsync(new(Source, renamed, 3))).Diagnostics;
        Assert.DoesNotContain(touched, diagnostic => diagnostic.Id == "CS0169");
        Assert.Contains(touched, diagnostic => diagnostic.Id == "CS0414");
        Assert.Contains("_unusedNow", Assert.Single(await SettledAsync(renamed, 3), diagnostic => diagnostic.Id == "CS0169").Message);

        // An edit that makes one wrong keeps it only until the pass disagrees.
        var used = renamed.Replace("    public int Bad()", "    public int Use() => _unusedNow;\n    public int Bad()", StringComparison.Ordinal);
        Assert.Contains((await engine.UpdateDocumentAsync(new(Source, used, 4))).Diagnostics, diagnostic => diagnostic.Id == "CS0169");
        Assert.DoesNotContain(await SettledAsync(used, 4), diagnostic => diagnostic.Id == "CS0169");

        // Rewriting the class leaves nothing stale; a diagnostic with no position stays.
        var fixedText = "namespace Shop;\npublic class Report\n{\n    public int Good() => 1;\n}\n";
        var edited = (await engine.UpdateDocumentAsync(new(Source, fixedText, 5))).Diagnostics;
        Assert.DoesNotContain(edited, diagnostic => diagnostic.Id is "CS0029" or "CS0169" or "CS0414");
        Assert.Contains(edited, diagnostic => diagnostic.Id == "CS5001");
    }

    private static string Apply(string text, IReadOnlyList<TextEdit> edits) =>
        edits.OrderByDescending(edit => edit.Start).Aggregate(text, (current, edit) => current.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText));

    [Fact]
    public async Task SymbolsAndImplementationsResolveAcrossTheLoadedProject()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = await File.ReadAllTextAsync(Source);
        await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false));
        var symbols = await engine.SearchSymbolsAsync(new("OrdSto"));
        Assert.Contains(symbols, symbol => symbol.Name == "OrderStore" && symbol.Kind == "Class" && symbol.Container == "Shop" && symbol.Location.Line == 3);
        var implementations = await engine.FindImplementationsAsync(new(Source, text.IndexOf("void Save", StringComparison.Ordinal) + 6, 1));
        var location = Assert.Single(implementations);
        Assert.Equal(3, location.Line);
    }

    [Fact]
    public async Task DecorationsDescribeTheSynchronizedVersion()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = await File.ReadAllTextAsync(Source) + "public static class Use { public static void Run(OrderStore store) { store.Save(); var count = System.Math.Max(1, 2); } }\n";
        await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false));
        var decorations = await engine.GetEditorDecorationsAsync(new(Source, 1));
        Assert.Equal(1, decorations.Version);
        Assert.Contains(decorations.Spans, span => span.Kind == "Interface" && text.Substring(span.Start, span.Length) == "IOrderStore");
        Assert.Contains(decorations.Spans, span => span.Kind == "Method" && text.Substring(span.Start, span.Length) == "Save");
        Assert.Contains(decorations.Hints, hint => hint.Text == ": int" && text[..hint.Position].EndsWith("var count", StringComparison.Ordinal));
        var stale = await Assert.ThrowsAnyAsync<Exception>(() => engine.GetEditorDecorationsAsync(new(Source, 7)));
        Assert.Contains("stale", stale.Message);
    }
}
