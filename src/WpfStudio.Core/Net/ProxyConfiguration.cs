using System.Net;

namespace WpfStudio.Core.Net;

/// <summary>A proxy as requests use it: the mode, the server, what skips it, and how to sign in to it, with the password in clear.</summary>
public sealed record ProxyConfiguration(ProxyMode Mode, string Address = "", string Bypass = "", bool BypassLocalAddresses = true,
    bool UseDefaultCredentials = false, string UserName = "", string Password = "")
{
    public static ProxyConfiguration SystemDefault { get; } = new(ProxyMode.System);
    public static ProxyConfiguration Direct { get; } = new(ProxyMode.None);

    /// <summary>
    /// The proxy server's address. A bare <c>host:port</c> is read as http, which is how proxies are usually written and
    /// how HTTP_PROXY is read. Only http and https proxies are accepted: SOCKS needs a scheme of its own and a server that speaks it.
    /// </summary>
    public static bool TryParseAddress(string? text, out Uri? address, out string error)
    {
        address = null;
        text = text?.Trim() ?? "";
        if (text.Length == 0) { error = "Enter the proxy server's address, such as http://proxy.example.com:8080."; return false; }
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        { error = $"'{text}' is not a proxy address. Use host:port or http://host:port."; return false; }
        if (parsed.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        { error = $"A proxy address starts with http://, https:// or socks5://, not {parsed.Scheme}://."; return false; }
        if (parsed.AbsolutePath != "/" || parsed.Query.Length > 0)
        { error = "A proxy address is a server and port only, without a path or query."; return false; }
        address = parsed;
        error = "";
        return true;
    }

    /// <summary>Configuration problems that would make every request fail, or null when there are none.</summary>
    public string? Validate() => Mode == ProxyMode.Manual && !TryParseAddress(Address, out _, out var error) ? error : null;

    /// <summary>The proxy to hand to an HTTP handler. Credentials are those of this configuration, read once.</summary>
    public IWebProxy CreateProxy() => Mode switch
    {
        ProxyMode.None => DirectProxy.Instance,
        ProxyMode.Manual when TryParseAddress(Address, out var address, out _) => new ManualProxy(address!, ProxyBypassList.Parse(Bypass), BypassLocalAddresses) { Credentials = CreateCredentials() },
        // An unusable manual address fails the request loudly rather than quietly going direct.
        ProxyMode.Manual => throw new InvalidOperationException(Validate()),
        _ => new SystemProxy(CreateCredentials()),
    };

    /// <summary>The credentials offered when the proxy asks for them, or null when none are configured.</summary>
    public ICredentials? CreateCredentials()
    {
        if (UseDefaultCredentials) return CredentialCache.DefaultCredentials;
        if (UserName.Length == 0) return null;
        var (domain, user) = UserName.IndexOf('\\', StringComparison.Ordinal) is var slash and > 0 ? (UserName[..slash], UserName[(slash + 1)..]) : ("", UserName);
        return new NetworkCredential(user, Password, domain);
    }

    /// <summary>What a person reads to know where requests go, without the password.</summary>
    public string Describe() => Mode switch
    {
        ProxyMode.None => "No proxy",
        ProxyMode.Manual => Address.Trim().Length == 0 ? "Manual proxy (no address)" : "Proxy " + Address.Trim() + (UseDefaultCredentials ? " as the Windows user" : UserName.Length > 0 ? " as " + UserName : ""),
        _ => "System proxy" + (UseDefaultCredentials ? " as the Windows user" : UserName.Length > 0 ? " as " + UserName : ""),
    };

    /// <summary>Never prints the password, so a configuration can be logged or shown in a debugger safely.</summary>
    public override string ToString() => Describe();

    private sealed class DirectProxy : IWebProxy
    {
        public static readonly DirectProxy Instance = new();
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => null;
        public bool IsBypassed(Uri host) => true;
    }

    /// <summary>The operating system's proxy, with credentials of our own: the shared default proxy object is never changed.</summary>
    private sealed class SystemProxy(ICredentials? credentials) : IWebProxy
    {
        public ICredentials? Credentials { get; set; } = credentials;
        public Uri? GetProxy(Uri destination) => HttpClient.DefaultProxy.GetProxy(destination);
        public bool IsBypassed(Uri host) => HttpClient.DefaultProxy.IsBypassed(host);
    }

    private sealed class ManualProxy(Uri address, ProxyBypassList bypass, bool bypassLocal) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : address;
        public bool IsBypassed(Uri host) => (bypassLocal && ProxyBypassList.IsLocal(host)) || bypass.Matches(host);
    }
}
