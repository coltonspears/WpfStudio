using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfStudio.App.Features.Git;
using WpfStudio.Core.DevOps;
using WpfStudio.Core.IO;

namespace WpfStudio.App.Features.DevOps;

/// <summary>The DevOps workbench's preferences, and the tokens saved for each organization or host, encrypted for this Windows account.</summary>
public sealed record DevOpsSettings
{
    public Dictionary<string, string> ProtectedTokens { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Organizations and hosts where Git's own sign-in is not to be used, because the person signed out of it.</summary>
    public List<string> GitSignInDeclined { get; init; } = [];
    public string BranchTemplate { get; init; } = "feature/{id}-{title}";
    /// <summary>Work item states the personal Azure Boards lists leave out.</summary>
    public string HiddenStates { get; init; } = "Closed, Done, Removed, Completed, Cut";
    /// <summary>How often a running pipeline is read again, in seconds; 0 turns it off.</summary>
    public int RefreshSeconds { get; init; } = 15;
    public MergeStrategy MergeStrategy { get; init; } = MergeStrategy.Squash;
    public bool DeleteSourceBranch { get; init; } = true;
    public bool CompleteWorkItems { get; init; } = true;
    public bool KeepLogTimestamps { get; init; }

    public IReadOnlyList<string> HiddenStateList => HiddenStates.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public enum DevOpsSignInSource { PersonalAccessToken, GitCredentialManager }

/// <summary>A token to reach one service, and where it came from. It is never written to a log or shown.</summary>
public sealed record DevOpsSignIn(string Token, DevOpsSignInSource Source)
{
    public override string ToString() => $"DevOpsSignIn({Source})";
}

/// <summary>Where the DevOps workbench gets and keeps its tokens.</summary>
public interface IDevOpsCredentials
{
    Task<DevOpsSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(Func<DevOpsSettings, DevOpsSettings> change, CancellationToken cancellationToken = default);
    /// <summary>A saved token for the target, else the one Git Credential Manager has for it, without showing anything; null when there is neither.</summary>
    Task<DevOpsSignIn?> FindAsync(DevOpsTarget target, string repositoryRoot, CancellationToken cancellationToken = default);
    /// <summary>Asks Git Credential Manager, which may show its sign-in window (Microsoft Entra for Azure DevOps, the browser for GitHub).</summary>
    Task<DevOpsSignIn?> SignInWithGitAsync(DevOpsTarget target, string repositoryRoot, CancellationToken cancellationToken = default);
    Task SaveTokenAsync(DevOpsTarget target, string token, CancellationToken cancellationToken = default);
    /// <summary>Forgets the saved token and stops using Git's sign-in for the target until the person signs in again.</summary>
    Task SignOutAsync(DevOpsTarget target, CancellationToken cancellationToken = default);
}

/// <summary>
/// Saves to <c>devops.json</c> in the WpfStudio profile. Tokens are encrypted with Windows data protection for the current user, so
/// the file is useless to another account or machine. Git's credentials stay in Git Credential Manager: they are read when needed
/// and never copied here.
/// </summary>
public sealed class DevOpsCredentials(string directory, GitService git) : IDevOpsCredentials
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WpfStudio.DevOps.Token.v1");
    private readonly string _path = Path.Combine(directory, "devops.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DevOpsSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<DevOpsSettings>(await File.ReadAllTextAsync(_path, cancellationToken)) ?? new();
            // Deserialized dictionaries compare keys exactly; keys differ only in case when typed by hand.
            return settings with { ProtectedTokens = new(settings.ProtectedTokens, StringComparer.OrdinalIgnoreCase) };
        }
        catch (JsonException) { throw new InvalidOperationException("DevOps settings could not be read. Sign in again to replace the damaged file " + _path + "."); }
    }

    public async Task SaveSettingsAsync(Func<DevOpsSettings, DevOpsSettings> change, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = change(await LoadSettingsAsync(cancellationToken));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await AtomicFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(settings, Indented), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<DevOpsSignIn?> FindAsync(DevOpsTarget target, string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (settings.ProtectedTokens.TryGetValue(target.CredentialKey, out var stored)) return new(Unprotect(stored), DevOpsSignInSource.PersonalAccessToken);
        if (settings.GitSignInDeclined.Contains(target.CredentialKey, StringComparer.OrdinalIgnoreCase)) return null;
        return await git.GetCredentialTokenAsync(repositoryRoot, target.CredentialUrl, interactive: false, cancellationToken) is { } token
            ? new(token, DevOpsSignInSource.GitCredentialManager) : null;
    }

    public async Task<DevOpsSignIn?> SignInWithGitAsync(DevOpsTarget target, string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var token = await git.GetCredentialTokenAsync(repositoryRoot, target.CredentialUrl, interactive: true, cancellationToken);
        if (token is null) return null;
        await SaveSettingsAsync(settings => settings with
        {
            GitSignInDeclined = settings.GitSignInDeclined.Where(key => !string.Equals(key, target.CredentialKey, StringComparison.OrdinalIgnoreCase)).ToList()
        }, cancellationToken);
        return new(token, DevOpsSignInSource.GitCredentialManager);
    }

    public Task SaveTokenAsync(DevOpsTarget target, string token, CancellationToken cancellationToken = default)
    {
        var protectedToken = Protect(token.Trim());
        return SaveSettingsAsync(settings =>
        {
            var tokens = new Dictionary<string, string>(settings.ProtectedTokens, StringComparer.OrdinalIgnoreCase) { [target.CredentialKey] = protectedToken };
            return settings with { ProtectedTokens = tokens };
        }, cancellationToken);
    }

    public Task SignOutAsync(DevOpsTarget target, CancellationToken cancellationToken = default) => SaveSettingsAsync(settings =>
    {
        var tokens = new Dictionary<string, string>(settings.ProtectedTokens, StringComparer.OrdinalIgnoreCase);
        tokens.Remove(target.CredentialKey);
        var declined = settings.GitSignInDeclined.Contains(target.CredentialKey, StringComparer.OrdinalIgnoreCase)
            ? settings.GitSignInDeclined : [.. settings.GitSignInDeclined, target.CredentialKey];
        return settings with { ProtectedTokens = tokens, GitSignInDeclined = declined };
    }, cancellationToken);

    private static string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string Unprotect(string value)
    {
        byte[]? bytes = null;
        try
        {
            bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new InvalidOperationException("The saved token cannot be unlocked by this Windows account. Sign out and sign in again."); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
