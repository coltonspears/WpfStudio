using System.Text;
using System.Text.RegularExpressions;
using WpfStudio.Contracts;
using WpfStudio.Core.Text;

namespace WpfStudio.Core.Tests;

/// <summary>Find in files: what a line matches, which files the include and exclude lists name, and how many files are searched at once.</summary>
public sealed class FileSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WpfStudio.Core.Tests", "search-" + Guid.NewGuid().ToString("N"));
    public FileSearchTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    private static FileSearchResult Run(string query, IReadOnlyList<string> files, Func<FileSearchRequest, FileSearchRequest>? configure = null)
    {
        var request = new FileSearchRequest(TextSearch.Pattern(query, false, false, false), files);
        return FileSearch.Run(configure?.Invoke(request) ?? request);
    }

    // ------------------------------------------------------------------ Lines
    [Fact]
    public void ALineIsListedOnceAtItsFirstMatchWithTheFilesOwnOffsets()
    {
        var found = new List<TextMatch>();
        const string text = "class Order\r\n{\r\n    public int Count { get; set; } // count\r\n\tint count;\r\n}\r\n";
        int lines = TextSearch.FindLines("Order.cs", text, TextSearch.Pattern("count", false, false, false), found);
        Assert.Equal(2, lines);
        Assert.Equal([3, 4], found.Select(match => match.Line));
        Assert.Equal((16, "class Order\r\n{\r\n    public int ".Length, 5), (found[0].Column, found[0].Start, found[0].Length));
        Assert.Equal("    public int Count { get; set; } // count", found[0].LineText);
        Assert.Equal("\tint count;", found[1].LineText);
        Assert.Equal("count", text.Substring(found[1].Start, found[1].Length));
    }

    [Fact]
    public void TheSwitchesNarrowWhatALineMatches()
    {
        static int Lines(string text, string query, bool matchCase = false, bool wholeWord = false, bool useRegex = false) =>
            TextSearch.FindLines("a.cs", text, TextSearch.Pattern(query, matchCase, wholeWord, useRegex), null);
        const string text = "count Count recount count2 _count\ncounts\n";
        Assert.Equal(2, Lines(text, "count"));
        Assert.Equal(1, Lines(text, "count", wholeWord: true));
        Assert.Equal(0, Lines(text, "COUNT", matchCase: true));
        Assert.Equal(1, Lines("a => b\nb =>c\n", "=> b", wholeWord: true));
        Assert.Equal(1, Lines("a.b\naxb\n", "a.b"));
        Assert.Equal(2, Lines("a.b\naxb\n", "a.b", useRegex: true));
        // Each line is searched on its own, so an expression's ^ and $ are a line's start and end.
        Assert.Equal(2, Lines("using A;\n  using B;\nusing C;\n", "^using", useRegex: true));
        Assert.Equal(1, Lines("a;\r\nb\r\n", ";$", useRegex: true));
        Assert.ThrowsAny<ArgumentException>(() => TextSearch.Pattern("(", false, false, useRegex: true));
    }

    [Fact]
    public void AnExpressionThatCanMatchNothingListsOnlyWhatItFound()
    {
        var found = new List<TextMatch>();
        Assert.Equal(1, TextSearch.FindLines("a.cs", "abc\nabxx\n", TextSearch.Pattern("x*", false, false, true), found));
        Assert.Equal((2, 3, 2), (found[0].Line, found[0].Column, found[0].Length));
    }

    [Fact]
    public void LinesAreCountedPastTheLimitThatStopsListingThem()
    {
        var found = new List<TextMatch>();
        Assert.Equal(50, TextSearch.FindLines("a.cs", string.Concat(Enumerable.Repeat("x\n", 50)), new Regex("x"), found, limit: 20));
        Assert.Equal(Enumerable.Range(1, 20), found.Select(match => match.Line));
    }

    // ------------------------------------------------------------------ Files to include and exclude
    [Theory]
    [InlineData("*.cs", "src/App/Order.cs", true)]
    [InlineData("*.cs", "src/App/Order.xaml", false)]
    [InlineData("*.cs", "src/App/Order.xaml.cs", true)]
    [InlineData("*.CS", @"src\App\order.cs", true)]
    [InlineData(".cs", "Order.cs", true)]
    [InlineData(".cs", "src/Order.csproj", false)]
    [InlineData(".github", ".github/workflows/build.yml", true)]
    [InlineData(".editorconfig", "src/.editorconfig", true)]
    [InlineData("*.{cs,xaml}", "Views/Main.xaml", true)]
    [InlineData("*.{cs,xaml}", "Views/Main.json", false)]
    [InlineData("Views", "src/App/Views/Main.xaml", true)]
    [InlineData("Views", "src/App/ViewsOld/Main.xaml", false)]
    [InlineData("Views/", "Views/Main.xaml", true)]
    [InlineData("*Tests", "tests/App.Tests/OrderTests.cs", true)]
    [InlineData("*Tests", "src/App/Order.cs", false)]
    [InlineData("Order.cs", "src/App/Order.cs", true)]
    [InlineData("Order?.cs", "src/App/Order2.cs", true)]
    [InlineData("App/Views", "src/App/Views/Main.xaml", true)]
    [InlineData("App/Views", "src/MyApp/Views/Main.xaml", false)]
    [InlineData("Views/*.xaml", "src/App/Views/Main.xaml", true)]
    [InlineData("Views/*.xaml", "src/App/Views/Dialogs/Ask.xaml", false)]
    [InlineData("Views/**/*.xaml", "src/App/Views/Dialogs/Ask.xaml", true)]
    [InlineData("Views/**/*.xaml", "src/App/Views/Main.xaml", true)]
    [InlineData("**/*.xaml", "Main.xaml", true)]
    [InlineData("src/**", "src/App/Order.cs", true)]
    [InlineData("./src", "src/App/Order.cs", true)]
    [InlineData("./src", "tests/src/Order.cs", false)]
    [InlineData("/src/*/Order.cs", "src/App/Order.cs", true)]
    [InlineData("./", "anything/at/all.txt", true)]
    [InlineData("../Shared", "../Shared/Common.cs", true)]
    [InlineData("a{b.cs", "a{b.cs", true)]
    public void ATermNamesAnExtensionANameOrAPath(string include, string path, bool matches) =>
        Assert.Equal(matches, FileFilter.Parse(include, null).Matches(path));

    [Fact]
    public void AFileIsReadWhenItMatchesAnIncludedTermAndNoExcludedOne()
    {
        var filter = FileFilter.Parse("*.cs; *.xaml", "*.Designer.cs, Migrations, obj");
        Assert.True(filter.Matches("src/App/Order.cs"));
        Assert.True(filter.Matches(@"src\App\Main.xaml"));
        Assert.False(filter.Matches("src/App/appsettings.json"));
        Assert.False(filter.Matches("src/App/Resources.Designer.cs"));
        Assert.False(filter.Matches("src/Data/Migrations/Initial.cs"));

        // With nothing to include, everything is, less what is excluded.
        Assert.True(FileFilter.Parse("", "*.json").Matches("README.md"));
        Assert.False(FileFilter.Parse(null, "*.json").Matches("a/b.json"));
        Assert.Same(FileFilter.All, FileFilter.Parse(" , ; ", "  "));
        Assert.True(FileFilter.All.Matches("anything"));
    }

    [Fact]
    public void AListIsSplitAtCommasAndSemicolonsOutsideBraces()
    {
        Assert.Equal(["*.cs", "*.{xaml,axaml}", "src/My Folder"], FileFilter.Terms(" *.cs ,, *.{xaml,axaml} ; src/My Folder "));
        Assert.Empty(FileFilter.Terms("  "));
        // A brace that is never closed does not swallow the terms after it.
        Assert.Equal(["a{b", "*.cs"], FileFilter.Terms("a{b, *.cs"));
    }

    // ------------------------------------------------------------------ Many files
    [Fact]
    public void MatchesAreListedInTheFilesOrderWhateverOrderTheyWereReadIn()
    {
        var files = Enumerable.Range(0, 200).Select(index => Write($"dir{index % 7}/File{index:000}.cs", index % 3 == 0 ? $"// nothing here\nint needle{index};\n" : "class Other { }\n")).ToArray();
        var result = Run("needle", files);
        Assert.Equal(Enumerable.Range(0, 200).Where(index => index % 3 == 0).Select(index => files[index]), result.Matches.Select(match => match.Path));
        Assert.All(result.Matches, match => Assert.Equal((2, 5), (match.Line, match.Column)));
        Assert.Equal(200, result.FilesSearched);
        Assert.False(result.Truncated);
        Assert.Equal([new ExtensionCount(".cs", 67, 67)], result.Extensions);
    }

    [Fact]
    public void AnOpenDocumentIsSearchedAsItStandsInTheEditor()
    {
        var saved = Write("Saved.cs", "int onDisk;\n");
        var unsaved = Path.Combine(_root, "Untitled.cs");
        var result = Run("typed", [saved, unsaved], request => request with
        {
            OpenTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [saved] = "int typed;\n", [unsaved] = "// typed here\n" },
        });
        Assert.Equal([saved, unsaved], result.Matches.Select(match => match.Path));
        Assert.Empty(Run("onDisk", [saved], request => request with { OpenTexts = new Dictionary<string, string> { [saved] = "int typed;\n" } }).Matches);
    }

    [Fact]
    public void FilesThatAreNotTextOrCannotBeReadArePassedOver()
    {
        var text = Write("Notes.txt", "needle\n");
        var binary = Path.Combine(_root, "Data.blob");
        File.WriteAllBytes(binary, [.. Encoding.ASCII.GetBytes("needle"), 0, 1, 2, .. Encoding.ASCII.GetBytes("needle")]);
        var image = Write("Logo.png", "needle in a file whose extension says it is not text\n");
        var wide = Path.Combine(_root, "Wide.txt");
        File.WriteAllText(wide, "a wide needle\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        var legacy = Path.Combine(_root, "Legacy.txt");
        File.WriteAllBytes(legacy, [.. Encoding.ASCII.GetBytes("caf"), 0xE9, .. Encoding.ASCII.GetBytes(" needle\n")]);
        var huge = Path.Combine(_root, "Huge.log");
        using (var stream = File.Create(huge)) stream.SetLength(FileSearch.MaxFileBytes + 1);
        var missing = Path.Combine(_root, "Deleted.cs");

        var result = Run("needle", [text, binary, image, wide, legacy, huge, missing]);
        Assert.Equal([text, wide, legacy], result.Matches.Select(match => match.Path));
        Assert.Equal(3, result.FilesSearched);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(8, result.Matches[1].Column);
        // Bytes that are not UTF-8 are read in a code page, as the editor reads them, so the line is listed as the editor shows it.
        Assert.Equal(6, result.Matches[2].Column);
        Assert.DoesNotContain('\uFFFD', result.Matches[2].LineText);
    }

    [Fact]
    public void TheFileTypesAreCountedAndTheChosenOnesListed()
    {
        string[] files = [Write("A.cs", "needle\nneedle\n"), Write("B.xaml", "<needle/>\n"), Write("C.cs", "needle\n"), Write("D.json", "{}\n"), Write("LICENSE", "needle\n")];
        var all = Run("needle", files);
        Assert.Equal([new(".cs", 2, 3), new("", 1, 1), new(".xaml", 1, 1)], all.Extensions);
        Assert.Equal(5, all.Matches.Count);

        // The other types are still counted, so the list of types does not shrink to the one chosen.
        var narrowed = Run("needle", files, request => request with { ListedExtensions = new HashSet<string> { ".xaml", "" } });
        Assert.Equal([files[1], files[4]], narrowed.Matches.Select(match => match.Path));
        Assert.Equal(all.Extensions, narrowed.Extensions);
        Assert.Equal(all.Matches, Run("needle", files, request => request with { ListedExtensions = new HashSet<string>() }).Matches);
    }

    [Fact]
    public void ASearchStopsAtItsLimitWithTheFirstMatchesInOrder()
    {
        var files = Enumerable.Range(0, 150).Select(index => Write($"F{index:000}.cs", "x\nx\nx\n")).ToArray();
        var cut = Run("x", files, request => request with { Limit = 100 });
        Assert.True(cut.Truncated);
        Assert.Equal(100, cut.Matches.Count);
        Assert.Equal(Enumerable.Range(0, 100).Select(index => (files[index / 3], index % 3 + 1)), cut.Matches.Select(match => (match.Path, match.Line)));
        // Counted as far as it read: the file the limit fell in is the last.
        Assert.Equal([new ExtensionCount(".cs", 34, 102)], cut.Extensions);

        // Reaching the limit with nothing left unread is not being cut off.
        var exact = Run("x", files, request => request with { Limit = 450 });
        Assert.False(exact.Truncated);
        Assert.Equal(450, exact.Matches.Count);
        Assert.True(Run("x", [Write("One.cs", "x\nx\nx\n")], request => request with { Limit = 2 }).Truncated);

        // A limit on what is listed is not reached by matches in types that are not.
        var files2 = files.Append(Write("Z.xaml", "x\n")).ToArray();
        var listed = Run("x", files2, request => request with { Limit = 100, ListedExtensions = new HashSet<string> { ".xaml" } });
        Assert.False(listed.Truncated);
        Assert.Equal(files2[^1], Assert.Single(listed.Matches).Path);
        Assert.Equal(450, listed.Extensions.Single(count => count.Extension == ".cs").Matches);
    }

    [Fact]
    public void AnExpressionThatCannotFinishStopsTheSearchAndSaysWhere()
    {
        string[] files = [Write("A.cs", "aaa\n"), Write("B.cs", new string('a', 40) + "!\n"), Write("C.cs", "aaa\n")];
        var result = FileSearch.Run(new(new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(40)), files));
        Assert.Equal(files[1], result.TimedOutIn);
        Assert.Equal([files[0]], result.Matches.Select(match => match.Path));
    }

    [Fact]
    public void ACancelledSearchEndsWithoutAResult()
    {
        var files = Enumerable.Range(0, 10).Select(index => Write($"F{index}.cs", "x\n")).ToArray();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FileSearch.Run(new(new Regex("x"), files), cancelled.Token));
        Assert.Empty(FileSearch.Run(new(new Regex("x"), [])).Matches);
    }

    // ------------------------------------------------------------------ Scopes
    private static WorkspaceProject Project(string path, params string[] files) =>
        new(path, Path.GetFileNameWithoutExtension(path), path, "net10.0", null, false, files.Select(file => new WorkspaceFile(file, Path.GetFileName(file), "Compile", file.Contains(".g.", StringComparison.Ordinal))).ToArray());
    private static string P(string path) => Path.Combine(Path.GetTempPath(), "ws", path.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void AFileBelongsToTheProjectThatListsItOrTheNearestOneAboveIt()
    {
        var app = Project(P("src/App/App.csproj"), P("src/App/Order.cs"), P("src/Shared/Common.cs"));
        var tests = Project(P("src/App/Tests/Tests.csproj"), P("src/App/Tests/OrderTests.cs"));
        var shared = Project(P("src/Shared/Shared.csproj"), P("src/Shared/Common.cs"));
        WorkspaceProject[] projects = [app, tests, shared];
        Assert.Same(app, SearchScopeFiles.ProjectOf(P("src/App/Order.cs"), projects));
        Assert.Same(app, SearchScopeFiles.ProjectOf(P("src/App/App.csproj"), projects));
        Assert.Same(app, SearchScopeFiles.ProjectOf(P("src/App/readme.md"), projects));
        Assert.Same(tests, SearchScopeFiles.ProjectOf(P("src/App/Tests/Data/sample.json"), projects));
        // A file two projects compile is the one's whose folder it is in.
        Assert.Same(shared, SearchScopeFiles.ProjectOf(P("src/Shared/Common.cs"), projects));
        Assert.Null(SearchScopeFiles.ProjectOf(P("docs/guide.md"), projects));
        // Listed by one project from outside every project's folder.
        var linked = Project(P("src/Tool/Tool.csproj"), P("build/Version.cs"));
        Assert.Same(linked, SearchScopeFiles.ProjectOf(P("build/Version.cs"), [app, linked]));
    }

    [Fact]
    public void AProjectsSearchCoversItsOwnFilesAndTheLooseFilesInItsFolder()
    {
        var app = Project(P("src/App/App.csproj"), P("src/App/Order.cs"), P("src/App/obj/Order.g.cs"), P("build/Version.cs"));
        var tests = Project(P("src/App/Tests/Tests.csproj"), P("src/App/Tests/OrderTests.cs"));
        var other = Project(P("src/AppTools/AppTools.csproj"), P("src/AppTools/Tool.cs"), P("src/App/Linked.cs"));
        WorkspaceProject[] projects = [app, tests, other];
        string[] workspace = [P("src/App/Order.cs"), P("src/App/App.csproj"), P("src/App/readme.md"), P("src/App/Assets/logo.svg"), P("src/App/Linked.cs"),
            P("src/App/Tests/OrderTests.cs"), P("src/App/Tests/notes.txt"), P("src/AppTools/Tool.cs"), P("README.md")];

        var files = SearchScopeFiles.ProjectFiles(app, projects, workspace);
        Assert.Equal(new[] { P("src/App/Order.cs"), P("build/Version.cs"), P("src/App/App.csproj"), P("src/App/readme.md"), P("src/App/Assets/logo.svg") }.Order(), files.Order());
        Assert.Equal(new[] { P("src/App/Tests/OrderTests.cs"), P("src/App/Tests/Tests.csproj"), P("src/App/Tests/notes.txt") }.Order(), SearchScopeFiles.ProjectFiles(tests, projects, workspace).Order());
    }

    [Fact]
    public void AFolderHoldsWhatIsBelowItAndNotItsNamesakes()
    {
        string[] workspace = [P("src/App/Order.cs"), P("src/App/Views/Main.xaml"), P("src/AppTools/Tool.cs"), P("src/App")];
        Assert.Equal([workspace[0], workspace[1]], SearchScopeFiles.FolderFiles(P("src/App"), workspace));
        Assert.Equal([workspace[0], workspace[1]], SearchScopeFiles.FolderFiles(P("src/App") + Path.DirectorySeparatorChar, workspace));
        Assert.True(SearchScopeFiles.IsUnder(@"C:\work\App\a.cs", @"c:\Work\app"));
        Assert.True(SearchScopeFiles.IsUnder(@"C:\a.cs", @"C:\"));
        Assert.False(SearchScopeFiles.IsUnder(P("src/App"), ""));
    }
}
