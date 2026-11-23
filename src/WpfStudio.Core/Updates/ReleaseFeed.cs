using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WpfStudio.Core.Updates;

/// <summary>A file attached to a release. <see cref="Sha256"/> is the hash GitHub lists for it, lower-case hex, when it lists one.</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>A published release: its version (read from the tag), the notes written for it and its files.</summary>
public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string Title, string Notes, Uri Page, DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Why an update could not be checked, downloaded or installed, worded for the person who asked.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads a repository's newest release from GitHub's REST API. GitHub's "latest" release is the newest one that is
/// neither a draft nor a pre-release, so neither is ever offered. The repository is public, so nothing signs in and no
/// token is sent; GitHub allows about 60 such requests an hour from one address.
/// </summary>
public sealed class ReleaseFeed
{
    public const string DefaultApiAddress = "https://api.github.com/";
    private readonly HttpClient _http;
    private readonly string _userAgent;

    public ReleaseFeed(HttpClient http, string repository, string? userAgentVersion = null, Uri? apiAddress = null)
    {
        if (!IsRepositoryName(repository)) throw new ArgumentException($"'{repository}' is not a GitHub repository written as owner/name.", nameof(repository));
        _http = http; Repository = repository;
        ApiAddress = apiAddress ?? new Uri(DefaultApiAddress);
        // GitHub refuses API requests without a User-Agent. It names the product and its version, nothing about the person.
        _userAgent = "WpfStudio/" + (string.IsNullOrWhiteSpace(userAgentVersion) ? "0" : userAgentVersion);
    }

    public string Repository { get; }
    public Uri ApiAddress { get; }
    /// <summary>The page listing every release, for people who want to download one themselves.</summary>
    public Uri ReleasesPage => new($"https://github.com/{Repository}/releases");

    public static bool IsRepositoryName(string? value) =>
        value?.Split('/') is [var owner, var name] && owner.Length > 0 && name.Length > 0
        && owner.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>The newest published release, or null when the repository has published none yet.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiAddress, $"repos/{Repository}/releases/latest"));
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException exception) { throw new UpdateException($"GitHub could not be reached: {exception.Message}", exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw new UpdateException("GitHub did not answer in time.", exception); }
        using (response)
        {
            // GitHub answers 404 both for a repository without releases and for one it does not show; either way there is nothing to offer.
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (RateLimitReset(response) is { } reset)
                throw new UpdateException($"GitHub limits how often one computer may ask without signing in. Try again after {reset.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} when asked for the newest release.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return Read(document.RootElement);
            }
            catch (JsonException exception) { throw new UpdateException("GitHub's description of the newest release could not be read.", exception); }
        }
    }

    /// <summary>When GitHub refused the request for its unauthenticated rate limit, the time it allows the next one.</summary>
    private static DateTimeOffset? RateLimitReset(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return null;
        if (!response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) || remaining.FirstOrDefault() != "0") return null;
        return response.Headers.TryGetValues("x-ratelimit-reset", out var values) && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.Now.AddHours(1);
    }

    internal static ReleaseInfo Read(JsonElement release)
    {
        var tag = Text(release, "tag_name");
        if (!ReleaseVersion.TryParse(tag, out var version))
            throw new UpdateException($"The newest release is tagged '{tag}', which is not a version such as v1.2.3.");
        if (!Uri.TryCreate(Text(release, "html_url"), UriKind.Absolute, out var page)) throw new UpdateException("GitHub's description of the newest release has no address.");
        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var asset in list.EnumerateArray())
            {
                // An upload still in progress, or one that failed, is listed with another state.
                if (Text(asset, "state") is { Length: > 0 } state && state != "uploaded") continue;
                if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) continue;
                var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                var digest = Text(asset, "digest");
                var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsSha256(digest[7..]) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new ReleaseAsset(Text(asset, "name"), url, size, sha256));
            }
        var published = DateTimeOffset.TryParse(Text(release, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : (DateTimeOffset?)null;
        var title = Text(release, "name");
        return new ReleaseInfo(version, tag, title.Length > 0 ? title : "WpfStudio " + version, Text(release, "body"), page, published, assets);
    }

    internal static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static string Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString()! : "";
}
