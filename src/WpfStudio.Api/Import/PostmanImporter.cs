using System.Text.Json.Nodes;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;

namespace WpfStudio.Api.Import;

/// <summary>
/// Reads Postman collections (v2.0 and v2.1) and environments, so work saved in Postman comes across: folders, requests,
/// headers, bodies, sign-in and variables. Scripts and tests are Postman's own JavaScript and are left behind.
/// </summary>
public static class PostmanImporter
{
    public static bool IsCollection(JsonNode? document) =>
        document.Object("info").Text("schema")?.Contains("getpostman.com", StringComparison.OrdinalIgnoreCase) == true || document.Object("info").Text("_postman_id") is not null && document.Array("item") is not null;

    public static bool IsEnvironment(JsonNode? document) =>
        document.Array("values") is not null && (document.Text("_postman_variable_scope") == "environment" || document.Text("name") is not null && document.Array("item") is null && !OpenApiImporter.IsOpenApi(document));

    public static (ApiCollection Collection, IReadOnlyList<string> Warnings) ImportCollection(JsonNode document, string location)
    {
        var warnings = new List<string>();
        var info = document.Object("info");
        var collection = new ApiCollection
        {
            Name = info.Text("name") ?? "Imported collection",
            Description = Description(info?["description"]),
            Source = new ApiSource { Format = "Postman " + (info.Text("schema")?.Contains("v2.0", StringComparison.Ordinal) == true ? "v2.0" : "v2.1"), Location = location },
            Auth = Auth(document.Object("auth"), inheritByDefault: false),
        };
        foreach (var variable in (document.Array("variable") ?? []).OfType<JsonObject>())
            collection.Variables.Add(new KeyValue(variable.Text("key") ?? variable.Text("id") ?? "", variable["value"].AsParameterText(), !variable.Flag("disabled")) { Secret = variable.Text("type") == "secret" });
        var scripts = 0;
        Items(document.Array("item"), collection.Folders, collection.Requests, ref scripts);
        if (document.Array("event") is { Count: > 0 }) scripts++;
        if (scripts > 0) warnings.Add($"{scripts} pre-request or test script(s) were not imported: the API client sends requests as they are.");
        return (collection, warnings);
    }

    public static ApiEnvironment ImportEnvironment(JsonNode document) => new()
    {
        Name = document.Text("name") ?? "Imported environment",
        Variables = (document.Array("values") ?? []).OfType<JsonObject>()
            .Select(value => new KeyValue(value.Text("key") ?? "", value["value"].AsParameterText(), value["enabled"] is null || value.Flag("enabled")) { Secret = value.Text("type") == "secret" }).ToList(),
    };

    private static void Items(JsonArray? items, List<ApiFolder> folders, List<ApiRequest> requests, ref int scripts)
    {
        foreach (var item in (items ?? []).OfType<JsonObject>())
        {
            if (item.Array("event") is { Count: > 0 }) scripts++;
            if (item.Array("item") is { } children)
            {
                var folder = new ApiFolder { Name = item.Text("name") ?? "Folder", Description = Description(item["description"]) };
                Items(children, folder.Folders, folder.Requests, ref scripts);
                folders.Add(folder);
            }
            else if (item["request"] is { } request) requests.Add(Request(item.Text("name") ?? "Request", request));
        }
    }

    private static ApiRequest Request(string name, JsonNode node)
    {
        // A request can be just its address.
        if (node is JsonValue address) return Address(new ApiRequest { Name = name }, address.ToString(), null);
        var request = new ApiRequest
        {
            Name = name,
            Method = (node.Text("method") ?? "GET").ToUpperInvariant(),
            Description = Description(node["description"]),
            Auth = Auth(node.Object("auth"), inheritByDefault: true),
        };
        var url = node["url"];
        Address(request, url is JsonValue raw ? raw.ToString() : url.Text("raw") ?? Compose(url), url as JsonObject);
        foreach (var header in (node.Array("header") ?? []).OfType<JsonObject>())
            request.Headers.Add(new KeyValue(header.Text("key") ?? "", header.Text("value") ?? "", !header.Flag("disabled")) { Description = Description(header["description"]) });
        Body(request, node.Object("body"));
        return request;
    }

    private static ApiRequest Address(ApiRequest request, string raw, JsonObject? url)
    {
        var (address, query) = RequestUrl.Split(raw);
        request.Url = address;
        // The structured form keeps disabled query rows, which the raw text leaves out.
        if (url.Array("query") is { } rows)
            query = rows.OfType<JsonObject>().Select(row => new KeyValue(row.Text("key") ?? "", row.Text("value") ?? "", !row.Flag("disabled")) { Description = Description(row["description"]) }).ToList();
        request.Query = query;
        var values = (url.Array("variable") ?? []).OfType<JsonObject>().ToDictionary(row => row.Text("key") ?? "", row => row["value"].AsParameterText(), StringComparer.Ordinal);
        request.PathParameters = RequestUrl.PathParameterNames(address).Select(name => new KeyValue(name, values.GetValueOrDefault(name, ""))).ToList();
        return request;
    }

