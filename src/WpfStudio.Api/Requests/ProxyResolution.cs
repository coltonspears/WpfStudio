using WpfStudio.Api.Models;
using WpfStudio.Core.Net;

namespace WpfStudio.Api.Requests;

/// <summary>The proxy a request is sent through, and which setting chose it.</summary>
public sealed record EffectiveProxy(ProxyConfiguration Configuration, string Source)
{
    public string Describe() => Configuration.Describe() + " · from " + Source;
}

/// <summary>A request's own proxy wins, then its collection's, then the application's in Settings.</summary>
public static class ProxyResolution
{
    public static EffectiveProxy Resolve(ProxyConfiguration application, ProxyOverride? collection, ProxyOverride? request)
    {
        if (request is { Mode: not ProxyOverrideMode.Inherit }) return new(ToConfiguration(request), "this request");
        if (collection is { Mode: not ProxyOverrideMode.Inherit }) return new(ToConfiguration(collection), "the collection");
        return new(application, "Settings › Network");
    }

    public static ProxyConfiguration ToConfiguration(ProxyOverride proxy) => proxy.Mode switch
    {
        ProxyOverrideMode.None => ProxyConfiguration.Direct,
        ProxyOverrideMode.Custom => new(ProxyMode.Manual, proxy.Address.Trim(), proxy.Bypass, proxy.BypassLocalAddresses, proxy.UseDefaultCredentials, proxy.UserName.Trim(), proxy.Password),
        ProxyOverrideMode.System => new(ProxyMode.System, UseDefaultCredentials: proxy.UseDefaultCredentials, UserName: proxy.UserName.Trim(), Password: proxy.Password),
        _ => throw new ArgumentOutOfRangeException(nameof(proxy), proxy.Mode, "Inherit has no configuration of its own."),
    };
}
