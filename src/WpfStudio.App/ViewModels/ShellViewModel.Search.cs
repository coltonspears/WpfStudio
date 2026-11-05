using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

/// <summary>
/// Find in workspace, and the one list every search and symbol query publishes its places to: each row is the line of
/// code that was found, rows are grouped under their file, and a line above says what the list is.
/// </summary>
public sealed partial class ShellViewModel
{
    internal const int SearchResultLimit = 2000;
    private static readonly string[] SearchedExtensions = [".cs", ".xaml", ".json", ".xml", ".sql", ".csproj", ".props", ".targets", ".md"];

    /// <summary>What the results are: "12 references to Count in 3 files".</summary>
    [ObservableProperty] public partial string SearchResultsTitle { get; set; } = "";
    [ObservableProperty] public partial bool SearchMatchCase { get; set; }
    [ObservableProperty] public partial bool SearchWholeWord { get; set; }
    [ObservableProperty] public partial bool SearchUseRegex { get; set; }

    /// <summary>Replaces the results list. <paramref name="title"/> says what they are; the count of files is added to it.</summary>
    private void ShowResults(string title, IEnumerable<NavigationResult> results, bool reveal = true)
    {
        SearchResults.Clear();
        foreach (var result in results) SearchResults.Add(result.Folder.Length > 0 ? result : result with { Folder = RelativeDirectory(result.Path) });
        int files = SearchResults.Select(result => result.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SearchResultsTitle = SearchResults.Count == 0 ? title : title + (files == 1 ? " in 1 file" : $" in {files} files");
        if (reveal) ToolRequested?.Invoke(Workbenches.Search);
    }

    /// <summary>A row for a place the language worker found: the line of code it is on, with the found name marked.</summary>
    private static NavigationResult ResultFor(SourceLocation location, string fallback)
    {
        var result = new NavigationResult(location.Path, location.Line, location.Column, location.DisplayText ?? fallback,
            location.Start, location.ExpectedTextHash, location.ProjectPath, location.ProjectName);
        return location.LineText is { } line && !string.IsNullOrWhiteSpace(line) ? result.WithLine(line, location.Column - 1, location.Length) : result;
    }

    private static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    [RelayCommand(IncludeCancelCommand = true)] private async Task SearchAsync(CancellationToken token)
    {
        await GuardAsync(async () =>
        {
            var query = SearchQuery; if (string.IsNullOrWhiteSpace(query)) return;
            Regex pattern;
            try { pattern = SearchPattern(query, SearchMatchCase, SearchWholeWord, SearchUseRegex); }
            catch (ArgumentException exception) { Status = "The search pattern is not a valid regular expression: " + exception.Message; return; }
            SearchResults.Clear(); SearchResultsTitle = $"Searching for “{query}”…";
            ToolRequested?.Invoke(Workbenches.Search);
            var files = _allFiles.Where(path => SearchedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
            var buffers = _store.Documents.ToDictionary(document => document.Path, document => document.Content, StringComparer.OrdinalIgnoreCase);
            var results = await Task.Run(async () =>
            {
                var found = new List<NavigationResult>();
                foreach (var path in files)
                {
                    token.ThrowIfCancellationRequested();
                    string text;
                    try { text = buffers.GetValueOrDefault(path) ?? await File.ReadAllTextAsync(path, token); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                    FindIn(path, text, pattern, found);
                    if (found.Count >= SearchResultLimit) break;
                }
                return found;
            }, token);
            ShowResults(Count(results.Count, "match", "matches") + $" for “{query}”" + (results.Count >= SearchResultLimit ? " (the first " + SearchResultLimit + ")" : ""), results);
            Status = results.Count == 0 ? $"No match for “{query}” in {files.Length} files" : SearchResultsTitle;
        });
    }

    /// <summary>The expression a search runs: the text as typed unless it is a regular expression, optionally as a whole word.</summary>
    internal static Regex SearchPattern(string query, bool matchCase, bool wholeWord, bool useRegex)
    {
        var expression = useRegex ? query : Regex.Escape(query);
        // \b only matches beside a word character, so it is added only on the sides where the text has one.
        if (wholeWord) expression = (WordStarts(query) || useRegex ? @"\b" : "") + "(?:" + expression + ")" + (WordEnds(query) || useRegex ? @"\b" : "");
        return new Regex(expression, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(2));
        static bool WordStarts(string text) => text.Length > 0 && (char.IsLetterOrDigit(text[0]) || text[0] == '_');
        static bool WordEnds(string text) => text.Length > 0 && (char.IsLetterOrDigit(text[^1]) || text[^1] == '_');
    }

    /// <summary>One result for each line of a file that matches, marking the line's first match.</summary>
    internal static void FindIn(string path, string text, Regex pattern, List<NavigationResult> found)
    {
        int offset = 0, number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            var content = line.EndsWith('\r') ? line[..^1] : line;
            Match match;
            try { match = pattern.Match(content); }
            catch (RegexMatchTimeoutException) { return; } // An expression that cannot finish on this file's lines will not on the next one either.
            if (match.Success && match.Length > 0)
            {
                found.Add(new NavigationResult(path, number, match.Index + 1, content, offset + match.Index).WithLine(content, match.Index, match.Length));
                if (found.Count >= SearchResultLimit) return;
            }
            offset += line.Length + 1;
        }
    }
}
