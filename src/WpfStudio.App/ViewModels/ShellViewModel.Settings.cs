using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.ViewModels;

/// <summary>
/// The settings window's pages, editor preferences and the file behaviours they control: automatic saving and
/// tidying whitespace when saving. Changes are saved shortly after they are made, not only on exit.
/// </summary>
public sealed partial class ShellViewModel
{
    public const string AppearancePage = "Appearance", EditorPage = "Editor", IntelligencePage = "Intelligence",
        FilesPage = "Files", AssistantPage = "Assistant", ShortcutsPage = "Shortcuts";
    private CancellationTokenSource? _settingsSave, _autoSave;

    public EditorPreferences Editor => EditorPreferences.Current;
    [ObservableProperty] public partial string SettingsPage { get; set; } = AppearancePage;
    [ObservableProperty] public partial bool ShowQuickInfo { get; set; } = true;
    partial void OnShowQuickInfoChanged(bool value) { foreach (var document in Documents) document.ShowQuickInfo = value; ScheduleSettingsSave(); }
    partial void OnShowInlayHintsChanged(bool oldValue, bool newValue) => ScheduleSettingsSave();
    partial void OnShowCodeLensChanged(bool oldValue, bool newValue) => ScheduleSettingsSave();
    partial void OnShowInlineBlameChanged(bool oldValue, bool newValue) => ScheduleSettingsSave();
    [ObservableProperty] public partial string ShortcutFilter { get; set; } = "";
    partial void OnShortcutFilterChanged(string value) => OnPropertyChanged(nameof(Shortcuts));

    /// <summary>Commands with a keyboard shortcut, filtered by name, menu or keys.</summary>
    public IReadOnlyList<PaletteEntry> Shortcuts
    {
        get
        {
            var filter = ShortcutFilter.Trim();
            return CommandEntries().Concat(EditorShortcuts()).Where(entry => entry.HasShortcut)
                .Where(entry => filter.Length == 0 || entry.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || entry.Category.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || entry.Shortcut.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(entry => (entry.Label, entry.Shortcut)).OrderBy(entry => entry.Category).ThenBy(entry => entry.Label).ToArray();
        }
    }

    // Keys handled inside the code editor rather than by shell commands.
    private static IEnumerable<PaletteEntry> EditorShortcuts()
    {
        static PaletteEntry Key(string label, string keys) => new(label, "", () => Task.CompletedTask, "Editor", keys);
        yield return Key("Show completions", "Ctrl+Space");
        yield return Key("Quick fixes and refactorings", "Ctrl+.");
        yield return Key("Go to definition", "F12");
        yield return Key("Expand a snippet", "Tab");
        yield return Key("Find in file", "Ctrl+F");
    }

    [RelayCommand] private void ShowSettingsPage(string? page) { if (!string.IsNullOrEmpty(page)) SettingsPage = page; }

    private void InitializeSettings()
    {
        Editor.PropertyChanged += (_, args) =>
        {
            ScheduleSettingsSave();
            if (args.PropertyName is nameof(EditorPreferences.AutoSave) or nameof(EditorPreferences.AutoSaveDelaySeconds)) ScheduleAutoSave();
        };
    }

    // Settings are written after a short pause, so dragging through font sizes writes once.
    private void ScheduleSettingsSave()
    {
        if (!_initialized) return;
        _settingsSave?.Cancel(); _settingsSave?.Dispose();
        var pending = _settingsSave = new();
        _ = Task.Delay(600, pending.Token).ContinueWith(_ => _dispatcher.Post(() =>
        {
            if (pending.IsCancellationRequested || _disposed) return;
            _ = GuardAsync(SaveSettingsAsync);
        }), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>
    /// Saves edited files after a pause in typing. Files that changed on disk, were never saved, are read-only or
    /// are part of an open change review are left for an explicit save. Whitespace is not tidied, so text you are
    /// typing does not move.
    /// </summary>
    private void ScheduleAutoSave()
    {
        _autoSave?.Cancel(); _autoSave?.Dispose(); _autoSave = null;
        if (!Editor.AutoSave || !_initialized) return;
        var pending = _autoSave = new();
        _ = Task.Delay(TimeSpan.FromSeconds(Editor.AutoSaveDelaySeconds), pending.Token).ContinueWith(_ => _dispatcher.Post(() =>
        {
            if (!pending.IsCancellationRequested && !_disposed) _ = AutoSaveAsync();
        }), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private async Task AutoSaveAsync()
    {
        if (IsPreviewOpen) { ScheduleAutoSave(); return; }
        var saved = 0;
        foreach (var document in Documents.Where(document => document.State.IsDirty && document.State.DiskHash is not null && !document.IsReadOnly
                     && !document.State.HasExternalChange && File.Exists(document.State.Path)).ToArray())
        {
            try
            {
                await _store.SaveAsync(document.State);
                if (document.IsXaml) _xamlResources.Invalidate();
                saved++;
            }
            catch (ExternalFileChangedException) { _ = CheckExternalChangesAsync(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { AppendOutput("Automatic save: " + exception.Message); }
        }
        if (saved == 0) return;
        RequestProjectXamlAnalysis();
        Status = saved == 1 ? "Saved automatically" : $"Saved {saved} files automatically";
    }

    /// <summary>Applies the whitespace settings to a document before an explicit save.</summary>
    private void TidyBeforeSave(EditorViewModel document) => document.TidyForSave(Editor.TrimTrailingWhitespace, Editor.EnsureFinalNewline);
}
