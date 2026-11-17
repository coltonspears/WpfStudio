using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using WpfStudio.Api.Models;
using WpfStudio.Core.Net;

namespace WpfStudio.Api.Requests;

/// <summary>What came back.</summary>
public sealed class ApiResponse
{
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = "";
    public string HttpVersion { get; init; } = "";
    public IReadOnlyList<KeyValue> Headers { get; init; } = [];
    public IReadOnlyList<KeyValue> Cookies { get; init; } = [];
    public byte[] Body { get; init; } = [];
    /// <summary>The body was longer than <see cref="ApiSender.MaxBodyBytes"/>; <see cref="Body"/> holds its start.</summary>
    public bool Truncated { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public long HeadersMilliseconds { get; init; }
    public string? MediaType { get; init; }
    public string Text { get; init; } = "";
    /// <summary>The body indented when it is JSON or XML, otherwise the text as received.</summary>
    public string Pretty { get; init; } = "";
    public bool IsJson { get; init; }
    public Uri? Address { get; init; }
    public string Proxy { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// Sends requests. One handler per distinct proxy and connection setting is kept, so connections are reused between
/// sends, and all share one cookie jar.
/// </summary>
public sealed class ApiSender : IDisposable
{
    public const int MaxBodyBytes = 64 * 1024 * 1024;
    private const int MaxPrettyBytes = 8 * 1024 * 1024;
    private const int MaxHandlers = 12;
    private static readonly JsonWriterOptions IndentedJson = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Func<ProxyConfiguration> _applicationProxy;
    private readonly Dictionary<HandlerKey, HttpClient> _clients = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <param name="applicationProxy">The proxy in Settings, read at each send so a change there applies to the next request.</param>
    public ApiSender(Func<ProxyConfiguration> applicationProxy) => _applicationProxy = applicationProxy;

    public CookieContainer Cookies { get; private set; } = new();

    public void ClearCookies()
    {
        lock (_gate)
        {
            // Handlers hold the container they were given, so they go too.
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
            Cookies = new();
        }
    }

    public EffectiveProxy ProxyFor(ApiRequest request, ApiCollection? collection) => ProxyResolution.Resolve(_applicationProxy(), collection?.Proxy, request.Proxy);

    public async Task<ApiResponse> SendAsync(ApiRequest request, ApiCollection? collection, ApiEnvironment? environment, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var variables = new VariableResolver(collection?.Variables, environment?.Variables);
        var proxy = ProxyFor(request, collection);
        if (proxy.Configuration.Validate() is { } proxyError) throw new ApiRequestException(proxyError + " (" + proxy.Source + ")");
        using var prepared = ApiRequestBuilder.Build(request, collection, variables);
        var client = ClientFor(new HandlerKey(proxy.Configuration, request.Settings.VerifyCertificates, request.Settings.FollowRedirects, request.Settings.UseCookies));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Settings.TimeoutSeconds > 0) timeout.CancelAfter(TimeSpan.FromSeconds(request.Settings.TimeoutSeconds));
        var clock = Stopwatch.StartNew();
        HttpResponseMessage response;
        try { response = await client.SendAsync(prepared.Message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ApiRequestException($"No response within {request.Settings.TimeoutSeconds} seconds. Raise the timeout under Settings, or check the address and proxy ({proxy.Describe()})."); }
        catch (HttpRequestException exception) { throw new ApiRequestException(Explain(exception, prepared.Address, proxy), exception); }
        using (response)
        {
            var headersAt = clock.ElapsedMilliseconds;
            var (body, truncated) = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            clock.Stop();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var text = Decode(body, response.Content.Headers.ContentType);
            var (pretty, isJson) = Prettify(text, mediaType, body.Length);
            var warnings = prepared.Warnings.ToList();
            if (truncated) warnings.Add($"Only the first {MaxBodyBytes / (1024 * 1024)} MB of the body were kept.");
            return new ApiResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString(),
                HttpVersion = "HTTP/" + response.Version,
                Headers = response.Headers.Concat(response.Content.Headers).SelectMany(header => header.Value.Select(value => new KeyValue(header.Key, value))).ToList(),
                Cookies = request.Settings.UseCookies ? Cookies.GetCookies(prepared.Address).Select(cookie => new KeyValue(cookie.Name, cookie.Value) { Description = $"{cookie.Domain}{cookie.Path}" + (cookie.Expires == DateTime.MinValue ? "" : " · expires " + cookie.Expires.ToString("g", System.Globalization.CultureInfo.CurrentCulture)) }).ToList() : [],
                Body = body,
                Truncated = truncated,
                ElapsedMilliseconds = clock.ElapsedMilliseconds,
                HeadersMilliseconds = headersAt,
                MediaType = mediaType,
                Text = text,
                Pretty = pretty,
                IsJson = isJson,
                Address = response.RequestMessage?.RequestUri ?? prepared.Address,
                Proxy = proxy.Describe(),
                Warnings = warnings,
            };
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5359", Justification = "Only for a request whose sender turned certificate verification off, which is on by default, to reach a development server with a self-signed certificate.")]
    private HttpClient ClientFor(HandlerKey key)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var existing)) return existing;
            if (_clients.Count >= MaxHandlers)
            {
                foreach (var client in _clients.Values) client.Dispose();
                _clients.Clear();
            }
            var handler = new SocketsHttpHandler
            {
                Proxy = key.Proxy.CreateProxy(),
                UseProxy = true,
                AllowAutoRedirect = key.FollowRedirects,
                MaxAutomaticRedirections = 10,
                UseCookies = key.UseCookies,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            if (key.UseCookies) handler.CookieContainer = Cookies;
            if (!key.VerifyCertificates) handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = static (_, _, _, _) => true };
            var created = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            _clients[key] = created;
            return created;
        }
    }

