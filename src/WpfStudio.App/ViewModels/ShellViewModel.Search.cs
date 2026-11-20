using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Text;

namespace WpfStudio.App.ViewModels;

/// <summary>
/// Find in files, and the one list every search and symbol query publishes its places to: each row is the line of
/// code that was found, rows are grouped under their file, and a line above says what the list is.
/// <para>
/// The search runs as its text is typed, and again whenever one of its switches, its scope or its file lists changes. A
/// newer run replaces the one before it, so only the last thing asked for is ever listed.
/// </para>
/// </summary>
public sealed partial class ShellViewModel
{
    internal const int SearchResultLimit = 2000;
    /// <summary>How long typing has to rest before the search runs. Enter does not wait.</summary>
    internal TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    private readonly HashSet<string> _searchTypes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _searchRun;
    private Task _lastSearch = Task.CompletedTask;
    /// <summary>The list holds a text search's matches, rather than the places a symbol query found.</summary>
    private bool _resultsAreSearch;
    /// <summary>The line above the list as it was before "Searching…" took its place, to put back if that search ends with nothing to list.</summary>
    private string? _titleUnderSearch;
    private bool _refreshingScopes;
    /// <summary>The editor selection last taken as the text to find.</summary>
    private (string Path, int Start, int Length) _seededSelection;

