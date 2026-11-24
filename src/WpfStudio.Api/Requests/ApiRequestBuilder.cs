using System.Net.Http.Headers;
using System.Text;
using WpfStudio.Api.Models;

namespace WpfStudio.Api.Requests;

/// <summary>A request ready to send, the address it goes to, and what may be wrong with it.</summary>
public sealed record PreparedRequest(HttpRequestMessage Message, Uri Address, IReadOnlyList<string> Warnings) : IDisposable
{
    public void Dispose() => Message.Dispose();
}

/// <summary>Turns a saved request into an <see cref="HttpRequestMessage"/>: variables, path and query parameters, sign-in, headers and body.</summary>
public static class ApiRequestBuilder
{
    public const string UserAgent = "WpfStudio-ApiClient/1.0";
    public static IReadOnlyList<string> Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"];

    /// <summary>The sign-in a request uses: its own, or its collection's when it inherits.</summary>
    public static ApiAuth EffectiveAuth(ApiRequest request, ApiCollection? collection) =>
        request.Auth.Kind != AuthKind.Inherit ? request.Auth : collection?.Auth is { Kind: not AuthKind.Inherit } inherited ? inherited : new ApiAuth { Kind = AuthKind.None };

    /// <summary>The full address with variables, path parameters and enabled query rows filled in and encoded.</summary>
    public static Uri ResolveAddress(ApiRequest request, ApiCollection? collection, VariableResolver variables, List<string>? warnings = null)
    {
        var address = variables.Resolve(request.Url).Trim();
        address = RequestUrl.FillPathParameters(address, request.PathParameters, variables.Resolve);
        if (address.Length == 0) throw new ApiRequestException("Enter an address to send the request to.");
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
        var query = new StringBuilder();
        foreach (var row in request.Query.Where(row => row.Enabled && row.Key.Length > 0))
            AppendQuery(query, variables.Resolve(row.Key), variables.Resolve(row.Value));
        var auth = EffectiveAuth(request, collection);
        if (auth.Kind == AuthKind.ApiKey && auth.KeyLocation == ApiKeyLocation.Query && auth.KeyName.Length > 0)
            AppendQuery(query, variables.Resolve(auth.KeyName), variables.Resolve(auth.KeyValue));
        if (query.Length > 0)
        {
            var (authority, path, existingQuery, fragment) = RequestUrl.SplitParts(address);
            var separator = existingQuery.Length == 0 ? "?" : existingQuery is "?" || existingQuery.EndsWith('&') ? "" : "&";
            address = authority + path + existingQuery + separator + query + fragment;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            var missing = variables.Unresolved(request.Url);
            throw new ApiRequestException(missing.Count > 0
                ? $"The address uses {string.Join(", ", missing.Select(name => "{{" + name + "}}"))}, which no variable defines. Choose an environment or add the variable to the collection."
                : $"'{address}' is not an http or https address.");
        }
        if (warnings is not null)
            foreach (var name in RequestUrl.PathParameterNames(variables.Resolve(request.Url)).Where(name => !request.PathParameters.Any(row => row.Enabled && row.Key == name)))
                warnings.Add($"The path parameter '{name}' has no value.");
        return uri;
    }

