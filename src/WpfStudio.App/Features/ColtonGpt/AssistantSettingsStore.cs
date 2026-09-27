using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfStudio.Core;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.Features.ColtonGpt;

public sealed record AssistantConfiguration
{
    public string ModelId { get; init; } = "";
    public int MaxOutputTokens { get; init; } = 4096;
    public bool UseTemperature { get; init; }
    public double Temperature { get; init; } = 0.2;
    public string? ProtectedApiKey { get; init; }
}

public sealed class AssistantSettingsStore(string? directory = null)
{
    private readonly string _path = Path.Combine(directory ?? AppPaths.DataDirectory, "coltongpt.json");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WpfStudio.ColtonGPT.OpenRouter.v1");

    public async Task<AssistantConfiguration> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(_path)) return new();
        try { return JsonSerializer.Deserialize<AssistantConfiguration>(await File.ReadAllTextAsync(_path, token)) ?? new(); }
        catch (JsonException) { throw new AssistantException("ColtonGPT settings could not be read. Save settings again to replace the damaged file."); }
    }

    public Task SaveAsync(AssistantConfiguration settings, CancellationToken token = default) =>
        DocumentStore.AtomicWriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }), token);

    public string ProtectKey(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string? ReadKey(AssistantConfiguration settings)
    {
        if (string.IsNullOrEmpty(settings.ProtectedApiKey)) return null;
        byte[]? bytes = null;
        try
        {
            bytes = ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedApiKey), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new AssistantException("The saved API key cannot be unlocked by this Windows account. Clear it and save a new key."); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