    private static async Task<(byte[] Body, bool Truncated)> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return (buffer.ToArray(), false);
            var room = MaxBodyBytes - (int)buffer.Length;
            buffer.Write(chunk, 0, Math.Min(read, room));
            if (read > room) return (buffer.ToArray(), true);
        }
    }

    internal static string Decode(byte[] body, MediaTypeHeaderValue? contentType)
    {
        if (body.Length == 0) return "";
        var encoding = Encoding.UTF8;
        if (contentType?.CharSet is { Length: > 0 } charset)
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { }
        }
        var text = encoding.GetString(body);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    internal static (string Pretty, bool IsJson) Prettify(string text, string? mediaType, int length)
    {
        var trimmed = text.TrimStart();
        var looksJson = mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true || trimmed.StartsWith('{') || trimmed.StartsWith('[');
        if (length > MaxPrettyBytes) return (text, looksJson);
        if (looksJson)
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                using var output = new MemoryStream();
                using (var writer = new Utf8JsonWriter(output, IndentedJson)) document.WriteTo(writer);
                return (Encoding.UTF8.GetString(output.ToArray()), true);
            }
            catch (JsonException) { return (text, false); }
        }
        if (mediaType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true || trimmed.StartsWith("<?xml", StringComparison.Ordinal))
        {
            try { return (XDocument.Parse(text).ToString(), false); }
            catch (XmlException) { }
        }
        return (text, false);
    }

    private static string Explain(HttpRequestException exception, Uri address, EffectiveProxy proxy)
    {
        var inner = exception.InnerException;
        if (inner is AuthenticationException || exception.HttpRequestError == HttpRequestError.SecureConnectionError)
            return $"The secure connection to {address.Host} failed: {inner?.Message ?? exception.Message} If this is a development server whose certificate you trust, turn off certificate verification under Settings.";
        if (exception.HttpRequestError == HttpRequestError.ProxyTunnelError)
            return $"The proxy refused to connect to {address.Host}: {exception.Message} ({proxy.Describe()})";
        if (exception.HttpRequestError == HttpRequestError.NameResolutionError)
            return $"{address.Host} could not be found. Check the address{(proxy.Configuration.Mode == ProxyMode.None ? "" : " and the proxy (" + proxy.Describe() + ")")}.";
        if (exception.HttpRequestError == HttpRequestError.ConnectionError)
            return $"Could not connect to {(proxy.Configuration.Mode == ProxyMode.Manual ? "the proxy or " : "")}{address.Authority}: {inner?.Message ?? exception.Message} ({proxy.Describe()})";
        return exception.Message + (inner is null ? "" : " " + inner.Message) + " (" + proxy.Describe() + ")";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }

    private sealed record HandlerKey(ProxyConfiguration Proxy, bool VerifyCertificates, bool FollowRedirects, bool UseCookies);
}
