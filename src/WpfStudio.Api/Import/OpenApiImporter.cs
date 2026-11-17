using System.Text.Json.Nodes;
using WpfStudio.Api.Models;

namespace WpfStudio.Api.Import;

/// <summary>A collection made from an OpenAPI document, the document to keep beside it, and what could not be carried over.</summary>
public sealed record OpenApiImportResult(ApiCollection Collection, string Specification, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns an OpenAPI 3.0 or 3.1 document, or a Swagger 2.0 one, into a collection: one folder per tag, one request per
/// operation, with path, query and header parameters, an example body, and the sign-in the document declares. The
/// server address becomes the <c>{{baseUrl}}</c> variable, so switching servers is one edit.
/// </summary>
public static class OpenApiImporter
{
    private static readonly string[] OperationMethods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    public static bool IsOpenApi(JsonNode? document) => document is JsonObject obj && (obj.ContainsKey("openapi") || obj.Text("swagger") is not null);

    public static OpenApiImportResult Import(JsonNode document, string location)
    {
        if (document is not JsonObject root || !IsOpenApi(root)) throw new FormatException("This is not an OpenAPI or Swagger document: it has neither an 'openapi' nor a 'swagger' field.");
        var warnings = new List<string>();
        var isSwagger2 = root.Text("swagger") is not null;
        var version = isSwagger2 ? "Swagger " + root.Text("swagger") : "OpenAPI " + root.Text("openapi");
        var info = root.Object("info");
        var collection = new ApiCollection
        {
            Name = info.Text("title") is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(location) is { Length: > 0 } file ? file : "Imported API",
            Description = info.Text("description") ?? "",
            Source = new ApiSource { Format = version, Location = location },
            HasSpecification = true,
        };
        collection.Variables.Add(new KeyValue("baseUrl", BaseUrl(root, isSwagger2, location, warnings)) { Description = "The server every request goes to" });
        var schemes = SecuritySchemes(root, isSwagger2);
        if (root.Array("security") is { } security && FirstScheme(security, schemes) is { } scheme)
            collection.Auth = AuthFor(scheme, collection.Variables);

        var tagDescriptions = (root.Array("tags") ?? []).OfType<JsonObject>().Where(tag => tag.Text("name") is not null)
            .GroupBy(tag => tag.Text("name")!).ToDictionary(group => group.Key, group => group.First().Text("description") ?? "", StringComparer.Ordinal);
        var folders = new Dictionary<string, ApiFolder>(StringComparer.Ordinal);
        foreach (var name in tagDescriptions.Keys) folders[name] = new ApiFolder { Name = name, Description = tagDescriptions[name] };

        var operations = 0;
        foreach (var (path, pathNode) in root.Object("paths") ?? [])
        {
            if (pathNode is not JsonObject pathItem) continue;
            if (pathItem.Text("$ref") is not null) { warnings.Add($"{path} refers to a path item in another file, which is not followed."); continue; }
            var shared = pathItem.Array("parameters");
            foreach (var method in OperationMethods)
            {
                if (pathItem.Object(method) is not { } operation) continue;
                operations++;
                var request = Request(root, isSwagger2, path, method, operation, shared, schemes, collection, warnings);
                var tag = operation.Array("tags") is [JsonValue first, ..] ? first.ToString() : null;
                if (tag is null) { collection.Requests.Add(request); continue; }
                if (!folders.TryGetValue(tag, out var folder)) folders[tag] = folder = new ApiFolder { Name = tag };
                folder.Requests.Add(request);
            }
        }
        collection.Folders.AddRange(folders.Values.Where(folder => folder.Requests.Count > 0));
        if (operations == 0) warnings.Add("The document describes no operations.");
        return new OpenApiImportResult(collection, root.ToJsonString(), warnings);
    }

    private static ApiRequest Request(JsonObject root, bool isSwagger2, string path, string method, JsonObject operation, JsonArray? shared,
        Dictionary<string, JsonObject> schemes, ApiCollection collection, List<string> warnings)
    {
        var summary = operation.Text("summary");
        var request = new ApiRequest
        {
            Name = summary is { Length: > 0 } ? summary : operation.Text("operationId") is { Length: > 0 } id ? id : method.ToUpperInvariant() + " " + path,
            Method = method.ToUpperInvariant(),
            Url = "{{baseUrl}}" + path,
            Description = string.Join(Environment.NewLine + Environment.NewLine, new[] { operation.Flag("deprecated") ? "Deprecated." : null, operation.Text("description") }.Where(text => !string.IsNullOrWhiteSpace(text))),
        };
        // Operation parameters replace path-level ones with the same name and location.
        var parameters = new Dictionary<(string, string), JsonObject>();
        foreach (var node in (shared ?? []).Concat(operation.Array("parameters") ?? []))
        {
            var parameter = Resolve(root, node);
            if (parameter is null) { warnings.Add($"{method.ToUpperInvariant()} {path}: a parameter refers to something outside this document."); continue; }
            if (parameter.Text("name") is { } name && parameter.Text("in") is { } location) parameters[(location, name)] = parameter;
        }
        var cookies = new List<string>();
        JsonObject? swaggerBody = null;
        var swaggerForm = new List<JsonObject>();
        foreach (var ((location, name), parameter) in parameters)
        {
            var value = ParameterExample(root, parameter, isSwagger2);
            var description = parameter.Text("description") ?? "";
            switch (location)
            {
                case "path": request.PathParameters.Add(new KeyValue(name, value) { Description = description }); break;
                case "query": request.Query.Add(new KeyValue(name, value, parameter.Flag("required")) { Description = description }); break;
                case "header": request.Headers.Add(new KeyValue(name, value, parameter.Flag("required")) { Description = description }); break;
                case "cookie": cookies.Add(name + "=" + value); break;
                case "body": swaggerBody = parameter; break;
                case "formData": swaggerForm.Add(parameter); break;
            }
        }
        if (cookies.Count > 0) request.Headers.Add(new KeyValue("Cookie", string.Join("; ", cookies)));
        // Path parameters the document forgot to declare still get a row, so the address can be filled in.
        foreach (var name in Requests.RequestUrl.PathParameterNames(path).Where(name => !request.PathParameters.Any(row => row.Key == name)))
            request.PathParameters.Add(new KeyValue(name, ""));

        if (isSwagger2) SwaggerBody(root, operation, swaggerBody, swaggerForm, request);
        else if (Resolve(root, operation["requestBody"]) is { } body) OpenApiBody(root, body, request);

        request.ResponseSchema = ResponseSchemaName(root, operation, isSwagger2);
        if (operation.Array("security") is { } security)
        {
            if (security.Count == 0) request.Auth = new ApiAuth { Kind = AuthKind.None };
            else if (FirstScheme(security, schemes) is { } scheme)
            {
                var auth = AuthFor(scheme, collection.Variables);
                if (!SameAuth(auth, collection.Auth)) request.Auth = auth;
            }
        }
        return request;
    }

    private static void OpenApiBody(JsonObject root, JsonObject body, ApiRequest request)
    {
        if (body.Object("content") is not { Count: > 0 } content) return;
        var (mediaType, media) = content.Select(entry => (entry.Key, entry.Value as JsonObject)).OrderBy(entry => MediaRank(entry.Key)).First();
        var schema = media?["schema"];
        request.RequestSchema = SchemaName(schema);
        if (mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            request.BodyMode = mediaType.Contains("multipart/", StringComparison.OrdinalIgnoreCase) ? BodyMode.Multipart : BodyMode.FormUrlEncoded;
            var resolved = Resolve(root, schema);
            foreach (var (name, property) in resolved.Object("properties") ?? [])
            {
                var propertySchema = Resolve(root, property);
                var isFile = propertySchema.Text("format") is "binary" or "base64" || propertySchema.Text("type") == "file";
                var required = resolved.Array("required")?.Any(entry => entry?.ToString() == name) == true;
                request.Form.Add(new KeyValue(name, isFile ? "" : SchemaExamples.Generate(property, root).AsParameterText(), required || isFile) { IsFile = isFile, Description = propertySchema.Text("description") ?? "" });
            }
            return;
        }
        var example = media?["example"]?.DeepClone() ?? (media.Object("examples")?.Select(entry => Resolve(root, entry.Value)?["value"]).FirstOrDefault(value => value is not null)?.DeepClone()) ?? SchemaExamples.Generate(schema, root);
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            request.BodyMode = BodyMode.Json;
            request.Body = example?.ToJsonString(ApiJson.Readable) ?? "{}";
            if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) request.Headers.Add(new KeyValue("Content-Type", mediaType));
        }
        else if (mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)) { request.BodyMode = BodyMode.Xml; request.Body = example.AsParameterText(); }
        else if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) { request.BodyMode = BodyMode.Text; request.Body = example.AsParameterText(); }
        else { request.BodyMode = BodyMode.File; request.Headers.Add(new KeyValue("Content-Type", mediaType)); }
    }

    private static void SwaggerBody(JsonObject root, JsonObject operation, JsonObject? body, List<JsonObject> form, ApiRequest request)
    {
        var consumes = (operation.Array("consumes") ?? root.Array("consumes") ?? []).Select(entry => entry?.ToString() ?? "").ToList();
        if (body is not null)
        {
            request.RequestSchema = SchemaName(body["schema"]);
            request.BodyMode = consumes.Count == 0 || consumes.Any(type => type.Contains("json", StringComparison.OrdinalIgnoreCase)) ? BodyMode.Json : consumes.Any(type => type.Contains("xml", StringComparison.OrdinalIgnoreCase)) ? BodyMode.Xml : BodyMode.Text;
            var example = SchemaExamples.Generate(body["schema"], root);
            request.Body = request.BodyMode == BodyMode.Json ? example?.ToJsonString(ApiJson.Readable) ?? "{}" : example.AsParameterText();
            return;
        }
        if (form.Count == 0) return;
        var multipart = form.Any(parameter => parameter.Text("type") == "file") || consumes.Any(type => type.Contains("multipart", StringComparison.OrdinalIgnoreCase));
        request.BodyMode = multipart ? BodyMode.Multipart : BodyMode.FormUrlEncoded;
        foreach (var parameter in form)
        {
            var isFile = parameter.Text("type") == "file";
            request.Form.Add(new KeyValue(parameter.Text("name") ?? "", isFile ? "" : ParameterExample(root, parameter, true), parameter.Flag("required") || isFile) { IsFile = isFile, Description = parameter.Text("description") ?? "" });
        }
    }

    private static int MediaRank(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ? 0
        : mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ? 1
        : mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ? 2
        : mediaType.Contains("multipart/", StringComparison.OrdinalIgnoreCase) ? 3
        : mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? 4
        : mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? 5 : 6;

    private static string ParameterExample(JsonObject root, JsonObject parameter, bool isSwagger2)
    {
        if (parameter.TryGetPropertyValue("example", out var example) && example is not null) return example.AsParameterText();
        if (parameter.Object("examples")?.Select(entry => Resolve(root, entry.Value)?["value"]).FirstOrDefault(value => value is not null) is { } fromExamples) return fromExamples.AsParameterText();
        // Swagger 2 keeps the type on the parameter itself; OpenAPI 3 moved it into a schema.
        var schema = isSwagger2 && parameter["schema"] is null ? parameter : parameter["schema"];
        if (schema is JsonObject obj && (obj.ContainsKey("example") || obj.ContainsKey("default") || obj.ContainsKey("enum") || obj.ContainsKey("examples") || obj.ContainsKey("$ref")))
            return SchemaExamples.Generate(schema, root).AsParameterText();
        return "";
    }

    private static string? ResponseSchemaName(JsonObject root, JsonObject operation, bool isSwagger2)
    {
        if (operation.Object("responses") is not { } responses) return null;
        var success = responses.Where(entry => entry.Key.StartsWith('2')).OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value)
            .Append(responses.TryGetPropertyValue("default", out var fallback) ? fallback : null).FirstOrDefault(value => value is not null);
        var response = Resolve(root, success);
        if (isSwagger2) return SchemaName(response?["schema"]);
        var content = response.Object("content");
        var media = content?.OrderBy(entry => MediaRank(entry.Key)).Select(entry => entry.Value).FirstOrDefault();
        return SchemaName(media?["schema"]);
    }

    /// <summary>The component a schema names, with <c>[]</c> after it for an array of them; null for an inline schema.</summary>
    private static string? SchemaName(JsonNode? schema)
    {
        if (schema.Text("$ref") is { } reference && reference.StartsWith('#')) return JsonPointer.Name(reference);
        if (SchemaExamples.TypeOf(schema) == "array" && schema.Object("items").Text("$ref") is { } item && item.StartsWith('#')) return JsonPointer.Name(item) + "[]";
        return null;
    }

    private static string BaseUrl(JsonObject root, bool isSwagger2, string location, List<string> warnings)
    {
        Uri.TryCreate(location, UriKind.Absolute, out var source);
        var fromWeb = source is { Scheme: "http" or "https" };
        if (isSwagger2)
        {
            var host = root.Text("host") ?? (fromWeb ? source!.Authority : null);
            var scheme = root.Array("schemes") is { Count: > 0 } schemes ? schemes.Select(entry => entry?.ToString()).OrderBy(entry => entry == "https" ? 0 : 1).First() : fromWeb ? source!.Scheme : "https";
            if (host is null) { warnings.Add("The document names no host. Set baseUrl in the collection's variables."); host = "localhost"; }
            return scheme + "://" + host + (root.Text("basePath") ?? "").TrimEnd('/');
        }
        if (root.Array("servers") is not [JsonObject server, ..] || server.Text("url") is not { } url)
        {
            if (fromWeb) return source!.GetLeftPart(UriPartial.Authority);
            warnings.Add("The document lists no servers. Set baseUrl in the collection's variables.");
            return "http://localhost";
        }
        foreach (var (name, variable) in server.Object("variables") ?? [])
            url = url.Replace("{" + name + "}", variable.Text("default") ?? "", StringComparison.Ordinal);
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return url.TrimEnd('/');
        if (fromWeb) return new Uri(source!, url).AbsoluteUri.TrimEnd('/');
        warnings.Add($"The server address '{url}' is relative to where the document is published. Set baseUrl to the server's full address.");
        return ("http://localhost" + (url.StartsWith('/') ? url : "/" + url)).TrimEnd('/');
    }

    private static Dictionary<string, JsonObject> SecuritySchemes(JsonObject root, bool isSwagger2)
    {
        var schemes = isSwagger2 ? root.Object("securityDefinitions") : root.Object("components").Object("securitySchemes");
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (name, scheme) in schemes ?? []) if (Resolve(root, scheme) is { } resolved) result[name] = resolved;
        return result;
    }

    private static JsonObject? FirstScheme(JsonArray requirements, Dictionary<string, JsonObject> schemes) =>
        requirements.OfType<JsonObject>().SelectMany(requirement => requirement.Select(entry => entry.Key))
            .Select(name => schemes.TryGetValue(name, out var scheme) ? scheme : null).FirstOrDefault(scheme => scheme is not null && AuthKindOf(scheme) != AuthKind.None);

    private static AuthKind AuthKindOf(JsonObject scheme) => (scheme.Text("type"), scheme.Text("scheme")?.ToUpperInvariant()) switch
    {
        ("http", "BEARER") or ("oauth2", _) or ("openIdConnect", _) => AuthKind.Bearer,
        ("http", "BASIC") or ("basic", _) => AuthKind.Basic,
        ("apiKey", _) when scheme.Text("in") is "header" or "query" => AuthKind.ApiKey,
        _ => AuthKind.None,
    };

    /// <summary>The sign-in a scheme asks for, its secret held in a collection variable so it is typed once.</summary>
    private static ApiAuth AuthFor(JsonObject scheme, List<KeyValue> variables)
    {
        void Variable(string name, string description)
        {
            if (!variables.Any(variable => variable.Key == name)) variables.Add(new KeyValue(name, "") { Secret = true, Description = description });
        }
        switch (AuthKindOf(scheme))
        {
            case AuthKind.Bearer:
                Variable("bearerToken", "The token sent as 'Authorization: Bearer'");
                return new ApiAuth { Kind = AuthKind.Bearer, Token = "{{bearerToken}}" };
            case AuthKind.Basic:
                Variable("username", "The user name for basic authentication");
                Variable("password", "The password for basic authentication");
                return new ApiAuth { Kind = AuthKind.Basic, UserName = "{{username}}", Password = "{{password}}" };
            case AuthKind.ApiKey:
                Variable("apiKey", "The API key");
                return new ApiAuth { Kind = AuthKind.ApiKey, KeyName = scheme.Text("name") ?? "X-API-Key", KeyValue = "{{apiKey}}", KeyLocation = scheme.Text("in") == "query" ? ApiKeyLocation.Query : ApiKeyLocation.Header };
            default:
                return new ApiAuth { Kind = AuthKind.None };
        }
    }

    private static bool SameAuth(ApiAuth a, ApiAuth b) => a.Kind == b.Kind && a.Token == b.Token && a.UserName == b.UserName && a.KeyName == b.KeyName && a.KeyLocation == b.KeyLocation;

    private static JsonObject? Resolve(JsonObject root, JsonNode? node)
    {
        for (var hops = 0; hops < 16 && node is JsonObject obj && obj.Text("$ref") is { } reference; hops++) node = JsonPointer.Resolve(root, reference);
        return node as JsonObject;
    }
}
