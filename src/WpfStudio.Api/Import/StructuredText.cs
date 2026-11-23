using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace WpfStudio.Api.Import;

/// <summary>Reads JSON or YAML into one JSON tree, so every importer works on a single shape.</summary>
public static partial class StructuredText
{
    public const int MaxCharacters = 64 * 1024 * 1024;
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };

    public static JsonNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxCharacters) throw new FormatException($"The document is larger than {MaxCharacters / (1024 * 1024)} MB.");
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try { return JsonNode.Parse(trimmed, documentOptions: Lenient) ?? throw new FormatException("The document is empty."); }
            catch (JsonException exception) { throw new FormatException($"The JSON could not be read: {exception.Message}", exception); }
        }
        return ParseYaml(trimmed);
    }

    public const int MaxYamlNodes = 250_000;

    private static JsonNode ParseYaml(string text)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (YamlException exception) { throw new FormatException($"The YAML could not be read at line {exception.Start.Line}, column {exception.Start.Column}: {exception.Message}", exception); }
        if (stream.Documents.Count == 0) throw new FormatException("The document is empty.");
        var nodes = 0;
        return Convert(stream.Documents[0].RootNode, 0, ref nodes) ?? throw new FormatException("The document is empty.");
    }

    private static JsonNode? Convert(YamlNode node, int depth, ref int nodes)
    {
        if (depth > 256) throw new FormatException("The YAML nests too deeply, or refers to itself through an alias.");
        if (++nodes > MaxYamlNodes) throw new FormatException($"The YAML expands to more than {MaxYamlNodes:N0} nodes.");
        switch (node)
        {
            case YamlMappingNode mapping:
                var result = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    var name = key is YamlScalarNode scalarKey ? scalarKey.Value ?? "" : key.ToString();
                    // YAML merge keys (<<: *defaults) copy the referenced mapping's entries in.
                    if (name == "<<" && key is YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.Plain } && Convert(value, depth + 1, ref nodes) is JsonObject merged)
                    {
                        foreach (var (mergedName, mergedValue) in merged)
                        {
                            if (++nodes > MaxYamlNodes) throw new FormatException($"The YAML expands to more than {MaxYamlNodes:N0} nodes.");
                            if (!result.ContainsKey(mergedName)) result[mergedName] = mergedValue?.DeepClone();
                        }
                        continue;
                    }
                    result[name] = Convert(value, depth + 1, ref nodes);
                }
                return result;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var item in sequence.Children) array.Add(Convert(item, depth + 1, ref nodes));
                return array;
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                return null;
        }
    }

    // The YAML 1.2 core schema: a plain scalar can be null, a boolean or a number; anything quoted or tagged !!str is text.
    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        var tag = scalar.Tag.IsEmpty ? "" : scalar.Tag.Value;
        if (tag.EndsWith(":str", StringComparison.Ordinal) || scalar.Style != YamlDotNet.Core.ScalarStyle.Plain) return JsonValue.Create(value);
        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL") return null;
        if (value is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (Integer().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
        if (Float().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return JsonValue.Create(number);
        return JsonValue.Create(value);
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Integer();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Float();
}

/// <summary>Follows local <c>$ref</c> pointers such as <c>#/components/schemas/Pet</c> within one document.</summary>
public static class JsonPointer
{
    public static JsonNode? Resolve(JsonNode root, string reference)
    {
        if (!reference.StartsWith('#')) return null;
        JsonNode? current = root;
        foreach (var raw in reference[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject obj => obj.TryGetPropertyValue(segment, out var child) ? child : null,
                JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count => array[index],
                _ => null,
            };
            if (current is null) return null;
        }
        return current;
    }

    /// <summary>The last segment of a reference: <c>Pet</c> for <c>#/components/schemas/Pet</c>.</summary>
    public static string Name(string reference) => Uri.UnescapeDataString(reference[(reference.LastIndexOf('/') + 1)..]).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}

/// <summary>Small helpers for reading optional values out of a JSON tree.</summary>
internal static class JsonNodeReading
{
    public static string? Text(this JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) && value is JsonValue scalar ? scalar.ToString() : null;

    public static bool Flag(this JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) && value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) && flag;

    public static JsonObject? Object(this JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value as JsonObject : null;

    public static JsonArray? Array(this JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value as JsonArray : null;

    /// <summary>A scalar as the text a parameter table holds; objects and arrays as compact JSON.</summary>
    public static string AsParameterText(this JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => node.ToJsonString(),
    };
}
