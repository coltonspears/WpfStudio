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
    public async Task CompletionEntriesSayWhereTheyComeFromAndDescribeThemselves()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = "using System;\nnamespace Shop;\npublic class Lookup\n{\n    /// <summary>The running total.</summary>\n    public int Total;\n"
            + "    public int Read(string name) => To;\n    public int Size(string name) => name.Le;\n    [Obs]\n    public void Old() { }\n}\n";
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);

        // Origins give way to a compilation that is not ready, so wait for the first one.
        async Task<CompletionEntry> EntryAsync(string marker, string name, bool expectOrigin = true)
        {
            int position = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                var entry = Assert.Single((await engine.GetCompletionsAsync(new DocumentPositionRequest(Source, position, 1))).Items, item => item.DisplayText == name);
                if (!expectOrigin || entry.Origin is not null || DateTime.UtcNow > deadline) return entry;
                await Task.Delay(100);
            }
        }

        Assert.Equal("System", (await EntryAsync("[Obs", "Obsolete")).Origin);
        Assert.Equal("String", (await EntryAsync("name.Le", "Length")).Origin);
        Assert.Null((await EntryAsync("Size(string name) => ", "name", expectOrigin: false)).Origin);
        var total = await EntryAsync("=> To", "Total");
        Assert.Equal("Lookup", total.Origin);

        var details = await engine.GetCompletionDetailsAsync(new CompletionEditRequest(Source, 1, total.Id));
        Assert.NotNull(details);
        Assert.Contains("Field", details.Tags);
        Assert.Equal(["Description", "DocumentationComments"], details.Sections.Select(section => section.Kind));
        Assert.Contains("Total", string.Concat(details.Sections[0].Runs.Select(run => run.Text)), StringComparison.Ordinal);
        Assert.Contains("The running total.", string.Concat(details.Sections[1].Runs.Select(run => run.Text)), StringComparison.Ordinal);

        // Typing filters the open popup, and its entries can still be described.
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text.Replace("=> To;", "=> Tot;", StringComparison.Ordinal), 2, Analyze: false))).Accepted);
        Assert.NotNull(await engine.GetCompletionDetailsAsync(new CompletionEditRequest(Source, 2, total.Id)));
        // A newer list replaces it.
        await engine.GetCompletionsAsync(new DocumentPositionRequest(Source, text.IndexOf("[Obs", StringComparison.Ordinal) + "[Obs".Length + 1, 2));
        Assert.Null(await engine.GetCompletionDetailsAsync(new CompletionEditRequest(Source, 2, total.Id)));
    }

    [Fact]
    public async Task AnEditThatLeavesDeclarationsAloneInvalidatesNothingOutsideItsFile()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = "namespace Shop;\npublic class Lookup\n{\n    public const int Limit = 1;\n    public int Total = 1;\n    public int Size()\n    {\n        return 1;\n    }\n}\n";
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        int semantic = 0, models = 0, bodies = 0;
        engine.SemanticStateChanged += (_, _) => Interlocked.Increment(ref semantic);
        engine.CSharpModelChanged += (_, _) => Interlocked.Increment(ref models);
        engine.CSharpBodyChanged += (_, _) => Interlocked.Increment(ref bodies);
        long version = 1;
        async Task<bool> EditAsync(string from, string to)
        {
            text = text.Replace(from, to, StringComparison.Ordinal);
            var update = await engine.UpdateDocumentAsync(new(Source, text, ++version, Analyze: false));
            Assert.True(update.Accepted);
            return update.DeclarationsChanged;
        }

        Assert.False(await EditAsync("return 1;", "return 2;"));
        Assert.False(await EditAsync("    public int Size()", "    // Sized.\n    public int Size()"));
        Assert.False(await EditAsync("Total = 1", "Total = 2"));
        Assert.Equal((0, 0, 3), (Volatile.Read(ref semantic), Volatile.Read(ref models), Volatile.Read(ref bodies)));

        Assert.True(await EditAsync("Limit = 1", "Limit = 2")); // A constant's value is part of what other files see.
        Assert.True(await EditAsync("public int Size()", "public long Size()"));
        Assert.True(await EditAsync("namespace Shop;", "using System;\nnamespace Shop;"));
        Assert.Equal((3, 3, 3), (Volatile.Read(ref semantic), Volatile.Read(ref models), Volatile.Read(ref bodies)));

        // The file itself still sees every edit.
        Assert.True(await EditAsync("return 2;", "return \"text\";") is false);
        Assert.Contains((await engine.UpdateDocumentAsync(new(Source, text, version))).Diagnostics, diagnostic => diagnostic.Severity == "Error");
    }

    [Fact]
    public async Task ReferenceCountsAreOnlySearchedForAgainWhenAnEditTouchesTheirNames()
    {
        string other = Path.Combine(_directory, "Other.cs");
        var otherText = "namespace Shop;\npublic class Other\n{\n    public int Use(Lookup lookup)\n    {\n        return 1;\n    }\n}\n";
        await File.WriteAllTextAsync(other, otherText);
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = "namespace Shop;\npublic class MarkAttribute : System.Attribute { }\npublic class Lookup\n{\n    public Lookup() { }\n    public int Read() => 1;\n    public int Size()\n    {\n        return 1;\n    }\n}\n";
        long version = 0;
        async Task<CodeLensResult> LensAfterAsync(string from, string to)
        {
            text = text.Replace(from, to, StringComparison.Ordinal);
            Assert.True((await engine.UpdateDocumentAsync(new(Source, text, ++version, Analyze: false))).Accepted);
            return await engine.GetCodeLensAsync(new(Source, version));
        }
        static CodeLensEntry Read(CodeLensResult lens) => Assert.Single(lens.Entries, entry => entry.Name == "Read");

        var first = await LensAfterAsync("namespace Shop;", "namespace Shop;");
        Assert.Equal(5, first.Counted); // MarkAttribute, Lookup, its constructor, Read and Size.
        Assert.Equal(0, Read(first).References);

        // A comment moves the declarations and cannot change a count.
        var commented = await LensAfterAsync("    public int Read()", "    // Reads.\n    public int Read()");
        Assert.Equal(0, commented.Counted);
        Assert.Equal(5, commented.Entries.Count);
        Assert.Equal(text.IndexOf("Read()", StringComparison.Ordinal), Read(commented).Position);
        Assert.Equal(first.Entries.First(entry => entry.Name == "Size").Line + 1, commented.Entries.First(entry => entry.Name == "Size").Line);

        // Only the declaration whose name the edit used is searched for again.
        var used = await LensAfterAsync("return 1;", "return Read();");
        Assert.Equal(1, used.Counted);
        Assert.Equal(1, Read(used).References);

        // A new name nothing here declares, as most typing is.
        var local = await LensAfterAsync("return Read();", "var unrelated = 2;\n        return Read();");
        Assert.Equal(0, local.Counted);
        Assert.Equal(1, Read(local).References);

        // An attribute is used without its suffix.
        var attribute = await LensAfterAsync("var unrelated = 2;", "var unrelated = 2;\n        [Mark] void Local() { }");
        Assert.Equal(1, attribute.Counted);
        Assert.Equal(1, Assert.Single(attribute.Entries, entry => entry.Name == "MarkAttribute").References);

        // A constructor is used through its type's name. new() names nothing, and the counts leave it out as an implicit use.
        var named = await LensAfterAsync("[Mark] void Local() { }", "[Mark] void Local() { }\n        _ = new Lookup();");
        Assert.Equal(2, named.Counted);
        Assert.Equal(1, Assert.Single(named.Entries, entry => entry.Name == ".ctor").References);
        var unnamed = await LensAfterAsync("_ = new Lookup();", "_ = new Lookup();\n        Lookup target = new();");
        Assert.Equal(1, Assert.Single(unnamed.Entries, entry => entry.Name == ".ctor").References);

        // The same holds for an edit in another file, which also shows up in this file's counts.
        Assert.True((await engine.UpdateDocumentAsync(new(other, otherText, 1, Analyze: false))).Accepted);
        Assert.True((await engine.UpdateDocumentAsync(new(other, otherText.Replace("return 1;", "return lookup.Read();", StringComparison.Ordinal), 2, Analyze: false))).Accepted);
        var elsewhere = await engine.GetCodeLensAsync(new(Source, version));
        Assert.Equal(1, elsewhere.Counted);
        Assert.Equal(2, Read(elsewhere).References);

        // Changing a declaration can change anything, so everything is counted again.
        Assert.Equal(5, (await LensAfterAsync("public int Size()", "public long Size()")).Counted);
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
    public async Task DefinitionSaysWhyThereIsNowhereToGoAndFallsBackToDeclarationsByName()
    {
        var other = Path.Combine(_directory, "Other.cs");
        var text = "namespace Shop;\npublic class Lookup\n{\n    public string Text = string.Empty;\n    public Missing Find() => null;\n    public void Run() => Unknown.Save();\n    public void Use(OrderStore store) => store.Save();\n}\n";
        await File.WriteAllTextAsync(other, text);
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        Assert.True((await engine.UpdateDocumentAsync(new(other, text, 1, Analyze: false))).Accepted);
        Task<DefinitionResult> AtAsync(string marker, int into) => engine.FindDefinitionAsync(new(other, text.IndexOf(marker, StringComparison.Ordinal) + into, 1));

        // Resolved, and in source.
        var store = await AtAsync("store.Save", "store.Sa".Length);
        Assert.True(store.Resolved);
        Assert.Null(store.Status);
        Assert.Equal(Source, Assert.Single(store.Locations).Path, ignoreCase: true);

        // Resolved into an assembly: nowhere to go, and the reason names it.
        var framework = await AtAsync("string.Empty", "string.Em".Length);
        Assert.True(framework.Resolved);
        Assert.Empty(framework.Locations);
        Assert.Contains("has no source in this workspace", framework.Status);

        // A name nothing declares.
        var missing = await AtAsync("Missing Find", 3);
        Assert.False(missing.Resolved);
        Assert.Empty(missing.Locations);
        Assert.Contains("'Missing' does not resolve", missing.Status);

        // A name that does not resolve here, but that source declares: every declaration with it is offered as a guess.
        var guessed = await AtAsync("Unknown.Save", "Unknown.Sa".Length);
        Assert.False(guessed.Resolved);
        Assert.Equal(2, guessed.Locations.Count);
        Assert.All(guessed.Locations, location => Assert.Equal(Source, location.Path, ignoreCase: true));
        Assert.Contains("Showing the declarations with that name", guessed.Status);

        // Nothing under the caret to look up.
        Assert.Contains("Place the caret on a name", (await AtAsync("{\n    public string", 0)).Status);
    }

    [Fact]
    public async Task CompletionMatchesInitialsAndTextInsideANameNotOnlyItsStart()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = "namespace Shop;\npublic class Lookup\n{\n    public int RunningTotal;\n    public int ByInitials() => RT;\n    public int ByPart() => Tot;\n    public int ByStart() => Run;\n}\n";
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        foreach (var typed in new[] { "=> RT", "=> Tot", "=> Run" })
        {
            var completions = await engine.GetCompletionsAsync(new DocumentPositionRequest(Source, text.IndexOf(typed, StringComparison.Ordinal) + typed.Length, 1));
            Assert.Contains(completions.Items, item => item.DisplayText == "RunningTotal");
            Assert.False(completions.Incomplete);
            // What does not match in any of those ways stays out.
            Assert.DoesNotContain(completions.Items, item => item.DisplayText is "ByInitials" or "GetHashCode");
        }
    }

    [Fact]
    public void CompletionEntriesBeyondOneReplyKeepTheClosestMatchesInTheListsOrder()
    {
        var items = new[] { "Assert", "ToString", "StringBuilder", "String", "SortedSet", "ObservableCollection", "Strip" }.Select(name => Microsoft.CodeAnalysis.Completion.CompletionItem.Create(name)).ToArray();
        static string[] Names((IReadOnlyList<Microsoft.CodeAnalysis.Completion.CompletionItem> Items, bool Incomplete) result) => result.Items.Select(item => item.DisplayText).ToArray();

        var everything = CompletionMatching.Select(items, "", 10);
        Assert.Equal(7, everything.Items.Count);
        Assert.False(everything.Incomplete);

        var matching = CompletionMatching.Select(items, "Str", 10);
        Assert.Equal(["ToString", "StringBuilder", "String", "Strip"], Names(matching));
        Assert.False(matching.Incomplete);
        Assert.Equal(["ObservableCollection"], Names(CompletionMatching.Select(items, "oc", 10)));
        // Initials match; two letters in the middle of a word do not, though three do.
        Assert.Equal(["SortedSet"], Names(CompletionMatching.Select(items, "SS", 10)));
        Assert.Equal(["Assert"], Names(CompletionMatching.Select(items, "sse", 10)));

        // Too many: the ones that start with the text are kept over the one that only contains it, still in list order.
        var cut = CompletionMatching.Select(items, "Str", 3);
        Assert.Equal(["StringBuilder", "String", "Strip"], Names(cut));
        Assert.True(cut.Incomplete);
        Assert.True(CompletionMatching.Select(items, "", 3).Incomplete);
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
