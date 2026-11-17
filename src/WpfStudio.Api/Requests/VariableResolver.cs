using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WpfStudio.Api.Models;

namespace WpfStudio.Api.Requests;

/// <summary>
/// Fills in <c>{{name}}</c> placeholders. The environment wins over the collection, so one collection can be sent to
/// development and production by switching environments. Names starting with <c>$</c> are generated each time:
/// <c>{{$guid}}</c>, <c>{{$timestamp}}</c>, <c>{{$isoTimestamp}}</c> and <c>{{$randomInt}}</c>.
/// </summary>
public sealed partial class VariableResolver
{
    private const int MaxDepth = 8;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public VariableResolver(IEnumerable<KeyValue>? collectionVariables = null, IEnumerable<KeyValue>? environmentVariables = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        foreach (var source in new[] { collectionVariables, environmentVariables })
            foreach (var variable in source ?? [])
                if (variable.Enabled && variable.Key.Trim().Length > 0) _values[variable.Key.Trim()] = variable.Value;
    }

    public static VariableResolver Empty { get; } = new();

    /// <summary>Names used in the text that have no value, so a request can say what is missing before it is sent.</summary>
    public IReadOnlyList<string> Unresolved(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var missing = new List<string>();
        foreach (Match match in Placeholder().Matches(text))
        {
            var name = match.Groups["name"].Value.Trim();
            if (!name.StartsWith('$') && !_values.ContainsKey(name) && !missing.Contains(name)) missing.Add(name);
        }
        return missing;
    }

    public bool TryGet(string name, out string value) => _values.TryGetValue(name, out value!);

    /// <summary>Replaces every known placeholder; unknown ones stay as typed so the problem is visible in what is sent.</summary>
    public string Resolve(string? text) => Resolve(text, 0);

    private string Resolve(string? text, int depth)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal)) return text ?? "";
        return Placeholder().Replace(text, match =>
        {
            var name = match.Groups["name"].Value.Trim();
            if (name.StartsWith('$')) return Dynamic(name) ?? match.Value;
            // A variable may refer to others ({{baseUrl}} = {{scheme}}://{{host}}), but not forever.
            return _values.TryGetValue(name, out var value) ? depth < MaxDepth ? Resolve(value, depth + 1) : value : match.Value;
        });
    }

    private string? Dynamic(string name) => name switch
    {
        "$guid" or "$uuid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$timestamp" => _time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "$isoTimestamp" => _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        "$randomInt" => RandomNumberGenerator.GetInt32(0, 1001).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    [GeneratedRegex(@"\{\{(?<name>[^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>
    /// Encodes the characters a URL component may not contain, leaving <c>%XX</c> escapes someone already typed alone. In a
    /// path segment <c>/</c> and <c>?</c> are encoded too, so a parameter's value stays one segment.
    /// </summary>
    public static string EncodeUrlComponent(string value, bool pathSegment = false, bool spaceAsPlus = false)
    {
        var builder = new StringBuilder(value.Length + 8);
        var bytes = new byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2])) { builder.Append(c); continue; }
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '!' or '$' or '\'' or '(' or ')' or '*' or ',' or ';' or ':' or '@') { builder.Append(c); continue; }
            if (c is '/' or '?' && !pathSegment) { builder.Append(c); continue; }
            if (c == ' ' && spaceAsPlus) { builder.Append('+'); continue; }
            var length = char.IsHighSurrogate(c) && i + 1 < value.Length
                ? Encoding.UTF8.GetBytes(value.AsSpan(i++, 2), bytes)
                : Encoding.UTF8.GetBytes(value.AsSpan(i, 1), bytes);
            for (var b = 0; b < length; b++) builder.Append('%').Append(bytes[b].ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
