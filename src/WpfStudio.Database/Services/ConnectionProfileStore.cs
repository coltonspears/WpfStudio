using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfStudio.Core.IO;
using WpfStudio.Database.Models;

namespace WpfStudio.Database.Services;

public sealed class ConnectionProfileStore : IConnectionProfileStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WpfStudio.SqlConnections.v1");
    public ConnectionProfileStore(string path) => _path = path;

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream = File.OpenRead(_path);
            var saved = await JsonSerializer.DeserializeAsync<List<StoredProfile>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
            return saved.Select(item => item.Profile with { ProtectedPassword = item.EncryptedPassword }).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Profiles nobody has opened keep their blob as it is, including one this account cannot unlock.
            var saved = profiles.Select(profile => new StoredProfile(profile, profile.RememberPassword && !profile.WindowsAuthentication ? profile.ProtectedPassword ?? Encrypt(profile.Password) : null));
            await AtomicFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(saved, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public string UnlockPassword(ConnectionProfile profile) => profile.ProtectedPassword is { } saved ? Decrypt(saved) : profile.Password;

    private static string Encrypt(string password)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string Decrypt(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        byte[] bytes;
        // A blob protected by another Windows account or machine cannot be unlocked here. The profile stays usable
        // once its password is typed again, and must not take every other saved connection down with it.
        try { bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser); }
        catch (Exception exception) when (exception is CryptographicException or FormatException) { return ""; }
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private sealed record StoredProfile(ConnectionProfile Profile, string? EncryptedPassword);
}
