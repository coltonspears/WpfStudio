using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfStudio.Database.Models;

namespace WpfStudio.Database.Services;

public sealed class ConnectionProfileStore : IConnectionProfileStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WpfStudio.SqlConnections.v1");
    public ConnectionProfileStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio", "sql-connections.json")) { }
    public ConnectionProfileStore(string path) => _path = path;

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream = File.OpenRead(_path);
            var saved = await JsonSerializer.DeserializeAsync<List<StoredProfile>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
            return saved.Select(item => item.Profile with { Password = Decrypt(item.EncryptedPassword) }).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var saved = profiles.Select(profile => new StoredProfile(profile, profile.RememberPassword && !profile.WindowsAuthentication ? Encrypt(profile.Password) : null));
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, saved, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    private static string Encrypt(string password)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string Decrypt(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private sealed record StoredProfile(ConnectionProfile Profile, string? EncryptedPassword);
}