    public static PreparedRequest Build(ApiRequest request, ApiCollection? collection, VariableResolver variables)
    {
        var warnings = new List<string>();
        var address = ResolveAddress(request, collection, variables, warnings);
        var method = request.Method.Trim().ToUpperInvariant();
        if (method.Length == 0 || method.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ApiRequestException($"'{request.Method}' is not an HTTP method.");
        var message = new HttpRequestMessage(new HttpMethod(method), address);
        try
        {
            var headers = request.Headers.Where(row => row.Enabled && row.Key.Trim().Length > 0)
                .Select(row => (Name: variables.Resolve(row.Key).Trim(), Value: variables.Resolve(row.Value))).ToList();
            message.Content = BuildContent(request, variables, warnings);
            foreach (var (name, value) in headers)
            {
                if (IsContentHeader(name))
                {
                    message.Content ??= new ByteArrayContent([]);
                    message.Content.Headers.Remove(name);
                    if (!message.Content.Headers.TryAddWithoutValidation(name, value)) warnings.Add($"The header '{name}' was not sent: '{value}' is not a valid value.");
                }
                else if (!message.Headers.TryAddWithoutValidation(name, value)) warnings.Add($"The header '{name}' was not sent: it is not a valid header name.");
            }
            ApplyAuth(message, EffectiveAuth(request, collection), variables, headers.Select(header => header.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (!message.Headers.Contains("User-Agent")) message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (!message.Headers.Contains("Accept")) message.Headers.TryAddWithoutValidation("Accept", "*/*");
            foreach (var text in new[] { request.Url, request.Body }.Concat(request.Headers.Where(row => row.Enabled).Select(row => row.Value)).Concat(request.Query.Where(row => row.Enabled).Select(row => row.Value)))
                foreach (var name in variables.Unresolved(text))
                {
                    var warning = $"{{{{{name}}}}} has no value and was sent as typed.";
                    if (!warnings.Contains(warning)) warnings.Add(warning);
                }
            return new PreparedRequest(message, address, warnings);
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private static void AppendQuery(StringBuilder query, string key, string value)
    {
        if (query.Length > 0) query.Append('&');
        query.Append(VariableResolver.EncodeUrlComponent(key)).Append('=').Append(VariableResolver.EncodeUrlComponent(value));
    }

    private static HttpContent? BuildContent(ApiRequest request, VariableResolver variables, List<string> warnings)
    {
        switch (request.BodyMode)
        {
            case BodyMode.Json: return Text(request.Body, "application/json");
            case BodyMode.Xml: return Text(request.Body, "application/xml");
            case BodyMode.Text: return Text(request.Body, "text/plain");
            case BodyMode.FormUrlEncoded:
                return new FormUrlEncodedContent(request.Form.Where(row => row.Enabled && row.Key.Length > 0)
                    .Select(row => new KeyValuePair<string, string>(variables.Resolve(row.Key), variables.Resolve(row.Value))).ToList());
            case BodyMode.Multipart:
                var multipart = new MultipartFormDataContent();
                try
                {
                    foreach (var row in request.Form.Where(row => row.Enabled && row.Key.Length > 0))
                    {
                        var name = variables.Resolve(row.Key);
                        if (!row.IsFile) { multipart.Add(new StringContent(variables.Resolve(row.Value)), name); continue; }
                        var path = variables.Resolve(row.Value).Trim('"');
                        if (!File.Exists(path)) throw new ApiRequestException($"The file for form field '{name}' does not exist: {path}");
                        var file = new StreamContent(File.OpenRead(path));
                        try
                        {
                            file.Headers.ContentType = new MediaTypeHeaderValue(MediaTypes.ForFile(path));
                            multipart.Add(file, name, Path.GetFileName(path));
                        }
                        catch
                        {
                            file.Dispose();
                            throw;
                        }
                    }
                    return multipart;
                }
                catch
                {
                    multipart.Dispose();
                    throw;
                }
            case BodyMode.File:
                var bodyPath = variables.Resolve(request.BodyFilePath).Trim('"');
                if (bodyPath.Length == 0) { warnings.Add("No file was chosen, so the body is empty."); return null; }
                if (!File.Exists(bodyPath)) throw new ApiRequestException("The body file does not exist: " + bodyPath);
                var content = new StreamContent(File.OpenRead(bodyPath));
                try
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypes.ForFile(bodyPath));
                    return content;
                }
                catch
                {
                    content.Dispose();
                    throw;
                }
            default:
                return null;
        }
        StringContent Text(string body, string mediaType) => new(variables.Resolve(body), new UTF8Encoding(false), mediaType);
    }

    private static void ApplyAuth(HttpRequestMessage message, ApiAuth auth, VariableResolver variables, HashSet<string> explicitHeaders)
    {
        // A header the person typed is what they meant to send.
        switch (auth.Kind)
        {
            case AuthKind.Bearer when !explicitHeaders.Contains("Authorization"):
                var token = variables.Resolve(auth.Token).Trim();
                if (token.Length > 0) message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                break;
            case AuthKind.Basic when !explicitHeaders.Contains("Authorization"):
                var pair = variables.Resolve(auth.UserName) + ":" + variables.Resolve(auth.Password);
                message.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
                break;
            case AuthKind.ApiKey when auth.KeyLocation == ApiKeyLocation.Header && auth.KeyName.Length > 0:
                var name = variables.Resolve(auth.KeyName).Trim();
                if (!explicitHeaders.Contains(name)) message.Headers.TryAddWithoutValidation(name, variables.Resolve(auth.KeyValue));
                break;
        }
    }

    internal static bool IsContentHeader(string name) => name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Expires", StringComparison.OrdinalIgnoreCase) || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase) || name.Equals("Allow", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A request that cannot be sent as it stands; the message says what to change.</summary>
public sealed class ApiRequestException : Exception
{
    public ApiRequestException() { }
    public ApiRequestException(string message) : base(message) { }
    public ApiRequestException(string message, Exception inner) : base(message, inner) { }
}

internal static class MediaTypes
{
    public static string ForFile(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".JSON" => "application/json",
        ".XML" => "application/xml",
        ".TXT" or ".LOG" or ".CSV" => "text/plain",
        ".HTML" or ".HTM" => "text/html",
        ".PNG" => "image/png",
        ".JPG" or ".JPEG" => "image/jpeg",
        ".GIF" => "image/gif",
        ".SVG" => "image/svg+xml",
        ".PDF" => "application/pdf",
        ".ZIP" => "application/zip",
        _ => "application/octet-stream",
    };
}
