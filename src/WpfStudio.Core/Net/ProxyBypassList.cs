namespace WpfStudio.Core.Net;

/// <summary>
/// The hosts that skip a proxy, written the way Windows writes them: separated by semicolons or commas, <c>*</c> matching
/// any run of characters, an optional scheme and port, and <c>&lt;local&gt;</c> for single-label intranet names. Each entry
/// must match the whole host, unlike WebProxy's bypass list, whose unanchored regular expressions also match
/// <c>example.com.attacker.net</c> for <c>example.com</c>.
/// </summary>
public sealed class ProxyBypassList
{
    private readonly IReadOnlyList<Entry> _entries;
    private ProxyBypassList(IReadOnlyList<Entry> entries) => _entries = entries;

    public static ProxyBypassList Empty { get; } = new([]);
    public int Count => _entries.Count;

    public static ProxyBypassList Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        var entries = new List<Entry>();
        foreach (var raw in text.Split([';', ',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw.Trim();
            if (item.Equals("<local>", StringComparison.OrdinalIgnoreCase)) { entries.Add(new Entry(null, null, null, Local: true)); continue; }
            string? scheme = null;
            var schemeEnd = item.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0) { scheme = item[..schemeEnd]; item = item[(schemeEnd + 3)..]; }
            item = item.TrimEnd('/');
            int? port = null;
            // A port follows the last colon, unless the host is an IPv6 literal without brackets.
            var colon = item.LastIndexOf(':');
            var singleColon = colon > 0 && item.IndexOf(':', StringComparison.Ordinal) == colon;
            var afterBracket = item.StartsWith('[') && colon > item.IndexOf(']', StringComparison.Ordinal);
            if (singleColon || afterBracket)
            {
                if (int.TryParse(item[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) { port = parsed; item = item[..colon]; }
                else if (item[(colon + 1)..] == "*") item = item[..colon];
            }
            item = item.Trim('[', ']');
            if (item.Length > 0) entries.Add(new Entry(scheme, item, port, Local: false));
        }
        return new ProxyBypassList(entries);
    }

    public bool Matches(Uri destination)
    {
        foreach (var entry in _entries)
        {
            if (entry.Local) { if (IsSingleLabel(destination)) return true; continue; }
            if (entry.Scheme is not null && !entry.Scheme.Equals(destination.Scheme, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Port is { } port && port != destination.Port) continue;
            if (WildcardMatch(entry.Host!, destination.IdnHost.Trim('[', ']'))) return true;
        }
        return false;
    }

    /// <summary>Loopback, the local machine, or a single-label intranet name: what Windows means by "bypass for local addresses".</summary>
    public static bool IsLocal(Uri destination)
    {
        if (destination.IsLoopback) return true;
        var host = destination.IdnHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        return IsSingleLabel(destination);
    }

    private static bool IsSingleLabel(Uri destination) =>
        destination.HostNameType == UriHostNameType.Dns && !destination.IdnHost.Contains('.', StringComparison.Ordinal);

    /// <summary>Case-insensitive whole-string match where <c>*</c> stands for any run of characters, including none.</summary>
    internal static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private sealed record Entry(string? Scheme, string? Host, int? Port, bool Local);
}