    private static string Compose(JsonNode? url)
    {
        if (url is not JsonObject obj) return "";
        static string Join(JsonNode? part, string separator) => part switch
        {
            JsonArray array => string.Join(separator, array.Select(segment => segment is JsonObject o ? o.Text("value") : segment?.ToString())),
            JsonValue value => value.ToString(),
            _ => "",
        };
        var protocol = obj.Text("protocol");
        var host = Join(obj["host"], ".");
        var port = obj.Text("port");
        var path = Join(obj["path"], "/");
        return (protocol is null ? "" : protocol + "://") + host + (port is null ? "" : ":" + port) + (path.Length == 0 ? "" : "/" + path.TrimStart('/'));
    }

    private static void Body(ApiRequest request, JsonObject? body)
    {
        if (body is null || body.Flag("disabled")) return;
        switch (body.Text("mode"))
        {
            case "raw":
                request.Body = body.Text("raw") ?? "";
                var language = body.Object("options").Object("raw").Text("language");
                var contentType = request.Headers.FirstOrDefault(header => header.Enabled && header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                request.BodyMode = language == "json" || contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ? BodyMode.Json
                    : language == "xml" || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? BodyMode.Xml : BodyMode.Text;
                break;
            case "urlencoded":
                request.BodyMode = BodyMode.FormUrlEncoded;
                request.Form = Rows(body.Array("urlencoded"));
                break;
            case "formdata":
                request.BodyMode = BodyMode.Multipart;
                request.Form = (body.Array("formdata") ?? []).OfType<JsonObject>().Select(row =>
                {
                    var isFile = row.Text("type") == "file";
                    var value = isFile ? row["src"] is JsonArray sources ? sources.FirstOrDefault()?.ToString() ?? "" : row.Text("src") ?? "" : row.Text("value") ?? "";
                    return new KeyValue(row.Text("key") ?? "", value, !row.Flag("disabled")) { IsFile = isFile, Description = Description(row["description"]) };
                }).ToList();
                break;
            case "file":
                request.BodyMode = BodyMode.File;
                request.BodyFilePath = body.Object("file").Text("src") ?? "";
                break;
            case "graphql":
                request.BodyMode = BodyMode.Json;
                var graphql = body.Object("graphql");
                var payload = new JsonObject { ["query"] = graphql.Text("query") ?? "" };
                if (graphql.Text("variables") is { Length: > 0 } variables)
                {
                    try { payload["variables"] = JsonNode.Parse(variables); }
                    catch (System.Text.Json.JsonException) { payload["variables"] = variables; }
                }
                request.Body = payload.ToJsonString(ApiJson.Readable);
                break;
        }
    }

    private static List<KeyValue> Rows(JsonArray? rows) =>
        (rows ?? []).OfType<JsonObject>().Select(row => new KeyValue(row.Text("key") ?? "", row.Text("value") ?? "", !row.Flag("disabled")) { Description = Description(row["description"]) }).ToList();

    private static ApiAuth Auth(JsonObject? auth, bool inheritByDefault)
    {
        if (auth is null) return new ApiAuth { Kind = inheritByDefault ? AuthKind.Inherit : AuthKind.None };
        var type = auth.Text("type");
        // v2.1 stores [{ key, value }] lists; v2.0 stored plain objects.
        string Value(string key) => auth[type ?? ""] switch
        {
            JsonArray list => list.OfType<JsonObject>().FirstOrDefault(entry => entry.Text("key") == key)?["value"].AsParameterText() ?? "",
            JsonObject map => map[key].AsParameterText(),
            _ => "",
        };
        return type switch
        {
            "noauth" => new ApiAuth { Kind = AuthKind.None },
            "bearer" => new ApiAuth { Kind = AuthKind.Bearer, Token = Value("token") },
            "basic" => new ApiAuth { Kind = AuthKind.Basic, UserName = Value("username"), Password = Value("password") },
            "apikey" => new ApiAuth { Kind = AuthKind.ApiKey, KeyName = Value("key") is { Length: > 0 } key ? key : "X-API-Key", KeyValue = Value("value"), KeyLocation = Value("in") == "query" ? ApiKeyLocation.Query : ApiKeyLocation.Header },
            "oauth2" => new ApiAuth { Kind = AuthKind.Bearer, Token = Value("accessToken") },
            _ => new ApiAuth { Kind = inheritByDefault ? AuthKind.Inherit : AuthKind.None },
        };
    }

    private static string Description(JsonNode? node) => node switch
    {
        JsonValue value => value.ToString(),
        JsonObject obj => obj.Text("content") ?? "",
        _ => "",
    };
}
