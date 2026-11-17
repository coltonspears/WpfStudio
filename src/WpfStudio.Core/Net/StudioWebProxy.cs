using System.Net;

namespace WpfStudio.Core.Net;

/// <summary>
/// The application's proxy as one long-lived object, so HTTP handlers built at startup follow later changes in Settings.
/// SocketsHttpHandler asks <see cref="IsBypassed"/> and <see cref="GetProxy"/> on every request, but reads
/// <see cref="Credentials"/> once when its first request is sent, so the credentials are a fixed object that looks up the
/// current ones each time the proxy asks.
/// </summary>
public sealed class StudioWebProxy : IWebProxy
{
    private readonly LiveCredentials _credentials;
    private volatile State _state;

    public StudioWebProxy() : this(ProxyConfiguration.SystemDefault) { }
    public StudioWebProxy(ProxyConfiguration configuration)
    {
        _state = State.For(configuration);
        _credentials = new(this);
    }

    /// <summary>The configuration requests use now.</summary>
    public ProxyConfiguration Current => _state.Configuration;

    /// <summary>Raised after <see cref="Update"/> replaces the configuration.</summary>
    public event Action<ProxyConfiguration>? Changed;

    /// <summary>Applies a new configuration to every request that starts from now on.</summary>
    public void Update(ProxyConfiguration configuration)
    {
        if (configuration == _state.Configuration) return;
        _state = State.For(configuration);
        Changed?.Invoke(configuration);
    }

    public ICredentials? Credentials
    {
        get => _credentials;
        // Credentials come from Settings; a handler has no business replacing them for everyone.
        set { }
    }

    public Uri? GetProxy(Uri destination) => _state.Proxy.GetProxy(destination);
    public bool IsBypassed(Uri host) => _state.Proxy.IsBypassed(host);

    private sealed record State(ProxyConfiguration Configuration, IWebProxy Proxy)
    {
        public static State For(ProxyConfiguration configuration)
        {
            // An unusable manual address must not break every HTTP client in the application: requests go direct until it is fixed.
            var proxy = configuration.Validate() is null ? configuration.CreateProxy() : ProxyConfiguration.Direct.CreateProxy();
            return new State(configuration, proxy);
        }
    }

    private sealed class LiveCredentials(StudioWebProxy owner) : ICredentials, ICredentialsByHost
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) => owner._state.Proxy.Credentials?.GetCredential(uri, authType);
        public NetworkCredential? GetCredential(string host, int port, string authenticationType) =>
            owner._state.Proxy.Credentials switch
            {
                ICredentialsByHost byHost => byHost.GetCredential(host, port, authenticationType),
                { } credentials => credentials.GetCredential(new UriBuilder("http", host, port).Uri, authenticationType),
                null => null,
            };
    }
}
