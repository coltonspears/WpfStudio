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
