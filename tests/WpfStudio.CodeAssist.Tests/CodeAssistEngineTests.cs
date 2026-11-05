using WpfStudio.Contracts;

namespace WpfStudio.CodeAssist.Tests;

public sealed class CodeAssistEngineTests : IDisposable
{
    private static readonly string[] ListMembers = ["Add", "AddRange", "Clear", "Contains", "Count", "IndexOf", "Insert", "Remove", "Sort", "ToArray"];
    private readonly string _copy = Path.Combine(Path.GetTempPath(), "WpfStudio-CodeAssist-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_copy)) Directory.Delete(_copy, recursive: true); }

    private static CodeAssistEngine NewEngine() => new(new AssistModelCatalog(AssistModelCatalog.DefaultDirectory));

    private static CompletionResult Completions(string text, params string[] names) =>
        new(1, text.Length, 0, names.Select((name, index) => new CompletionEntry("id" + index, name, name, null, ["Method", "Public"])).ToArray());

    /// <summary>The engine loads in the background; a request made before it finishes may be past the ranking budget.</summary>
    private static async Task<CompletionResult> RankOnceLoadedAsync(CodeAssistEngine engine, string text, CompletionResult completions)
    {
        var ranked = await engine.RankMembersAsync(text, completions.Start, ["Add", "Clear"]);
        Assert.NotEmpty(ranked);
        return await engine.RankCompletionsAsync(text, completions);
    }

    [Fact]
    public async Task IsOffByDefaultAndLeavesEverythingAlone()
    {
        using var engine = NewEngine();
        Assert.False(engine.RanksMembers);
        Assert.False(engine.SuggestsLines);
        var completions = Completions("names.", ListMembers);
        Assert.Same(completions, await engine.RankCompletionsAsync("names.", completions));
        Assert.Empty(await engine.RankMembersAsync("names.", 6, ListMembers));
        Assert.Null(await engine.SuggestLineAsync("var x = 1;", 10));
        Assert.Contains("Off", engine.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecommendsLikelyMembersFirstAndKeepsTheRest()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        const string text = "// Clear out the array\nnames.";
        var completions = Completions(text, ListMembers);
        var ranked = await RankOnceLoadedAsync(engine, text, completions);

        Assert.Equal(completions.Items.Count, ranked.Items.Count);
        Assert.Equal(completions.Items.Select(item => item.Id).Order(), ranked.Items.Select(item => item.Id).Order());
        Assert.Equal("Clear", ranked.Items[0].DisplayText);
        Assert.Equal(CodeAssistEngine.MaxRecommendations, ranked.Items.Count(item => item.Recommended));
        Assert.All(ranked.Items.Take(CodeAssistEngine.MaxRecommendations), item => Assert.True(item.Recommended));
        // Whatever the model did not pick keeps the order the compiler gave it.
        var rest = ranked.Items.Skip(CodeAssistEngine.MaxRecommendations).Select(item => item.DisplayText).ToArray();
        Assert.Equal(ListMembers.Where(rest.Contains), rest);
        Assert.Contains("code-assist-members 2.0.0", ranked.Items[0].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheChosenVersionIsTheOneThatRanks()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true, "1.0.0"));
        const string text = "// Clear out the array\nnames.";
        var ranked = await RankOnceLoadedAsync(engine, text, Completions(text, ListMembers));
        Assert.Contains("code-assist-members 1.0.0", ranked.Items[0].Description, StringComparison.Ordinal);
        Assert.Contains("code-assist-members 1.0.0 is loaded", engine.Status, StringComparison.Ordinal);

        await engine.ConfigureAsync(new CodeAssistOptions(true, "2.0.0"));
        Assert.Contains("code-assist-members 2.0.0 is loaded", engine.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeverStarsEveryEntry()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        var ranked = await RankOnceLoadedAsync(engine, "x.", Completions("x.", "Alpha", "Beta", "Gamma"));
        Assert.Equal(2, ranked.Items.Count(item => item.Recommended));
    }

    [Theory]
    [InlineData("var total = ")]
    [InlineData("items[1..")]
    public async Task OnlyAMemberAccessIsRanked(string text)
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        var completions = Completions(text, ListMembers);
        Assert.Same(completions, await engine.RankCompletionsAsync(text, completions));
    }

    [Fact]
    public async Task NamespacesAndTypesAreNotRanked()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        var completions = new CompletionResult(1, 7, 0, [new("a", "IO", "IO", null, ["Namespace"]), new("b", "Console", "Console", null, ["Class", "Public"]), new("c", "Text", "Text", null, ["Namespace"])]);
        Assert.Same(completions, await engine.RankCompletionsAsync("System.", completions));
    }

