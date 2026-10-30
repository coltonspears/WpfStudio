using System.Text.Json;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core;

public static class AppPaths
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("WPFSTUDIO_DATA_DIRECTORY") is { Length: > 0 } directory
        ? Path.GetFullPath(directory)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio");
}
public sealed record StudioSettings
{
    public List<string> RecentWorkspaces { get; init; } = [];
    public List<string> OpenDocuments { get; init; } = [];
    public string? WorkspacePath { get; init; }
    public string Theme { get; init; } = "Dark";
    public string Configuration { get; init; } = "Debug";
    public string? StartupProject { get; init; }
    public bool ShowInlayHints { get; init; } = true;
    public bool ShowInlineBlame { get; init; } = true;
    public bool ShowCodeLens { get; init; } = true;
    /// <summary>Load files changed outside WpfStudio without asking when the editor has no unsaved edits to them.</summary>
    public bool AutoReloadUnchangedFiles { get; init; }
    public bool ShowQuickInfo { get; init; } = true;
    /// <summary>Solution Explorer nests code-behind under its XAML and partial files under their main file.</summary>
    public bool NestRelatedFiles { get; init; } = true;
    public EditorSettings Editor { get; init; } = new();
}

/// <summary>How code editors look and behave. Defaults match WpfStudio before these were configurable.</summary>
public sealed record EditorSettings
{
    public const string DefaultFontFamily = "Cascadia Code, Consolas";
    public string FontFamily { get; init; } = DefaultFontFamily;
    public double FontSize { get; init; } = 13;
    public int TabSize { get; init; } = 4;
    public bool InsertSpaces { get; init; } = true;
    public bool ShowLineNumbers { get; init; } = true;
    public bool HighlightCurrentLine { get; init; } = true;
    public bool WordWrap { get; init; }
    public bool ShowWhitespace { get; init; }
    public bool ShowLineEndings { get; init; }
    public bool ShowColumnRuler { get; init; }
    public int ColumnRulerPosition { get; init; } = 120;
    public bool ShowFolding { get; init; } = true;
    public bool ScrollBelowDocument { get; init; }
    public bool CutCopyWholeLine { get; init; } = true;
    public bool EnableTextDragDrop { get; init; } = true;
    public bool HideCursorWhileTyping { get; init; }
    public bool TrimTrailingWhitespace { get; init; }
    public bool EnsureFinalNewline { get; init; }
    public bool AutoSave { get; init; }
    public int AutoSaveDelaySeconds { get; init; } = 2;
}
public sealed class SettingsStore
{
    private readonly string _path;
    public SettingsStore(string? directory = null) => _path = Path.Combine(directory ?? AppPaths.DataDirectory, "settings.json");
    public async Task<StudioSettings> LoadAsync(CancellationToken token = default)
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<StudioSettings>(await File.ReadAllTextAsync(_path, token)) ?? new() : new(); }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }
    public Task SaveAsync(StudioSettings settings, CancellationToken token = default) => DocumentStore.AtomicWriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }), token);
}
