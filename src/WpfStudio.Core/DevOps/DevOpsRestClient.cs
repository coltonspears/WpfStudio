using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WpfStudio.Core.DevOps;

/// <summary>A request the service refused or could not answer. <see cref="Status"/> is the HTTP status when there was a response.</summary>
public class DevOpsException(string message, int? status = null, Exception? inner = null) : Exception(message, inner)
{
    public int? Status { get; } = status;
}

/// <summary>The token itself was refused: expired, revoked, or not valid for this organization or host.</summary>
public sealed class DevOpsAuthenticationException(string message, int? status = null) : DevOpsException(message, status);

/// <summary>The token is valid but lacks a scope or permission; <see cref="Requirement"/> names what to enable when known.</summary>
public sealed class DevOpsAccessDeniedException(string message, int? status = null, string? requirement = null) : DevOpsException(message, status)
{
    public string? Requirement { get; } = requirement;
}

public sealed record DevOpsResponse(int Status, string Body, string ContentType, IReadOnlyDictionary<string, string> Headers)
{
    public bool IsSuccess => Status is >= 200 and < 300;
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    public JsonElement Json()
    {
        if (Body.Length == 0) return default;
        try
        {
            using var document = JsonDocument.Parse(Body);
            return document.RootElement.Clone();
        }
        catch (JsonException ex) { throw new DevOpsException("The service returned a response that is not valid JSON.", Status, ex); }
    }
}

/// <summary>
/// HTTP for the DevOps providers. The token goes only to addresses <paramref name="mayAuthenticate"/> accepts, and never across a
/// redirect to another origin: GitHub answers a log request with a pre-signed storage address that must not receive it. The
/// <see cref="HttpClient"/> should not follow redirects itself; if it does, .NET drops the Authorization header on each one.
/// </summary>
public sealed class DevOpsRestClient(HttpClient http, AuthenticationHeaderValue authorization, Func<Uri, bool> mayAuthenticate,
    IReadOnlyDictionary<string, string>? headers = null)
{
    /// <summary>The most a response may hold. Pipeline logs are the largest thing read, and the log view shows their end.</summary>
    public const int ResponseLimit = 16 * 1024 * 1024;
    private const int MaxRedirects = 5;

    public async Task<DevOpsResponse> SendAsync(HttpMethod method, string url, string? body = null, string? contentType = null,
        string accept = "application/json", CancellationToken cancellationToken = default)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (!mayAuthenticate(uri)) throw new DevOpsException($"WpfStudio refused to send your token to {uri.Host}, which is not part of this service.");
        var authenticate = true;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (authenticate) request.Headers.Authorization = authorization;
            request.Headers.Accept.ParseAdd(accept);
            foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, MediaTypeHeaderValue.Parse(contentType ?? "application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && redirects < MaxRedirects)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                // Same scheme, host and port, and still the service: anything else gets the request without credentials.
                authenticate = authenticate && Uri.Compare(uri, next, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
                    && mayAuthenticate(next);
                if (status == 303 || (status is 301 or 302 && method != HttpMethod.Get)) { method = HttpMethod.Get; body = null; }
                uri = next;
                continue;
            }
            var text = response.Content is null ? "" : await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var collected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers.Concat(response.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
                collected[header.Key] = string.Join(", ", header.Value);
            return new(status, text, response.Content?.Headers.ContentType?.MediaType ?? "", collected);
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > ResponseLimit) throw new DevOpsException($"The response is larger than {ResponseLimit / (1024 * 1024)} MiB. Open it on the web instead.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > ResponseLimit) throw new DevOpsException($"The response is larger than {ResponseLimit / (1024 * 1024)} MiB. Open it on the web instead.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var charset = content.Headers.ContentType?.CharSet;
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* An unknown charset: UTF-8, which every DevOps API uses. */ }
        }
        return encoding.GetString(bytes);
    }

    /// <summary>Basic authentication with an empty user name and the token as password, as Git and Azure DevOps expect a PAT.</summary>
    public static AuthenticationHeaderValue Basic(string token) => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    /// <summary>
    /// A Microsoft Entra access token (a JWT, which Git Credential Manager stores for Azure Repos by default) goes as a bearer
    /// token; anything else is a personal access token.
    /// </summary>
    public static bool IsJwt(string token) => token.StartsWith("eyJ", StringComparison.Ordinal) && token.Count(c => c == '.') == 2;
}

/// <summary>Tolerant readers for the services' JSON: a missing or differently typed member reads as absent.</summary>
public static class DevOpsJson
{
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

    public static JsonElement? Get(this JsonElement element, params string[] path)
    {
        JsonElement? current = element;
        foreach (var name in path)
        {
            current = current?.Get(name);
            if (current is null) return null;
        }
        return current;
    }

    public static string? Str(this JsonElement element, string name) => element.Get(name) switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } value => value.GetRawText(),
        _ => null
    };

    public static string Text(this JsonElement element, string name) => element.Str(name) ?? "";

    public static int? Int(this JsonElement element, string name) => element.Get(name) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt32(out var number) => number,
        { ValueKind: JsonValueKind.String } value when int.TryParse(value.GetString(), out var parsed) => parsed,
        _ => null
    };

    public static long? Long(this JsonElement element, string name) => element.Get(name) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number,
        { ValueKind: JsonValueKind.String } value when long.TryParse(value.GetString(), out var parsed) => parsed,
        _ => null
    };

    public static bool Bool(this JsonElement element, string name) => element.Get(name) is { ValueKind: JsonValueKind.True };

    public static DateTimeOffset? Date(this JsonElement element, string name) => DevOpsText.Date(element.Str(name));

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Items(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
}
