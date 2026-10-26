using System.IO;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.ViewModels;

/// <summary>Go to symbol, Go to implementation and Back/Forward navigation history.</summary>
public sealed partial class ShellViewModel
{
    private readonly NavigationHistory _history = new();
    private CancellationTokenSource? _symbolSearch;
    private bool _replayingHistory;

    private NavigationLocation? CurrentLocation() =>
        ActiveDocument is { } document ? new(document.State.Path, document.State.CaretOffset, document.State.CaretLine) : null;

    /// <summary>Remembers the caret before a jump so Back can return to it.</summary>
    private void RecordNavigationOrigin()
    {
        if (!_replayingHistory && CurrentLocation() is { } origin) _history.Record(origin);
    }

    [RelayCommand] private Task NavigateBackAsync() => GuardAsync(async () =>
    {
        if (_history.GoBack(CurrentLocation()) is { } target) await ReplayLocationAsync(target);
        else Status = "No earlier location";
    });
    [RelayCommand] private Task NavigateForwardAsync() => GuardAsync(async () =>
    {
        if (_history.GoForward(CurrentLocation()) is { } target) await ReplayLocationAsync(target);
        else Status = "No later location";
    });
    private async Task ReplayLocationAsync(NavigationLocation target)
    {
        if (!File.Exists(target.Path) && _store.Find(target.Path) is null) { Status = "That location's file no longer exists"; return; }
        _replayingHistory = true;
        try
        {
            await OpenDocumentAsync(target.Path);
            if (ActiveDocument is { } document && document.State.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase))
                document.Navigate(Math.Min(target.Offset, document.State.Content.Length));
        }
        finally { _replayingHistory = false; }
    }

    [RelayCommand] private void GoToSymbol() { IsCommandPalette = false; PaletteQuery = "#"; IsPaletteOpen = true; RefreshPalette(); }

    [RelayCommand] private Task GoToImplementationAsync() => GuardAsync(async () =>
    {
        if (ActiveDocument is not { IsCSharp: true } document) { Status = "Place the caret on a C# type or member to find its implementations"; return; }
        if (!_workspace.IsConnected) { Status = "Implementations are unavailable while the language worker is disconnected."; return; }
        await document.SyncAsync(); var state = document.State;
        var results = await _workspace.FindImplementationsAsync(new(state.Path, state.CaretOffset, state.Version), _lifetime.Token);
        if (results.Count == 0) { Status = "No implementations found"; return; }
        if (results.Count == 1) { await NavigateAsync(results[0].Path, results[0].Line, results[0].Column); return; }
        SearchResults.Clear();
        foreach (var location in results)
            SearchResults.Add(new NavigationResult(location.Path, location.Line, location.Column, location.DisplayText ?? "Implementation"));
        ToolRequested?.Invoke("Search");
        Status = $"{results.Count} implementation(s)";
    });

    /// <summary>Palette '#' mode: search types and members across the solution as the query changes.</summary>
    private void RefreshSymbolPalette(string query)
    {
        _symbolSearch?.Cancel(); _symbolSearch?.Dispose();
        _symbolSearch = new();
        PaletteResults.Clear(); SelectedPaletteEntry = null;
        if (query.Length == 0) return;
        _ = SearchPaletteSymbolsAsync(query, _symbolSearch.Token);
    }
    private async Task SearchPaletteSymbolsAsync(string query, CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token); // Coalesce keystrokes into one worker query.
            if (!_workspace.IsConnected) { Status = "Symbol search needs the language worker. Open a solution or restart the language service."; return; }
            var results = await _workspace.SearchSymbolsAsync(new(query, 80), token);
            if (token.IsCancellationRequested || !IsPaletteOpen) return;
            PaletteResults.Clear();
            foreach (var result in results) PaletteResults.Add(SymbolEntry(result));
            SelectedPaletteEntry = PaletteResults.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Status = "Symbol search: " + ex.Message; }
    }
    private PaletteEntry SymbolEntry(SymbolSearchResult result)
    {
        var location = result.Location;
        var where = $"{Path.GetFileName(location.Path)}:{location.Line}";
        return new(result.Name, result.Container is { Length: > 0 } container ? container + " · " + where : where,
            () => NavigateAsync(location.Path, location.Line, location.Column), result.Kind, Icon: "Braces");
    }
}
