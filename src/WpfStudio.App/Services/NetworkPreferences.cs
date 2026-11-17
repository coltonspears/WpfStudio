using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;
using WpfStudio.Api.Storage;
using WpfStudio.App.Features.Api;
using WpfStudio.Core.Net;

namespace WpfStudio.App.Services;

/// <summary>Protects secrets with Windows data protection, for the signed-in user only. Each purpose has its own entropy.</summary>
public sealed class WindowsSecretProtector(string purpose) : ISecretProtector
{
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes(purpose);

    public string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, _entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Unprotect(string value)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), _entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

/// <summary>
/// Settings › Network: the proxy WpfStudio's own HTTP requests use. Every change applies at once to the shared
/// <see cref="StudioWebProxy"/>, so the API client, the DevOps workbench, ColtonGPT and runtime downloads follow it without a restart.
/// </summary>
public sealed partial class NetworkPreferences : ObservableObject
{
    private readonly StudioWebProxy _proxy;
    private readonly ISecretProtector _protector;
    private bool _loading;

    public NetworkPreferences(StudioWebProxy proxy) : this(proxy, new WindowsSecretProtector("WpfStudio.Network.Proxy.v1")) { }
    public NetworkPreferences(StudioWebProxy proxy, ISecretProtector protector) { _proxy = proxy; _protector = protector; }

    public IReadOnlyList<Choice<ProxyMode>> Modes { get; } =
    [
        new(ProxyMode.System, "Use the system proxy", "Follows Windows' proxy settings, including automatic configuration scripts, and the HTTP_PROXY and HTTPS_PROXY variables."),
        new(ProxyMode.None, "No proxy", "Connects straight to every server."),
        new(ProxyMode.Manual, "Manual proxy", "Sends requests through the proxy server below, except to the hosts it skips."),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsManual), nameof(UsesProxy), nameof(Explanation), nameof(AddressError))] public partial ProxyMode Mode { get; set; } = ProxyMode.System;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddressError))] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string Bypass { get; set; } = "";
    [ObservableProperty] public partial bool BypassLocalAddresses { get; set; } = true;
    [ObservableProperty] public partial bool UseDefaultCredentials { get; set; }
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string TestAddress { get; set; } = "https://www.microsoft.com";
    [ObservableProperty] public partial string TestResult { get; set; } = "";
    /// <summary>Why a saved password could not be read, shown on the page.</summary>
    [ObservableProperty] public partial string Notice { get; set; } = "";

    public bool IsManual => Mode == ProxyMode.Manual;
    public bool UsesProxy => Mode != ProxyMode.None;
    public string Explanation => Modes.FirstOrDefault(choice => choice.Value == Mode)?.Description ?? "";
    public string AddressError => IsManual && !ProxyConfiguration.TryParseAddress(Address, out _, out var error) ? error + " Until it is fixed, requests go straight to the server, and the API client refuses to send." : "";
    public string Summary => Configuration().Describe();

    /// <summary>Raised for each change that should be saved.</summary>
    public event Action? Changed;

    public void Load(NetworkSettings settings)
    {
        _loading = true;
        try
        {
            Mode = settings.ProxyMode; Address = settings.ProxyAddress; Bypass = settings.ProxyBypass; BypassLocalAddresses = settings.BypassLocalAddresses;
            UseDefaultCredentials = settings.UseDefaultCredentials; UserName = settings.ProxyUserName; Notice = "";
            Password = "";
            if (!string.IsNullOrEmpty(settings.ProtectedProxyPassword))
            {
                try { Password = _protector.Unprotect(settings.ProtectedProxyPassword); }
                catch (Exception exception) when (exception is CryptographicException or FormatException) { Notice = "The saved proxy password was protected by another Windows user or computer. Enter it again."; }
            }
        }
        finally { _loading = false; }
        Apply();
    }

    public NetworkSettings ToSettings() => new()
    {
        ProxyMode = Mode, ProxyAddress = Address.Trim(), ProxyBypass = Bypass.Trim(), BypassLocalAddresses = BypassLocalAddresses,
        UseDefaultCredentials = UseDefaultCredentials, ProxyUserName = UserName.Trim(), ProtectedProxyPassword = Password.Length == 0 ? null : _protector.Protect(Password),
    };

    public ProxyConfiguration Configuration() => new(Mode, Address.Trim(), Bypass, BypassLocalAddresses, UseDefaultCredentials, UserName.Trim(), Password);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(TestAddress) or nameof(TestResult) or nameof(Notice) or nameof(Summary) or nameof(IsManual) or nameof(UsesProxy) or nameof(Explanation) or nameof(AddressError)) return;
        Apply();
        Changed?.Invoke();
    }

    private void Apply()
    {
        _proxy.Update(Configuration());
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Fetches an address through the proxy as configured, to show whether the settings work before relying on them.</summary>
    [RelayCommand]
    private async Task TestAsync(CancellationToken cancellationToken)
    {
        TestResult = "Connecting…";
        using var sender = new ApiSender(Configuration);
        try
        {
            var response = await sender.SendAsync(new ApiRequest { Method = "GET", Url = TestAddress.Trim(), Settings = new RequestSettings { TimeoutSeconds = 20, UseCookies = false } }, null, null, cancellationToken);
            TestResult = response.StatusCode == 407
                ? "The proxy asked for credentials it did not accept (407). Check the user name and password, or sign in as the Windows user."
                : $"Reached {response.Address?.Host} in {response.ElapsedMilliseconds:N0} ms: {response.StatusCode} {response.ReasonPhrase}. {response.Proxy}.";
        }
        catch (ApiRequestException exception) { TestResult = exception.Message; }
        catch (OperationCanceledException) { TestResult = "Cancelled"; }
    }
}
