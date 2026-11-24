using System.Text;
using System.Text.RegularExpressions;
using WpfStudio.Api.Models;

namespace WpfStudio.Api.Requests;

/// <summary>
/// The address bar and the parameter tables describe the same thing. These split what is typed in the bar into an
/// address and query rows, put them back together, and find the path parameters, all on the raw text: nothing is encoded
/// or decoded until the request is sent, so <c>{{variables}}</c> survive.
/// </summary>
public static partial class RequestUrl
{
    /// <summary>Splits <c>https://host/path?a=1&amp;b</c> into the address and its query rows, as typed. A fragment is dropped: it is never sent.</summary>
    public static (string Address, List<KeyValue> Query) Split(string? text)
    {
        text ??= "";
        var hash = FragmentStart(text);
        if (hash >= 0) text = text[..hash];
        var question = text.IndexOf('?', StringComparison.Ordinal);
        if (question < 0) return (text, []);
        var rows = new List<KeyValue>();
        foreach (var pair in text[(question + 1)..].Split('&'))
        {
            if (pair.Length == 0) continue;
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            rows.Add(equals < 0 ? new KeyValue(pair, "") : new KeyValue(pair[..equals], pair[(equals + 1)..]));
        }
        return (text[..question], rows);
    }

    /// <summary>The address bar's text: the address and the enabled query rows.</summary>
    public static string Compose(string address, IEnumerable<KeyValue> query)
    {
        var builder = new StringBuilder(address);
        var first = true;
        foreach (var row in query)
        {
            if (!row.Enabled || row.Key.Length == 0 && row.Value.Length == 0) continue;
            builder.Append(first ? '?' : '&').Append(row.Key);
            if (row.Value.Length > 0) builder.Append('=').Append(row.Value);
            first = false;
        }
        return builder.ToString();
    }

    /// <summary>The names of <c>{id}</c> and <c>:id</c> path parameters, in order, each once.</summary>
    public static IReadOnlyList<string> PathParameterNames(string? address)
    {
        var names = new List<string>();
        foreach (Match match in PathParameter().Matches(PathPart(address ?? "")))
        {
            var name = match.Groups["brace"].Success ? match.Groups["brace"].Value : match.Groups["colon"].Value;
            if (!names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Fills path parameters with their encoded values; a parameter without a row stays as written.</summary>
    public static string FillPathParameters(string address, IReadOnlyList<KeyValue> parameters, Func<string, string> resolve)
    {
        var (authority, path, query, fragment) = SplitParts(address);
        var filled = PathParameter().Replace(path, match =>
        {
            var name = match.Groups["brace"].Success ? match.Groups["brace"].Value : match.Groups["colon"].Value;
            var row = parameters.FirstOrDefault(parameter => parameter.Enabled && parameter.Key == name);
            return row is null ? match.Value : VariableResolver.EncodeUrlComponent(resolve(row.Value), pathSegment: true);
        });
        return authority + filled + query + fragment;
    }

    /// <summary>
    /// Keeps the path parameter table in step with the address: rows for new names are added, rows whose name left the
    /// address are removed, and the values already typed stay.
    /// </summary>
    public static List<KeyValue> SynchronizePathParameters(string address, IReadOnlyList<KeyValue> existing)
    {
        var names = PathParameterNames(address);
        return names.Select(name => existing.FirstOrDefault(row => row.Key == name) ?? new KeyValue(name, "")).ToList();
    }

    // The scheme, host, query and fragment are not part of the path: "http://localhost:5000?a={x}#b" has no path parameters.
    private static string PathPart(string address) => SplitParts(address).Path;

    internal static (string Authority, string Path, string Query, string Fragment) SplitParts(string address)
    {
        var fragmentStart = FragmentStart(address);
        var fragment = fragmentStart >= 0 ? address[fragmentStart..] : "";
        var beforeFragment = fragmentStart >= 0 ? address[..fragmentStart] : address;
        var queryStart = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        var query = queryStart >= 0 ? beforeFragment[queryStart..] : "";
        var beforeQuery = queryStart >= 0 ? beforeFragment[..queryStart] : beforeFragment;

        var scheme = beforeQuery.IndexOf("://", StringComparison.Ordinal);
        var start = scheme >= 0 ? scheme + 3 : 0;
        // {{baseUrl}}/pets: the variable is the authority.
        if (scheme < 0 && beforeQuery.StartsWith("{{", StringComparison.Ordinal)) start = beforeQuery.IndexOf("}}", StringComparison.Ordinal) is var end and >= 0 ? end + 2 : 0;
        var slash = beforeQuery.IndexOf('/', start);
        return slash < 0 ? (beforeQuery, "", query, fragment) : (beforeQuery[..slash], beforeQuery[slash..], query, fragment);
    }

    private static int FragmentStart(string text)
    {
        // {{#each}}-like text is not a fragment; only a # outside braces is.
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth = Math.Max(0, depth - 1);
            else if (text[i] == '#' && depth == 0) return i;
        }
        return -1;
    }

    // {name} but not {{name}}; :name at the start of a segment.
    [GeneratedRegex(@"(?<!\{)\{(?<brace>[A-Za-z_][A-Za-z0-9_.\-]*)\}(?!\})|(?<=/):(?<colon>[A-Za-z_][A-Za-z0-9_\-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex PathParameter();
}
