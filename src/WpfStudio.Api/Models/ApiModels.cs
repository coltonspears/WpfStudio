using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfStudio.Api.Models;

/// <summary>What a request sends as its body.</summary>
public enum BodyMode { None, Json, Text, Xml, FormUrlEncoded, Multipart, File }

/// <summary>How a request signs in. <see cref="Inherit"/> uses its collection's.</summary>
public enum AuthKind { Inherit, None, Bearer, Basic, ApiKey }

public enum ApiKeyLocation { Header, Query }

/// <summary>Which proxy a collection or request uses. <see cref="Inherit"/> defers to the collection, and the collection to the application's settings.</summary>
public enum ProxyOverrideMode { Inherit, System, None, Custom }

/// <summary>One row of a parameter, header, form or variable table.</summary>
public sealed class KeyValue
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>A variable whose value is masked on screen, protected for this Windows user on disk, and left out of exports.</summary>
    public bool Secret { get; set; }
    /// <summary>A multipart form row whose value is the path of a file to upload.</summary>
    public bool IsFile { get; set; }

    public KeyValue() { }
    public KeyValue(string key, string value, bool enabled = true) { Key = key; Value = value; Enabled = enabled; }
}

public sealed class ApiAuth
{
    public AuthKind Kind { get; set; } = AuthKind.Inherit;
    public string Token { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string KeyName { get; set; } = "X-API-Key";
    public string KeyValue { get; set; } = "";
    public ApiKeyLocation KeyLocation { get; set; } = ApiKeyLocation.Header;
}

/// <summary>A proxy chosen for one collection or request instead of the application's.</summary>
public sealed class ProxyOverride
{
    public ProxyOverrideMode Mode { get; set; } = ProxyOverrideMode.Inherit;
    public string Address { get; set; } = "";
    public string Bypass { get; set; } = "";
    public bool BypassLocalAddresses { get; set; } = true;
    public bool UseDefaultCredentials { get; set; }
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class RequestSettings
{
    /// <summary>Seconds before the request is abandoned; 0 waits as long as it takes.</summary>
    public int TimeoutSeconds { get; set; } = 100;
    public bool FollowRedirects { get; set; } = true;
    /// <summary>Off only for a server whose certificate you know, such as a local development certificate.</summary>
    public bool VerifyCertificates { get; set; } = true;
    /// <summary>Keep cookies the server sets and send them back, as a browser would.</summary>
    public bool UseCookies { get; set; } = true;
}

public sealed class ApiRequest
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "New request";
    public string Method { get; set; } = "GET";
    /// <summary>The address without its query; <c>{{variables}}</c>, <c>{path}</c> and <c>:path</c> parameters are filled in when sent.</summary>
    public string Url { get; set; } = "";
    public List<KeyValue> Query { get; set; } = [];
    public List<KeyValue> PathParameters { get; set; } = [];
    public List<KeyValue> Headers { get; set; } = [];
    public BodyMode BodyMode { get; set; } = BodyMode.None;
    public string Body { get; set; } = "";
    public List<KeyValue> Form { get; set; } = [];
    public string BodyFilePath { get; set; } = "";
    public ApiAuth Auth { get; set; } = new();
    public ProxyOverride Proxy { get; set; } = new();
    public RequestSettings Settings { get; set; } = new();
    public string Description { get; set; } = "";
    /// <summary>For requests imported from OpenAPI: the schema names of the request body and the success response, for model generation.</summary>
    public string? RequestSchema { get; set; }
    public string? ResponseSchema { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N");
}

public sealed class ApiFolder
{
    public string Id { get; set; } = ApiRequest.NewId();
    public string Name { get; set; } = "New folder";
    public string Description { get; set; } = "";
    public List<ApiFolder> Folders { get; set; } = [];
    public List<ApiRequest> Requests { get; set; } = [];
}

/// <summary>Where a collection came from, when it was imported.</summary>
public sealed class ApiSource
{
    /// <summary>OpenAPI 3.1.0, Swagger 2.0, Postman v2.1 and so on.</summary>
    public string Format { get; set; } = "";
    /// <summary>The file or address it was read from.</summary>
    public string Location { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ApiCollection
{
    public string Id { get; set; } = ApiRequest.NewId();
    public string Name { get; set; } = "New collection";
    public string Description { get; set; } = "";
    public List<KeyValue> Variables { get; set; } = [];
    public ApiAuth Auth { get; set; } = new() { Kind = AuthKind.None };
    public ProxyOverride Proxy { get; set; } = new();
    public List<ApiFolder> Folders { get; set; } = [];
    public List<ApiRequest> Requests { get; set; } = [];
    public ApiSource? Source { get; set; }
    /// <summary>True when the OpenAPI document it was imported from is kept beside it, so models can be generated from its schemas.</summary>
    public bool HasSpecification { get; set; }

    /// <summary>Every request, depth first, with the folder path that leads to it.</summary>
    public IEnumerable<(ApiRequest Request, IReadOnlyList<ApiFolder> Path)> AllRequests()
    {
        foreach (var request in Requests) yield return (request, []);
        foreach (var folder in Folders)
            foreach (var entry in Walk(folder, [folder])) yield return entry;
        static IEnumerable<(ApiRequest, IReadOnlyList<ApiFolder>)> Walk(ApiFolder folder, List<ApiFolder> path)
        {
            foreach (var request in folder.Requests) yield return (request, path);
            foreach (var child in folder.Folders)
                foreach (var entry in Walk(child, [.. path, child])) yield return entry;
        }
    }
}

public sealed class ApiEnvironment
{
    public string Id { get; set; } = ApiRequest.NewId();
    public string Name { get; set; } = "New environment";
    public List<KeyValue> Variables { get; set; } = [];
}

/// <summary>A request as it was sent, and how it went.</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = ApiRequest.NewId();
    public ApiRequest Request { get; set; } = new();
    public string? CollectionId { get; set; }
    public int? StatusCode { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>The JSON shape of everything the API client saves, and deep copies by way of it.</summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>For JSON people read and edit, such as request bodies: indented, with text left as typed rather than escaped.</summary>
    public static JsonSerializerOptions Readable { get; } = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options) ?? throw new InvalidOperationException("The copy could not be made.");
}
