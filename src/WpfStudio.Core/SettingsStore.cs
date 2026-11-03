using System.Text.Json;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core;

public sealed class SettingsStore
{
    private readonly string _path;
    public SettingsStore(string? directory = null) => _path = Path.Combine(directory ?? AppPaths.DataDirectory, "settings.json");
    public async Task<StudioSettings> LoadAsync(CancellationToken token = default)
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<StudioSettings>(await File.ReadAllTextAsync(_path, token)) ?? new() : new(); }
        catch (JsonException) { return new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }
    public Task SaveAsync(StudioSettings settings, CancellationToken token = default) => DocumentStore.AtomicWriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }), token);
}
