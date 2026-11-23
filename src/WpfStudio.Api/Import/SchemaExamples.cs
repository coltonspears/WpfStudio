using System.Text.Json.Nodes;

namespace WpfStudio.Api.Import;

/// <summary>
/// Builds an example value from an OpenAPI or JSON Schema schema, the way Swagger UI fills in a request body: the
/// schema's own example when it has one, otherwise a placeholder of the right type for every property.
/// </summary>
public static class SchemaExamples
{
    private const int MaxDepth = 10;

    /// <param name="forRequest">Leaves out read-only properties, which a server fills in itself.</param>
    public static JsonNode? Generate(JsonNode? schema, JsonNode root, bool forRequest = true) => Generate(schema, root, forRequest, [], 0);

    private static JsonNode? Generate(JsonNode? schema, JsonNode root, bool forRequest, HashSet<string> visiting, int depth)
    {
        if (schema is not JsonObject obj || depth > MaxDepth) return null;
        if (obj.Text("$ref") is { } reference)
        {
            // A schema that contains itself (a tree node with children) stops at the second level.
            if (!visiting.Add(reference)) return null;
            try { return Generate(JsonPointer.Resolve(root, reference), root, forRequest, visiting, depth + 1); }
            finally { visiting.Remove(reference); }
        }
        if (obj.TryGetPropertyValue("example", out var example) && example is not null) return example.DeepClone();
        if (obj.Array("examples") is [{ } first, ..]) return first.DeepClone();
        if (obj.TryGetPropertyValue("const", out var constant)) return constant?.DeepClone();
        if (obj.TryGetPropertyValue("default", out var defaultValue) && defaultValue is not null) return defaultValue.DeepClone();
        if (obj.Array("enum") is [var firstValue, ..]) return firstValue?.DeepClone();
        if (obj.Array("allOf") is { Count: > 0 } allOf)
        {
            var merged = new JsonObject();
            foreach (var part in allOf)
                if (Generate(part, root, forRequest, visiting, depth + 1) is JsonObject piece)
                    foreach (var (name, value) in piece) merged[name] = value?.DeepClone();
            foreach (var (name, value) in Properties(obj, root, forRequest, visiting, depth)) merged[name] = value;
            return merged;
        }
        foreach (var keyword in new[] { "oneOf", "anyOf" })
            if (obj.Array(keyword) is { Count: > 0 } choices)
                return choices.Where(choice => TypeOf(choice) != "null").Select(choice => Generate(choice, root, forRequest, visiting, depth + 1)).FirstOrDefault(value => value is not null);
        var type = TypeOf(obj);
        switch (type)
        {
            case "object":
                var result = new JsonObject();
                foreach (var (name, value) in Properties(obj, root, forRequest, visiting, depth)) result[name] = value;
                if (result.Count == 0 && obj["additionalProperties"] is JsonObject additional)
                    result["key"] = Generate(additional, root, forRequest, visiting, depth + 1);
                return result;
            case "array":
                var items = Generate(obj["items"], root, forRequest, visiting, depth + 1);
                return items is null ? new JsonArray() : new JsonArray(items);
            case "integer": return JsonValue.Create(0);
            case "number": return JsonValue.Create(0.0);
            case "boolean": return JsonValue.Create(true);
            case "null": return null;
            case "string": return JsonValue.Create(StringFor(obj.Text("format")));
            default: return null;
        }
    }

    private static IEnumerable<(string Name, JsonNode? Value)> Properties(JsonObject schema, JsonNode root, bool forRequest, HashSet<string> visiting, int depth)
    {
        if (schema.Object("properties") is not { } properties) yield break;
        foreach (var (name, property) in properties)
        {
            var resolved = property is JsonObject p && p.Text("$ref") is { } reference ? JsonPointer.Resolve(root, reference) : property;
            if (forRequest && resolved.Flag("readOnly")) continue;
            if (!forRequest && resolved.Flag("writeOnly")) continue;
            yield return (name, Generate(property, root, forRequest, visiting, depth + 1));
        }
    }

    /// <summary>The schema's type, reading 3.1's list form (<c>["string", "null"]</c>) and inferring object or array from their keywords.</summary>
    public static string? TypeOf(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return null;
        if (obj["type"] is JsonValue single && single.TryGetValue<string>(out var name)) return name;
        if (obj["type"] is JsonArray several)
        {
            string? fallback = null;
            foreach (var entry in several)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (text != "null") return text;
                    fallback = "null";
                }
            }
            return fallback;
        }
        if (obj.ContainsKey("properties") || obj.ContainsKey("additionalProperties")) return "object";
        if (obj.ContainsKey("items")) return "array";
        return null;
    }

    private static string StringFor(string? format) => format switch
    {
        "date-time" => "2024-01-01T12:00:00Z",
        "date" => "2024-01-01",
        "time" => "12:00:00",
        "uuid" or "guid" => "3fa85f64-5717-4562-b3fc-2c963f66afa6",
        "email" => "user@example.com",
        "uri" or "url" or "uri-reference" => "https://example.com",
        "hostname" => "example.com",
        "ipv4" => "192.0.2.1",
        "ipv6" => "2001:db8::1",
        "byte" => "U3dhZ2dlciByb2Nrcw==",
        "binary" => "",
        "password" => "password",
        _ => "string",
    };
}
