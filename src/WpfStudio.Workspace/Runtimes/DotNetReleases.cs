using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpfStudio.Workspace.Runtimes;

/// <summary>An official installer: where to download it and the SHA-512 hash it must have.</summary>
public sealed record InstallerFile(string Title, string Version, string FileName, Uri Url, string Sha512, bool OutOfSupport);

/// <summary>
/// Microsoft's release metadata for .NET: <c>releases-index.json</c> lists the channels, and each channel's
/// <c>releases.json</c> lists every release with its installers and their hashes, so nothing is looked up by hand.
/// </summary>
public sealed class DotNetReleases(HttpClient http)
{
    public static readonly Uri IndexUrl = new("https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json");
    // Installers are only fetched from the hosts the metadata has always pointed at.
    private static readonly string[] TrustedHosts = ["builds.dotnet.microsoft.com", "dotnetcli.azureedge.net", "dotnetcli.blob.core.windows.net", "download.visualstudio.microsoft.com"];

    /// <summary>The runtime identifier of this machine's installers, such as <c>win-x64</c>.</summary>
    public static string MachineRid => RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "win-arm64", Architecture.X86 => "win-x86", _ => "win-x64" };

    /// <summary>The newest release of the shared framework in its channel.</summary>
    public async Task<InstallerFile> FindRuntimeAsync(string framework, Version channel, string rid, CancellationToken cancellationToken)
    {
        var (component, prefix) = framework switch
        {
            SharedFrameworks.Desktop => ("windowsdesktop", "windowsdesktop-runtime-"),
            SharedFrameworks.AspNet => ("aspnetcore-runtime", "aspnetcore-runtime-"),
            _ => ("runtime", "dotnet-runtime-")
        };
        var (releases, outOfSupport) = await ReadChannelAsync(channel.ToString(2), cancellationToken).ConfigureAwait(false);
        using (releases)
        {
            foreach (var release in Items(releases.RootElement, "releases"))
                if (Member(release, component) is { } part && Installer(part, prefix, rid) is { } file)
                    return file with { Title = $"{SharedFrameworks.DisplayName(framework)} {Text(part, "version")} ({rid[4..]})", Version = Text(part, "version"), OutOfSupport = outOfSupport };
        }
        throw new InvalidOperationException($"The release list for .NET {channel.ToString(2)} has no {SharedFrameworks.DisplayName(framework)} installer for {rid}.");
    }

    /// <summary>
    /// The SDK for a global.json version: that release when it is listed, otherwise the newest of the same feature band,
    /// as global.json's default roll-forward accepts. Without a version, the newest SDK of the newest supported LTS channel.
    /// </summary>
    public async Task<InstallerFile> FindSdkAsync(string? version, string rid, CancellationToken cancellationToken)
    {
        string channel;
        if (version is not null && RuntimeRequirements.ParseVersion(version) is { } requested) channel = $"{requested.Major}.{requested.Minor}";
        else
        {
            using var index = await GetJsonAsync(IndexUrl, cancellationToken).ConfigureAwait(false);
            channel = Items(index.RootElement, "releases-index").Where(entry => Text(entry, "release-type") == "lts" && Text(entry, "support-phase") == "active")
                .Select(entry => Text(entry, "channel-version")).FirstOrDefault(name => name.Length > 0) ?? throw new InvalidOperationException("The release index lists no supported LTS channel.");
        }
        var (releases, outOfSupport) = await ReadChannelAsync(channel, cancellationToken).ConfigureAwait(false);
        using (releases)
        {
            // Newest release first; each lists its SDKs, newest feature band first.
            var sdks = Items(releases.RootElement, "releases").SelectMany(release => Items(release, "sdks").Concat(Member(release, "sdk") is { } sdk ? [sdk] : []))
                .Where(sdk => Text(sdk, "version").Length > 0).ToArray();
            JsonElement? chosen = version is null ? sdks.FirstOrDefault()
                : sdks.Where(sdk => Text(sdk, "version") == version).Cast<JsonElement?>().FirstOrDefault()
                  ?? sdks.Where(sdk => SameFeatureBand(Text(sdk, "version"), version)).OrderByDescending(sdk => RuntimeRequirements.ParseVersion(Text(sdk, "version"))).Cast<JsonElement?>().FirstOrDefault();
            if (chosen is { ValueKind: JsonValueKind.Object } found && Installer(found, "dotnet-sdk-", rid) is { } file)
                return file with { Title = $".NET SDK {Text(found, "version")} ({rid[4..]})", Version = Text(found, "version"), OutOfSupport = outOfSupport };
        }
        throw new InvalidOperationException(version is null ? $"No SDK installer for {rid} was found." : $"No SDK {version}, or later one of the same feature band, is listed for {rid}.");
    }

    /// <summary>10.0.105 and 10.0.199 share a feature band; 10.0.2xx is the next one. Only a band at or after the requested version counts.</summary>
    private static bool SameFeatureBand(string candidate, string requested) =>
        RuntimeRequirements.ParseVersion(candidate) is { } have && RuntimeRequirements.ParseVersion(requested) is { } want
        && have.Major == want.Major && have.Minor == want.Minor && have.Build / 100 == want.Build / 100 && have.Build >= want.Build;

    private async Task<(JsonDocument Releases, bool OutOfSupport)> ReadChannelAsync(string channel, CancellationToken cancellationToken)
    {
        using var index = await GetJsonAsync(IndexUrl, cancellationToken).ConfigureAwait(false);
        var entry = Items(index.RootElement, "releases-index").FirstOrDefault(item => Text(item, "channel-version") == channel);
        if (entry.ValueKind != JsonValueKind.Object || !Uri.TryCreate(Text(entry, "releases.json"), UriKind.Absolute, out var url))
            throw new InvalidOperationException($"The .NET release index has no channel {channel}.");
        return (await GetJsonAsync(url, cancellationToken).ConfigureAwait(false), Text(entry, "support-phase") == "eol");
    }

    private async Task<JsonDocument> GetJsonAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!IsTrusted(url)) throw new InvalidOperationException($"Release metadata must come from Microsoft over HTTPS, not {url.Host}.");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static InstallerFile? Installer(JsonElement part, string prefix, string rid)
    {
        foreach (var file in Items(part, "files"))
        {
            var name = Text(file, "name");
            if (Text(file, "rid") != rid || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(Text(file, "url"), UriKind.Absolute, out var url) || !IsTrusted(url) || Text(file, "hash") is not { Length: 128 } hash) continue;
            return new InstallerFile("", "", Path.GetFileName(url.AbsolutePath), url, hash.ToLowerInvariant(), false);
        }
        return null;
    }

    public static bool IsTrusted(Uri url) => url.Scheme == Uri.UriSchemeHttps && TrustedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
    private static IEnumerable<JsonElement> Items(JsonElement value, string property) => Member(value, property) is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray() : [];
    private static JsonElement? Member(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var member) && member.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? member : null;
    private static string Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString()! : "";
}

