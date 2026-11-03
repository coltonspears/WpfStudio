using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Workbench;

/// <summary>What the command palette needs beyond <see cref="IWorkbenchHost"/>: the command catalog, file entries and symbol search.</summary>
public interface ICommandPaletteHost : IWorkbenchHost
{
    /// <summary>Every shell command, grouped the same way as the menus.</summary>
    IEnumerable<PaletteEntry> CommandEntries();
    /// <summary>An entry that opens <paramref name="path"/>, with its location as the detail.</summary>
    PaletteEntry FileEntry(string path);
    /// <summary>The workspace folder, or empty when none is open.</summary>
    string WorkspaceRoot { get; }
    bool IsSymbolSearchAvailable { get; }
    Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(string query, CancellationToken token);
}

/// <summary>
/// The unified palette: files by name, <c>#</c> for types and members across the solution, <c>&gt;</c> for commands.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject, IDisposable
{
    private const int MaximumResults = 80;
    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;
    private readonly ICommandPaletteHost _host;
    private CancellationTokenSource? _symbolSearch;

    public CommandPaletteViewModel(ICommandPaletteHost host) => _host = host;

    public ObservableCollection<PaletteEntry> Results { get; } = [];
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsCommandMode { get; set; }
    [ObservableProperty] public partial PaletteEntry? Selected { get; set; }
    public string Placeholder => IsCommandMode ? "Type a command name" : "Go to file by name — # for a type or member, > to run a command";
    partial void OnQueryChanged(string value) => Refresh();
    partial void OnIsCommandModeChanged(bool value) => OnPropertyChanged(nameof(Placeholder));

    public void OpenFiles() => Open(commandMode: false, "");
    /// <summary>Opens on the command list; a query such as <c>"&gt; Snippet: "</c> narrows it at once.</summary>
    public void OpenCommands(string query = ">") => Open(commandMode: true, query);
    public void OpenSymbols() => Open(commandMode: false, "#");
    private void Open(bool commandMode, string query) { IsCommandMode = commandMode; Query = query; IsOpen = true; Refresh(); }

    [RelayCommand] private void Close() => IsOpen = false;
    [RelayCommand] private Task ExecuteAsync() => _host.GuardAsync(async () => { var entry = Selected; if (entry == null) return; IsOpen = false; await entry.Execute(); });
    [RelayCommand] private void Next() => Move(1);
    [RelayCommand] private void Previous() => Move(-1);
    private void Move(int delta)
    {
        if (Results.Count == 0) return;
        var index = Selected == null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    public void Refresh()
    {
        Results.Clear();
        var query = Query.TrimStart();
        var commandMode = query.StartsWith('>');
        if (IsCommandMode != commandMode) IsCommandMode = commandMode;
        if (commandMode) query = query[1..].Trim();
        else if (query.StartsWith('#')) { RefreshSymbols(query[1..].Trim()); return; }
        _symbolSearch?.Cancel();
        IEnumerable<PaletteEntry> results;
        if (commandMode)
        {
            var commands = _host.CommandEntries().ToList();
            results = query.Length == 0 ? commands : commands
                .Select(entry => (entry, score: Best(FuzzyScore(entry.Label, query), FuzzyScore(entry.Category + " " + entry.Label, query) - 6)))
                .Where(x => x.score != null).OrderByDescending(x => x.score).Select(x => x.entry);
        }
        else
        {
            var root = _host.WorkspaceRoot;
            var files = _host.OpenDocumentPaths.Reverse().Concat(_host.WorkspaceFiles).Distinct(StringComparer.OrdinalIgnoreCase);
            results = query.Length == 0 ? files.Select(_host.FileEntry) : files
                .Select(path => (path, score: Best(FuzzyScore(Path.GetFileName(path), query), FuzzyScore(Path.GetRelativePath(root.Length > 0 ? root : Path.GetPathRoot(path) ?? "", path), query) - 20)))
                .Where(x => x.score != null).OrderByDescending(x => x.score).ThenBy(x => x.path.Length).Select(x => _host.FileEntry(x.path));
        }
        foreach (var entry in results.Take(MaximumResults)) Results.Add(entry);
        Selected = Results.FirstOrDefault();
    }

    /// <summary>'#' mode: search types and members across the solution as the query changes.</summary>
    private void RefreshSymbols(string query)
    {
        _symbolSearch?.Cancel(); _symbolSearch?.Dispose();
        _symbolSearch = new();
        Results.Clear(); Selected = null;
        if (query.Length == 0) return;
        SearchSymbolsAsync(query, _symbolSearch.Token).Forget();
    }
    private async Task SearchSymbolsAsync(string query, CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token); // Coalesce keystrokes into one worker query.
            if (!_host.IsSymbolSearchAvailable) { _host.ReportStatus("Symbol search needs the language worker. Open a solution or restart the language service."); return; }
            var results = await _host.SearchSymbolsAsync(query, token);
            if (token.IsCancellationRequested || !IsOpen) return;
            Results.Clear();
            foreach (var result in results) Results.Add(SymbolEntry(result));
            Selected = Results.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _host.ReportStatus("Symbol search: " + ex.Message); }
    }
    private PaletteEntry SymbolEntry(SymbolSearchResult result)
    {
        var location = result.Location;
        var where = $"{Path.GetFileName(location.Path)}:{location.Line}";
        return new(result.Name, result.Container is { Length: > 0 } container ? container + " · " + where : where,
            () => _host.NavigateAsync(location.Path, location.Line, location.Column), result.Kind, Icon: "Braces");
    }

    private static int? Best(int? first, int? second) => first == null ? second : second == null ? first : Math.Max(first.Value, second.Value);

    /// <summary>Case-insensitive subsequence match favouring prefixes, word starts and runs; null when absent.</summary>
    internal static int? FuzzyScore(string candidate, string query)
    {
        if (query.Length == 0) return 0;
        int score = 0, matched = 0, previous = -2;
        for (var i = 0; i < candidate.Length && matched < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[matched])) continue;
            var wordStart = i == 0 || !char.IsLetterOrDigit(candidate[i - 1]) || (char.IsUpper(candidate[i]) && char.IsLower(candidate[i - 1]));
            score += 1 + (wordStart ? 8 : 0) + (i == previous + 1 ? 5 : 0);
            previous = i; matched++;
        }
        if (matched < query.Length) return null;
        if (candidate.StartsWith(query, IgnoreCase)) score += 30;
        else if (candidate.Contains(query, IgnoreCase)) score += 14;
        return score - candidate.Length / 10;
    }

    public void Dispose() { _symbolSearch?.Cancel(); _symbolSearch?.Dispose(); _symbolSearch = null; }
}
