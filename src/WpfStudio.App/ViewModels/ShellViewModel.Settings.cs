using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

/// <summary>
/// The settings window's pages, editor preferences and the file behaviours they control: automatic saving and
/// tidying whitespace when saving. Changes are saved shortly after they are made, not only on exit.
/// </summary>
public sealed partial class ShellViewModel
{
    public const string AppearancePage = "Appearance", EditorPage = "Editor", IntelligencePage = "Intelligence",
        FilesPage = "Files", AssistantPage = "Assistant", ExperimentalPage = "Experimental", ShortcutsPage = "Shortcuts";
    private CancellationTokenSource? _settingsSave;

    public EditorPreferences Editor { get; }
    public CodeAssistPreferences CodeAssist { get; }
    [ObservableProperty] public partial string SettingsPage { get; set; } = AppearancePage;
    [ObservableProperty] public partial bool ShowQuickInfo { get; set; } = true;
    partial void OnShowQuickInfoChanged(bool value) { foreach (var document in Documents) document.ShowQuickInfo = value; ScheduleSettingsSave(); }
    partial void OnShowInlayHintsChanged(bool oldValue, bool newValue) => ScheduleSettingsSave();
    partial void OnShowCodeLensChanged(bool oldValue, bool newValue) => ScheduleSettingsSave();
    partial void OnShowInlineBlameChanged(bool oldValue, bool newValue) => ScheduleSettingsSave();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Shortcuts))] public partial string ShortcutFilter { get; set; } = "";

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
        yield return Key("Go to definition with the mouse", "Ctrl+Click");
        yield return Key("Expand a snippet", "Tab");
        yield return Key("Find in file", "Ctrl+F");
        yield return Key("Replace in file", "Ctrl+H");
        yield return Key("Show the signature of the call at the caret", "Ctrl+Shift+Space");
        yield return Key("Duplicate the line or selection", "Ctrl+D");
        yield return Key("Delete the line", "Ctrl+Shift+K");
        yield return Key("Move lines up", "Alt+Up");
        yield return Key("Move lines down", "Alt+Down");
        yield return Key("Comment or uncomment lines", "Ctrl+/");
        yield return Key("Open a line below", "Ctrl+Enter");
        yield return Key("Open a line above", "Ctrl+Shift+Enter");
    }

    [RelayCommand] private void ShowSettingsPage(string? page) { if (!string.IsNullOrEmpty(page)) SettingsPage = page; }

    private void InitializeSettings()
    {
        Editor.PropertyChanged += EditorPreferenceChanged;
        CodeAssist.PropertyChanged += (_, args) => { if (args.PropertyName != nameof(CodeAssistPreferences.Status)) ScheduleSettingsSave(); };
    }
    private void EditorPreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        ScheduleSettingsSave();
        // Turned on while the project model is already out of date: there is no later change to wait for.
        if (args.PropertyName == nameof(EditorPreferences.AutoRefreshProject) && _initialized && XamlAnalysis.ProjectReloadRequired)
        {
            RefreshXamlContexts();
            ScheduleProjectModelRefresh();
        }
    }

    // Settings are written after a short pause, so dragging through font sizes writes once.
    private void ScheduleSettingsSave()
    {
        if (!_initialized) return;
        _settingsSave?.Cancel(); _settingsSave?.Dispose();
        var pending = _settingsSave = new();
        Task.Delay(600, pending.Token).ContinueWith(_ => _dispatcher.Post(() =>
        {
            if (pending.IsCancellationRequested || _disposed) return;
            _ = GuardAsync(SaveSettingsAsync);
        }), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default).Forget();
    }

    /// <summary>Applies the whitespace settings to a document before an explicit save.</summary>
    private void TidyBeforeSave(EditorViewModel document) => document.TidyForSave(Editor.TrimTrailingWhitespace, Editor.EnsureFinalNewline);
}
