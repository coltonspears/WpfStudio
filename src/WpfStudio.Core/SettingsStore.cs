using System.Text.Json;
using WpfStudio.Core.IO;

namespace WpfStudio.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string _path;
    public SettingsStore(string directory) => _path = Path.Combine(directory, "settings.json");
    public async Task<StudioSettings> LoadAsync(CancellationToken token = default)
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<StudioSettings>(await File.ReadAllTextAsync(_path, token)) ?? new() : new(); }
        catch (JsonException) { return new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }
    public Task SaveAsync(StudioSettings settings, CancellationToken token = default) => AtomicFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(settings, Indented), token);
}
