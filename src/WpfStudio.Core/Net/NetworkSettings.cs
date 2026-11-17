namespace WpfStudio.Core.Net;

/// <summary>Where outgoing HTTP requests go.</summary>
public enum ProxyMode
{
    /// <summary>The proxy Windows is configured with, including automatic configuration scripts and HTTP_PROXY variables.</summary>
    System,
    /// <summary>Straight to the server.</summary>
    None,
    /// <summary>One proxy server given by address.</summary>
    Manual,
}

/// <summary>
/// The application's proxy, as saved in settings.json. The password is protected for the Windows account that saved it
/// and is only ever held in clear in memory, in a <see cref="ProxyConfiguration"/>.
/// </summary>
public sealed record NetworkSettings
{
    public ProxyMode ProxyMode { get; init; } = ProxyMode.System;
    /// <summary>The proxy server, such as <c>http://proxy.example.com:8080</c>. A missing scheme means http.</summary>
    public string ProxyAddress { get; init; } = "";
    /// <summary>Hosts that skip the proxy, separated by semicolons or commas: <c>*.corp.example.com; 10.*; build-server:8080</c>.</summary>
    public string ProxyBypass { get; init; } = "";
    /// <summary>Loopback addresses and single-label intranet names skip the proxy.</summary>
    public bool BypassLocalAddresses { get; init; } = true;
    /// <summary>Authenticate to the proxy as the signed-in Windows user (NTLM or Kerberos) instead of with a user name and password.</summary>
    public bool UseDefaultCredentials { get; init; }
    public string ProxyUserName { get; init; } = "";
    public string? ProtectedProxyPassword { get; init; }
}
