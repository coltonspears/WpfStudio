using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

/// <summary>
/// Go to definition. The worker answers for C# and XAML; this waits out the moments in which it cannot (a save still being
/// read, buffers being synchronized) instead of giving up, says why when there is nowhere to go, and as a last resort
/// finds a type by its name in the workspace's files, including those of projects that did not load.
/// </summary>
public sealed partial class ShellViewModel
{
    private const int DefinitionAttempts = 3;

    [RelayCommand] private Task GoToDefinitionAsync() => GuardAsync(async () =>
    {
        if (ActiveDocument is not { } document) return;
        if (document.IsXaml) { await GoToXamlDefinitionAsync(document); return; }
        if (!document.IsCSharp) { Status = "Go to definition works in C# and XAML files"; return; }
        if (!_workspace.IsConnected) { await GoToNamedTypeAsync(document, "Definitions are unavailable while the language worker is disconnected."); return; }
        var state = document.State;
        int caret = state.CaretOffset;
        DefinitionResult result;
        try { result = await FindCSharpDefinitionAsync(document); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A file no loaded project compiles has no symbols, but the name under the caret may still lead somewhere.
            AppendOutput("Go to definition: " + exception.Message);
            if (ReferenceEquals(document, ActiveDocument) && state.CaretOffset == caret) await GoToNamedTypeAsync(document, exception.Message);
            return;
        }
        if (!ReferenceEquals(document, ActiveDocument) || state.CaretOffset != caret) return;
        if (result.Locations.Count > 0) await ShowDefinitionsAsync(result.Locations, result.Status);
        // A name the worker resolved into an assembly has no source to find. One it could not resolve may be in a project that did not load.
        else if (result.Resolved) Status = result.Status ?? "No source definition found";
        else await GoToNamedTypeAsync(document, result.Status ?? "No source definition found");
    });

    // The worker turns a request away while it synchronizes buffers and XAML fields. That passes in a moment, so ask again.
    private async Task<DefinitionResult> FindCSharpDefinitionAsync(EditorViewModel document)
    {
        for (int attempt = 1; ; attempt++)
        {
            await document.SyncAsync(_lifetime.Token);
            var state = document.State;
            try { return await _workspace.FindDefinitionAsync(new(state.Path, state.CaretOffset, state.Version), _lifetime.Token); }
            catch (Exception exception) when (attempt < DefinitionAttempts && exception is not OperationCanceledException && _workspace.IsConnected)
            { await Task.Delay(120 * attempt, _lifetime.Token); }
        }
    }