    /// <summary>What the results are: "12 references to Count in 3 files".</summary>
    [ObservableProperty] public partial string SearchResultsTitle { get; set; } = "";
    [ObservableProperty] public partial bool SearchMatchCase { get; set; }
    [ObservableProperty] public partial bool SearchWholeWord { get; set; }
    [ObservableProperty] public partial bool SearchUseRegex { get; set; }
    /// <summary>The files to search, as <see cref="FileFilter"/> reads them: <c>*.cs, Views, src/**/*.xaml</c>. Empty searches every text file.</summary>
    [ObservableProperty] public partial string SearchInclude { get; set; } = "";
    /// <summary>The files to leave out, in the same form.</summary>
    [ObservableProperty] public partial string SearchExclude { get; set; } = "";
    /// <summary>Where to look. Null, as a list being refilled leaves it for a moment, is the entire solution.</summary>
    [ObservableProperty] public partial SearchScope? SearchScope { get; set; } = SearchScope.Solution;
    /// <summary>The scope and the two file lists are shown under the search box.</summary>
    [ObservableProperty] public partial bool ShowSearchFilters { get; set; } = true;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCancelCommand))] public partial bool IsSearching { get; private set; }
    /// <summary>The fixed scopes, then each loaded project, then a folder chosen in Solution Explorer.</summary>
    public BulkObservableCollection<SearchScope> SearchScopes { get; } = [SearchScope.Solution, SearchScope.CurrentProject, SearchScope.OpenDocuments, SearchScope.CurrentDocument];
    /// <summary>The file types the last search matched in, most matches first, each with a switch to list only it.</summary>
    public BulkObservableCollection<SearchFileType> SearchFileTypes { get; } = [];
    /// <summary>The search in progress or the last one finished, for a caller that changed an option and needs the list it leads to.</summary>
    internal Task SearchCompletion => _lastSearch;

    partial void OnSearchQueryChanged(string value) => QueueSearch();
    partial void OnSearchMatchCaseChanged(bool value) => QueueSearch();
    partial void OnSearchWholeWordChanged(bool value) => QueueSearch();
    partial void OnSearchUseRegexChanged(bool value) => QueueSearch();
    partial void OnSearchIncludeChanged(string value) => QueueSearch();
    partial void OnSearchExcludeChanged(string value) => QueueSearch();
    partial void OnSearchScopeChanged(SearchScope? value) { if (!_refreshingScopes) QueueSearch(); }
    private void QueueSearch() { if (!_disposed) _lastSearch = RunSearchAsync(SearchDelay, reveal: false); }

    /// <summary>Replaces the results list with what a symbol query found. <paramref name="title"/> says what they are; the count of files is added to it.</summary>
    private void ShowResults(string title, IEnumerable<NavigationResult> results, bool reveal = true)
    {
        // A search still under way would put its matches over these when it finished.
        _searchRun?.Cancel();
        _resultsAreSearch = false;
        SearchFileTypes.Clear();
        PublishResults(title, results, "", reveal);
    }

    private void PublishResults(string title, IEnumerable<NavigationResult> results, string suffix, bool reveal)
    {
        SearchResults.ReplaceAll(results.Select(result => result.Folder.Length > 0 ? result : result with { Folder = RelativeDirectory(result.Path) }));
        int files = SearchResults.Select(result => result.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SearchResultsTitle = (SearchResults.Count == 0 ? title : title + (files == 1 ? " in 1 file" : $" in {files} files")) + suffix;
        _titleUnderSearch = null;
        if (reveal) ToolRequested?.Invoke(Workbenches.Search);
    }

    /// <summary>Empties the list and forgets what filled it, as when the solution closes.</summary>
    private void ClearSearchResults()
    {
        _searchRun?.Cancel();
        _resultsAreSearch = false;
        SearchResults.Clear(); SearchFileTypes.Clear(); SearchResultsTitle = "";
        _titleUnderSearch = null;
    }

    /// <summary>Says above the list why a search could not run. The list itself stays as it is.</summary>
    private void ShowSearchProblem(string problem) { SearchResultsTitle = Status = problem; _titleUnderSearch = null; }
    private void EndSearchingTitle() { if (_titleUnderSearch is { } title) { SearchResultsTitle = title; _titleUnderSearch = null; } }

    /// <summary>A row for a place the language worker found: the line of code it is on, with the found name marked.</summary>
    private static NavigationResult ResultFor(SourceLocation location, string fallback)
    {
        var result = new NavigationResult(location.Path, location.Line, location.Column, location.DisplayText ?? fallback,
            location.Start, location.ExpectedTextHash, location.ProjectPath, location.ProjectName);
        return location.LineText is { } line && !string.IsNullOrWhiteSpace(line) ? result.WithLine(line, location.Column - 1, location.Length) : result;
    }

    private static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    // ------------------------------------------------------------------ Opening
    /// <summary>
    /// Shows the Search pane with the caret in its box. Text selected in the editor, when it is part of one line, becomes what
    /// to look for, and is searched for at once.
    /// </summary>
    [RelayCommand] private void FindInFiles()
    {
        if (ActiveWorkbench == null && ActiveDocument is { SelectionLength: > 0 and <= 200 } editor
            && editor.SelectedText is var selected && selected.AsSpan().IndexOfAny('\r', '\n') < 0 && !string.IsNullOrWhiteSpace(selected)
            // A selection is taken once. Pressed again with the caret in the box, the shortcut must not put it back over what was typed since.
            && _seededSelection != (editor.State.Path, editor.SelectionStart, editor.SelectionLength))
        {
            _seededSelection = (editor.State.Path, editor.SelectionStart, editor.SelectionLength);
            SearchQuery = selected;
        }
        ToolRequested?.Invoke(Workbenches.Search); IsSearchFocusRequested = true;
    }

    /// <summary>Find in files, looking in what is selected in Solution Explorer: the solution, a project or a folder.</summary>
    [RelayCommand] private void FindInSelected()
    {
        if (Explorer.SelectedNode is not { CanSearch: true } node) { Status = "Select the solution, a project or a folder to search in"; return; }
        var scope = node.Kind switch
        {
            ExplorerNodeKind.Solution => SearchScope.Solution,
            ExplorerNodeKind.Project when Projects.FirstOrDefault(project => project.ProjectPath.Equals(node.ProjectPath, IgnoreCase)) is { } project => SearchScope.ForProject(project),
            _ => SearchScope.ForFolder(node.Path, WorkspaceRoot.Length > 0 ? Path.GetRelativePath(WorkspaceRoot, node.Path) : node.Path),
        };
        RefreshSearchScopes(scope);
        ShowSearchFilters = true;
        ToolRequested?.Invoke(Workbenches.Search); IsSearchFocusRequested = true;
    }

    /// <summary>
    /// Lists the scopes again after the projects changed, keeping the one in use when it is still there. <paramref name="select"/>
    /// is a scope to add and use, such as a folder; the list holds one folder at a time, the last one chosen.
    /// </summary>
    private void RefreshSearchScopes(SearchScope? select = null)
    {
        var current = select ?? SearchScope ?? SearchScope.Solution;
        var scopes = new List<SearchScope> { SearchScope.Solution, SearchScope.CurrentProject, SearchScope.OpenDocuments, SearchScope.CurrentDocument };
        scopes.AddRange(Projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).Select(SearchScope.ForProject));
        // A folder stays listed while the workspace has files in it, which another workspace opened since does not.
        if (current is { Kind: SearchScopeKind.Folder, Path: { } folder } && _allFiles.Any(path => SearchScopeFiles.IsUnder(path, folder))) scopes.Add(current);
        if (!scopes.Contains(current)) current = SearchScope.Solution;
        bool changed = current != SearchScope;
        // The list is refilled around a scope that is already in it, so the box showing it has nothing to deselect.
        _refreshingScopes = true;
        try { SearchScope = current; SearchScopes.ReplaceAll(scopes); SearchScope = current; }
        finally { _refreshingScopes = false; }
        // A scope that a reload took away is not a reason to put a search over the places a symbol query listed.
        if (changed && (select != null || _resultsAreSearch)) QueueSearch();
    }

    // ------------------------------------------------------------------ Running
    /// <summary>Searches now, without waiting for typing to rest, and shows the pane: Enter in the box, or the button.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)] private Task SearchAsync() => _lastSearch = RunSearchAsync(TimeSpan.Zero, reveal: true);

    [RelayCommand(CanExecute = nameof(IsSearching))] private void SearchCancel()
    {
        _searchRun?.Cancel();
        EndSearchingTitle();
        Status = "Search cancelled";
    }

    private void FileTypeToggled(SearchFileType type)
    {
        if (type.IsSelected) _searchTypes.Add(type.Extension); else _searchTypes.Remove(type.Extension);
        // Asked for with a click, not typed: there is nothing more to wait for.
        _lastSearch = RunSearchAsync(TimeSpan.Zero, reveal: false);
    }

    private async Task RunSearchAsync(TimeSpan delay, bool reveal)
    {
        _searchRun?.Cancel();
        var run = _searchRun = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = run.Token;
        try
        {
            var query = SearchQuery;
            if (string.IsNullOrWhiteSpace(query))
            {
                // An emptied box takes its matches with it, but not a list that something else filled, such as a symbol's references.
                if (_resultsAreSearch) ClearSearchResults(); else EndSearchingTitle();
                return;
            }
            if (reveal) ToolRequested?.Invoke(Workbenches.Search);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            Regex pattern;
            try { pattern = TextSearch.Pattern(query, SearchMatchCase, SearchWholeWord, SearchUseRegex); }
            catch (ArgumentException exception) { ShowSearchProblem("The search pattern is not a valid regular expression: " + exception.Message); return; }
            if (ResolveSearchScope(out var problem) is not { } scope) { ShowSearchProblem(problem); return; }
            var filter = FileFilter.Parse(SearchInclude, SearchExclude);
            var buffers = _store.Documents.ToDictionary(document => document.Path, document => document.Content, StringComparer.OrdinalIgnoreCase);
            var listed = _searchTypes.Count > 0 ? new HashSet<string>(_searchTypes, StringComparer.Ordinal) : null;
            string root = WorkspaceRoot;
            IsSearching = true;
            // Over matches already listed the line stays, so that it does not flicker between every two letters typed.
            if (!_resultsAreSearch || SearchResults.Count == 0) { _titleUnderSearch ??= SearchResultsTitle; SearchResultsTitle = $"Searching for “{query}”…"; }
            var (candidates, result) = await Task.Run(() =>
            {
                var inScope = scope.Files();
                var files = inScope.Where(path => filter.Matches(root.Length > 0 ? Path.GetRelativePath(root, path) : path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                return (inScope.Count, FileSearch.Run(new(pattern, files) { OpenTexts = buffers, ListedExtensions = listed, Limit = SearchResultLimit }, token));
            }, token);
            token.ThrowIfCancellationRequested();
            ShowSearchResult(query, scope.Description, result, candidates, filter.IsEmpty);
        }
        // Replaced by a newer search, or cancelled: either way this one has nothing to say.
        catch (OperationCanceledException) { }
        catch (Exception exception) { EndSearchingTitle(); _logger.LogError(exception, "Search failed"); Status = exception.Message; AppendOutput(exception.ToString()); }
        finally
        {
            if (ReferenceEquals(_searchRun, run)) { _searchRun = null; IsSearching = false; }
            run.Dispose();
        }
    }

    private void ShowSearchResult(string query, string scope, FileSearchResult result, int candidates, bool unfiltered)
    {
        _resultsAreSearch = true;
        var types = result.Extensions.Select(count => new SearchFileType(count.Extension, count.Files, count.Matches, result.Truncated, _searchTypes.Contains(count.Extension), FileTypeToggled)).ToList();
        // A chosen type the search found nothing in stays listed, so that it can be let go of.
        types.AddRange(_searchTypes.Where(chosen => result.Extensions.All(count => count.Extension != chosen)).Order(StringComparer.Ordinal).Select(chosen => new SearchFileType(chosen, 0, 0, false, true, FileTypeToggled)));
        SearchFileTypes.ReplaceAll(types);

        var rows = result.Matches.Select(SearchRow).ToArray();
        string only = _searchTypes.Count > 0 ? " · only " + string.Join(", ", types.Where(type => type.IsSelected).Select(type => type.Label)) : "";
        string where = (scope.Length > 0 ? " · " + scope : "") + only;
        string searched = result.FilesSearched == 1 ? "1 file" : $"{result.FilesSearched:N0} files";
        string title = rows.Length > 0 ? Count(rows.Length, "match", "matches") + $" for “{query}”" + (result.Truncated ? $" (the first {SearchResultLimit:N0})" : "")
            : result.FilesSearched == 0 && candidates > 0 && !unfiltered ? $"The files to include and exclude leave none of the {candidates:N0} to search"
            : result.FilesSearched == 0 ? "There are no text files to search"
            : $"No match for “{query}” in {searched}";
        PublishResults(title, rows, where, reveal: false);
        Status = SearchResultsTitle
            + (result.TimedOutIn is { } slow ? $" · stopped at {Path.GetFileName(slow)}, where the regular expression took too long" : "")
            + (result.Skipped > 0 ? $" · {Count(result.Skipped, "file", "files")} over {FileSearch.MaxFileBytes / (1024 * 1024)} MB not searched" : "");
        if (result.TimedOutIn != null) SearchResultsTitle += " · incomplete: the regular expression took too long";
    }

    /// <summary>The files a scope covers, read when the search runs, and how the results line names the scope. The solution needs no naming.</summary>
    private sealed record ResolvedSearchScope(string Description, Func<IReadOnlyList<string>> Files);

    private ResolvedSearchScope? ResolveSearchScope(out string problem)
    {
        problem = "";
        var all = _allFiles;
        var projects = Projects.ToArray();
        var scope = SearchScope ?? SearchScope.Solution;
        switch (scope.Kind)
        {
            case SearchScopeKind.OpenDocuments:
                var open = Documents.Select(document => document.State.Path).ToArray();
                if (open.Length == 0) { problem = "No documents are open to search"; return null; }
                return new("open documents", () => open);
            case SearchScopeKind.CurrentDocument:
                if (ActiveDocument is not { } active) { problem = "No document is open to search"; return null; }
                return new(active.State.Name, () => [active.State.Path]);
            case SearchScopeKind.CurrentProject:
                var current = (ActiveDocument is { } document ? SearchScopeFiles.ProjectOf(document.State.Path, projects) : null)
                    ?? projects.FirstOrDefault(project => project.ProjectPath.Equals(Explorer.SelectedNode?.ProjectPath, IgnoreCase)) ?? StartupProject;
                if (current == null) { problem = "No project is loaded to search"; return null; }
                return new("project " + current.Name, () => SearchScopeFiles.ProjectFiles(current, projects, all));
            case SearchScopeKind.Project:
                if (projects.FirstOrDefault(project => project.ProjectPath.Equals(scope.Path, IgnoreCase)) is not { } named) { problem = $"The project {scope.Label} is no longer loaded"; return null; }
                return new("project " + named.Name, () => SearchScopeFiles.ProjectFiles(named, projects, all));
            case SearchScopeKind.Folder when scope.Path is { } folder:
                return new(scope.Label.Replace("Folder: ", "folder ", StringComparison.Ordinal), () => SearchScopeFiles.FolderFiles(folder, all));
            default:
                // With no solution open there are only the open documents to look in.
                if (Workspace == null && all.Length == 0 && Documents.Count > 0) goto case SearchScopeKind.OpenDocuments;
                return new("", () => all);
        }
    }

    /// <summary>The row for a line a search matched: the line without its indentation, with what was found marked.</summary>
    internal static NavigationResult SearchRow(TextMatch match) =>
        new NavigationResult(match.Path, match.Line, match.Column, match.LineText, match.Start).WithLine(match.LineText, match.Column - 1, match.Length);
}