    [Fact]
    public async Task ExtensionMethodsAreNotRankedBecauseTheModelsNeverSawThem()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        const string text = "names.";
        CompletionEntry[] items = [new("a", "ToList<>", "ToList", null, ["ExtensionMethod", "Public"]), new("b", "Add", "Add", null, ["Method", "Public"]), new("c", "Clear", "Clear", null, ["Method", "Public"]), new("d", "Count", "Count", null, ["Property", "Public"])];
        var ranked = await RankOnceLoadedAsync(engine, text, new CompletionResult(1, text.Length, 0, items));
        var extension = Assert.Single(ranked.Items, item => item.Id == "a");
        Assert.False(extension.Recommended);
        Assert.Equal(2, ranked.Items.Count(item => item.Recommended));
        Assert.Equal(items.Length, ranked.Items.Count);
    }

    [Fact]
    public async Task TurningOffStopsRankingAndReleasesTheModel()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        Assert.NotEmpty(await engine.RankMembersAsync("x.", 2, ListMembers));
        await engine.ConfigureAsync(CodeAssistOptions.Off);
        Assert.Contains("No model is loaded", engine.Status, StringComparison.Ordinal);
        Assert.Empty(await engine.RankMembersAsync("x.", 2, ListMembers));
    }

    [Fact]
    public async Task TurningOnAndOffQuicklyEndsOff()
    {
        using var engine = NewEngine();
        var changes = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            changes.Add(engine.ConfigureAsync(new CodeAssistOptions(true, LineSuggestions: true)));
            changes.Add(engine.ConfigureAsync(CodeAssistOptions.Off));
        }
        await Task.WhenAll(changes);
        Assert.False(engine.RanksMembers);
        Assert.Empty(await engine.RankMembersAsync("x.", 2, ListMembers));
        // Whatever order those ran in, nothing is left behind to find again on the way back on.
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        Assert.Contains("code-assist-members 2.0.0 is loaded", engine.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADamagedModelIsReportedAndCompletionStillWorks()
    {
        CopyModels();
        File.AppendAllText(Path.Combine(_copy, "members", "2.0.0", "model.onnx"), "tampered");
        using var engine = new CodeAssistEngine(new AssistModelCatalog(_copy));
        int reports = 0;
        engine.StatusChanged += (_, _) => Interlocked.Increment(ref reports);
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        var completions = Completions("names.", ListMembers);
        Assert.Same(completions, await engine.RankCompletionsAsync("names.", completions));
        Assert.Contains("code-assist-members 2.0.0 failed", engine.Status, StringComparison.Ordinal);
        Assert.InRange(reports, 1, int.MaxValue);
        // The older version in the same folder is untouched, and choosing it recovers.
        await engine.ConfigureAsync(new CodeAssistOptions(true, "1.0.0"));
        Assert.NotEmpty(await engine.RankMembersAsync("names.", 6, ListMembers));
    }

    [Fact]
    public async Task LineSuggestionsNeedTheirOwnSwitch()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        Assert.True(engine.RanksMembers);
        Assert.False(engine.SuggestsLines);
        Assert.Null(await engine.SuggestLineAsync("var x = ", 8));
        await engine.ConfigureAsync(new CodeAssistOptions(true, LineSuggestions: true));
        Assert.True(engine.SuggestsLines);
        await engine.ConfigureAsync(new CodeAssistOptions(false, LineSuggestions: true));
        Assert.False(engine.SuggestsLines);
    }

    // With the file's opening lines: the newest gate rightly distrusts a fragment with nothing before it.
    private const string PropertyContext = "using System;\n\nnamespace Demo;\n\npublic sealed class Person\n{\n    public string Name { get; set; }\n    public string Email { get; ";

    [Fact]
    public async Task ALineSuggestionStaysOnItsLineAndIsFromTheNewestModel()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true, LineSuggestions: true));
        string? line = await engine.SuggestLineAsync(PropertyContext, PropertyContext.Length);
        Assert.NotNull(line);
        Assert.StartsWith("set;", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("code-assist-lines 4.0.0 is loaded", engine.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.0")]
    [InlineData("3.0.0")]
    [InlineData("4.0.0")]
    public async Task WindowsLineEndingsGiveTheSameSuggestion(string version)
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true, LineSuggestions: true, LineModelVersion: version));
        string windows = PropertyContext.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(await engine.SuggestLineAsync(PropertyContext, PropertyContext.Length), await engine.SuggestLineAsync(windows, windows.Length));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.0")]
    [InlineData("3.0.0")]
    [InlineData("4.0.0")]
    public async Task TabIndentationGivesTheSameSuggestionAsSpaces(string version)
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true, LineSuggestions: true, LineModelVersion: version));
        const string tabs = "using System;\n\nnamespace Demo;\n\npublic sealed class Person\n{\n\tpublic string Name { get; set; }\n\tpublic string Email { get; ";
        Assert.Equal(await engine.SuggestLineAsync(PropertyContext, PropertyContext.Length), await engine.SuggestLineAsync(tabs, tabs.Length));
    }

    [Fact]
    public async Task WholeLinesAreMarkedFinishedAndAnUnsureLineShowsOnlyItsSurePart()
    {
        var model = new AssistModelCatalog(AssistModelCatalog.DefaultDirectory).Resolve(AssistModelKind.Lines, "4.0.0")!;
        using var generator = TransformerLineGenerator.Load(model);
        Assert.True(generator.OffersPartialLines);
        var whole = generator.Generate(PropertyContext, CodeAssistEngine.MaxLineTokens);
        Assert.Equal(new LineCompletion("set; }", true), whole);
        const string fragment = "public sealed class Person\n{\n    public string Name { get; set; }\n    public string Email ";
        Assert.Equal(new LineCompletion("{ get; set; }", false), generator.Generate(fragment, CodeAssistEngine.MaxLineTokens));
        // Its guess here may be wrong, but it is sure of the part that closes the brace left open, however short that is.
        const string open = "public partial class Counter\n{\n    public string Name { get; set; }\n    public string Title { get; ";
        Assert.StartsWith("set;", generator.Generate(open, CodeAssistEngine.MaxLineTokens).Text, StringComparison.Ordinal);
        // A line it cannot finish in the tokens allowed is not shown at all, not even in part.
        Assert.Equal(new LineCompletion("", false), generator.Generate(fragment, 2));
        await Task.CompletedTask;
    }

    [Fact]
    public void TheGateSearchesWhatItWasFittedOn()
    {
        // Measured before tabs expand. This fits in 5,000 characters as written, and would not once its tabs became spaces.
        string tabbed = "first\r\n" + string.Concat(Enumerable.Repeat("\t\t\t\tx;\n", 700));
        string window = TransformerLineGenerator.SearchWindow(tabbed);
        Assert.StartsWith("first\n", window, StringComparison.Ordinal);
        Assert.Contains('\t', window);
        Assert.Equal(5000, TransformerLineGenerator.SearchWindow(string.Concat(Enumerable.Repeat("x;\n", 2000))).Length);

        // As Python reads them: an accented letter continues a name, and a combining mark ends it.
        Assert.Equal(["x\u00E91"], TransformerLineGenerator.Names("x\u00E91"));
        Assert.Equal(["xe", "y"], TransformerLineGenerator.Names("xe\u0301y"));
        Assert.Equal(["_name1", "x"], TransformerLineGenerator.Names("_name1 + x;"));

        // An emoji is one character, as it was when the gate was fitted.
        Assert.Equal(2, TransformerLineGenerator.VisibleLength(" \U0001F600 x\t"));
    }

    [Fact]
    public void AGateThatDoesNotFitThisBuildIsRefused()
    {
        var model = new AssistModelCatalog(AssistModelCatalog.DefaultDirectory).Resolve(AssistModelKind.Lines, "4.0.0")!;
        var spec = model.Manifest.Lines!;
        AssistModel With(LineModelSpec changed) => model with { Manifest = model.Manifest with { Lines = changed } };
        Assert.Contains("features this WpfStudio does not compute", Assert.Throws<InvalidDataException>(() => TransformerLineGenerator.Load(With(spec with { Gate = spec.Gate! with { Features = ["something_new"] } }))).Message, StringComparison.Ordinal);
        Assert.Contains("wrong size", Assert.Throws<InvalidDataException>(() => TransformerLineGenerator.Load(With(spec with { Gate = spec.Gate! with { Long = spec.Gate.Long with { Weights = [1.0] } } }))).Message, StringComparison.Ordinal);
        Assert.Contains("no line gate", Assert.Throws<InvalidDataException>(() => TransformerLineGenerator.Load(With(spec with { Gate = null }))).Message, StringComparison.Ordinal);
        Assert.Contains("line features version 3", Assert.Throws<InvalidDataException>(() => TransformerLineGenerator.Load(With(spec with { FeatureVersion = 3 }))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateStopsWhereTheModelStopsBeingSure()
    {
        var catalog = new AssistModelCatalog(AssistModelCatalog.DefaultDirectory);
        using var gated = LineGenerator.Load(catalog.Resolve(AssistModelKind.Lines, "2.0.0")!);
        using var ungated = LineGenerator.Load(catalog.Resolve(AssistModelKind.Lines, "1.0.0")!);
        Assert.True(gated.OffersPartialLines);
        Assert.False(ungated.OffersPartialLines);
        // Nothing here to go on: the old rule still guesses, and the gate says nothing.
        const string unknown = "var zq = ";
        Assert.Equal("", gated.Generate(unknown, CodeAssistEngine.MaxLineTokens).Text);
    }

    [Fact]
    public async Task ASuggestionTooShortToHelpIsNotOffered()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true, LineSuggestions: true));
        // After a finished statement the model expects the line to end, which is nothing to suggest.
        const string finished = "public sealed class Person\n{\n    public string Name { get; set; }";
        Assert.Null(await engine.SuggestLineAsync(finished, finished.Length));
    }

    [Fact]
    public async Task ACancelledSuggestionThrowsAndTheNextOneWorks()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true, LineSuggestions: true));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.SuggestLineAsync("var x = ", 8, cancelled.Token));
        Assert.DoesNotContain("failed", engine.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheEndOfALongFileIsRead()
    {
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        string tail = "// Clear out the array\nnames.";
        string text = string.Concat(Enumerable.Repeat("var value = other.Add(1);\n", 40_000)) + tail;
        var ranked = await engine.RankMembersAsync(text, text.Length, ListMembers);
        Assert.Equal((await engine.RankMembersAsync(tail, tail.Length, ListMembers))[0].Name, ranked[0].Name);
    }

    private void CopyModels()
    {
        foreach (string file in Directory.EnumerateFiles(AssistModelCatalog.DefaultDirectory, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(_copy, Path.GetRelativePath(AssistModelCatalog.DefaultDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