public enum InstallOutcome { Installed, RestartNeeded, Cancelled, Failed }
public sealed record InstallResult(InstallOutcome Outcome, int ExitCode);

/// <summary>Runs a downloaded installer and returns its exit code. The official installers ask Windows for administrator rights themselves.</summary>
public interface IInstallerLauncher
{
    Task<int> RunAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Downloads an installer, refuses it unless its SHA-512 hash matches the release metadata, then runs it.</summary>
public sealed class RuntimeInstaller(HttpClient http, IInstallerLauncher launcher, string downloadDirectory)
{
    public async Task<InstallResult> InstallAsync(InstallerFile file, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!DotNetReleases.IsTrusted(file.Url)) throw new InvalidOperationException($"Installers are only downloaded from Microsoft over HTTPS, not {file.Url.Host}.");
        Directory.CreateDirectory(downloadDirectory);
        var path = Path.Combine(downloadDirectory, Guid.NewGuid().ToString("N")[..8] + "-" + file.FileName);
        try
        {
            progress?.Report($"Downloading {file.Title}…");
            using (var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                var buffer = new byte[81920];
                long received = 0, reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total is > 0 && received - reported > total / 10) { reported = received; progress?.Report($"Downloading {file.Title}… {received * 100 / total}%"); }
                }
                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!actual.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The downloaded {file.FileName} does not match the SHA-512 hash Microsoft lists for it, so it was not run.");
            }
            progress?.Report($"Installing {file.Title}. Windows asks for permission to change the computer.");
            var exitCode = await launcher.RunAsync(path, cancellationToken).ConfigureAwait(false);
            return new(exitCode switch
            {
                0 => InstallOutcome.Installed,
                3010 or 1641 => InstallOutcome.RestartNeeded,
                1602 or 1223 => InstallOutcome.Cancelled,
                _ => InstallOutcome.Failed
            }, exitCode);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