    private async Task GoToXamlDefinitionAsync(EditorViewModel document)
    {
        var current = document.State;
        var version = current.Version;
        int caret = current.CaretOffset;
        bool Moved() => document != ActiveDocument || current.Version != version || current.CaretOffset != caret;
        // XAML reads its types from the project, which a save a moment ago may still be changing.
        if (_workspace.IsConnected && XamlAnalysis.ProjectTypesPending)
        {
            Status = "Reading changed source files…";
            await XamlAnalysis.WaitForProjectTypesAsync(TimeSpan.FromSeconds(8), _lifetime.Token);
            if (Moved()) return;
        }
        for (int attempt = 1; _workspace.IsConnected; attempt++)
        {
            var contextRevision = document.XamlContextRevision;
            IReadOnlyList<SourceLocation> definitions = [];
            try { definitions = await document.XamlDefinitionAsync(caret); }
            catch (Exception ex) { AppendOutput("XAML definition: " + ex.Message); }
            if (Moved()) return;
            if (definitions.Count > 0) { await ShowDefinitionsAsync(definitions, null); return; }
            // An answer for types that changed meanwhile says nothing about the ones there now.
            if (document.XamlContextRevision == contextRevision || attempt >= DefinitionAttempts) break;
            await Task.Delay(120 * attempt, _lifetime.Token);
            if (Moved()) return;
        }
        // The resource index is usable offline only while its source still matches this buffer.
        var usage = WpfExplorer.Index?.Texts.TryGetValue(current.Path, out var indexed) == true && indexed == current.Content
            ? WpfExplorer.Index.Usages.FirstOrDefault(u => u.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase) && caret >= u.Start && caret <= u.Start + u.Length) : null;
        var declaration = usage?.ResolvedPath == null ? null : WpfExplorer.Index!.Resources.FirstOrDefault(r => r.Path == usage.ResolvedPath && r.ValueStart == usage.ResolvedDeclarationStart);
        if (declaration != null) await NavigateAsync(declaration.Path, declaration.Line, 1);
        else Status = "This XAML reference could not be resolved statically";
    }

    /// <summary>Goes to the first declaration. Several, as with a partial class, are also listed in Search.</summary>
    private async Task ShowDefinitionsAsync(IReadOnlyList<SourceLocation> locations, string? note)
    {
        var first = locations[0];
        await NavigateAsync(first.Path, first.Line, first.Column);
        if (locations.Count == 1) { if (note is not null) Status = note; return; }
        // A guess needs showing; the parts of one declaration only need mentioning.
        ShowResults(Count(locations.Count, "declaration", "declarations"), locations.Select(location => ResultFor(location, "Declaration") with { Start = 0, ExpectedTextHash = null }), reveal: note is not null);
        Status = note ?? $"Declared in {locations.Count} places; the others are listed in Search.";
    }

    /// <summary>
    /// The last resort: types declared with the caret's name anywhere in the workspace's C# files. It is how a type in a
    /// project the language service could not load is still reached. <paramref name="reason"/> is shown when nothing is found.
    /// </summary>
    private async Task GoToNamedTypeAsync(EditorViewModel document, string reason)
    {
        if (IdentifierAt(document.State.Content, document.State.CaretOffset) is not { } name) { Status = reason; return; }
        var files = _allFiles.Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
        var buffers = _store.Documents.ToDictionary(open => open.Path, open => open.Content, StringComparer.OrdinalIgnoreCase);
        var token = _lifetime.Token;
        var found = await Task.Run(async () =>
        {
            var declaration = new Regex(@"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+" + Regex.Escape(name) + @"\b", RegexOptions.CultureInvariant);
            var results = new List<NavigationResult>();
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                string text;
                try { text = buffers.GetValueOrDefault(path) ?? await File.ReadAllTextAsync(path, token); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                if (!text.Contains(name, StringComparison.Ordinal)) continue;
                foreach (Match match in declaration.Matches(text))
                {
                    int start = match.Index + match.Length - name.Length;
                    int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
                    int lineEnd = text.IndexOf('\n', start); if (lineEnd < 0) lineEnd = text.Length;
                    var line = text[lineStart..lineEnd].TrimEnd('\r');
                    results.Add(new NavigationResult(path, text.AsSpan(0, start).Count('\n') + 1, start - lineStart + 1, line).WithLine(line, start - lineStart, name.Length));
                    if (results.Count >= 20) return results;
                }
            }
            return results;
        }, token);
        if (!ReferenceEquals(document, ActiveDocument)) return;
        if (found.Count == 0) { Status = reason; return; }
        await NavigateAsync(found[0].Path, found[0].Line, found[0].Column);
        if (found.Count > 1) ShowResults($"{found.Count} types named {name}", found);
        Status = found.Count == 1 ? $"Found {name} by its name; the language service could not resolve it."
            : $"{found.Count} types are named {name}; the language service could not say which one this is.";
    }

    /// <summary>The identifier the caret is in or directly after.</summary>
    internal static string? IdentifierAt(string text, int caret)
    {
        static bool Part(char c) => char.IsLetterOrDigit(c) || c == '_';
        caret = Math.Clamp(caret, 0, text.Length);
        int start = caret, end = caret;
        while (start > 0 && Part(text[start - 1])) start--;
        while (end < text.Length && Part(text[end])) end++;
        return end > start && !char.IsDigit(text[start]) ? text[start..end] : null;
    }
}
