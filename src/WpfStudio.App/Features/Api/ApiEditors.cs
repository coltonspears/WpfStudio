using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;
using WpfStudio.Core.Net;

namespace WpfStudio.App.Features.Api;

/// <summary>An entry of a combo box: the value it stands for, what it is called, and a line about it.</summary>
public sealed record Choice<T>(T Value, string Label, string Description = "");

/// <summary>One editable row of a parameter, header, form or variable table.</summary>
public sealed partial class KeyValueRowViewModel : ObservableObject
{
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial string Key { get; set; } = "";
    [ObservableProperty] public partial string Value { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial bool Secret { get; set; }
    [ObservableProperty] public partial bool IsFile { get; set; }

    public event Action<KeyValueRowViewModel>? Changed;
    public bool IsBlank => Key.Length == 0 && Value.Length == 0 && Description.Length == 0;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Changed?.Invoke(this);
    }

    public static KeyValueRowViewModel From(KeyValue model) => new()
    {
        Enabled = model.Enabled, Key = model.Key, Value = model.Value, Description = model.Description, Secret = model.Secret, IsFile = model.IsFile,
    };

    public KeyValue ToModel() => new() { Enabled = Enabled, Key = Key, Value = Value, Description = Description, Secret = Secret, IsFile = IsFile };
}

/// <summary>
/// A table of rows that always ends with a blank one, as in Postman: typing into it adds the row and a new blank one
/// below. <see cref="Changed"/> fires for every edit, so the request and the address bar stay in step.
/// </summary>
public sealed partial class KeyValueTableViewModel : ObservableObject
{
    private readonly Func<Task<string?>>? _pickFile;
    private bool _loading;

    public KeyValueTableViewModel(string keyPlaceholder = "Key", string valuePlaceholder = "Value", bool allowFiles = false, bool allowSecrets = false, Func<Task<string?>>? pickFile = null)
    {
        KeyPlaceholder = keyPlaceholder; ValuePlaceholder = valuePlaceholder; AllowFiles = allowFiles; AllowSecrets = allowSecrets; _pickFile = pickFile;
        AddBlank();
    }

    public ObservableCollection<KeyValueRowViewModel> Rows { get; } = [];
    public string KeyPlaceholder { get; }
    public string ValuePlaceholder { get; }
    public bool AllowFiles { get; }
    public bool AllowSecrets { get; }
    /// <summary>Enabled rows with a name, for the count beside a tab's title.</summary>
    public int Count => Rows.Count(row => row.Enabled && row.Key.Length > 0);
    public string CountLabel => Count == 0 ? "" : Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public event Action? Changed;

    public void Load(IEnumerable<KeyValue> rows)
    {
        _loading = true;
        try
        {
            foreach (var row in Rows) row.Changed -= RowChanged;
            Rows.Clear();
            foreach (var row in rows) Add(KeyValueRowViewModel.From(row));
            AddBlank();
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(CountLabel));
    }

    public List<KeyValue> ToModels() => Rows.Where(row => !row.IsBlank).Select(row => row.ToModel()).ToList();

    [RelayCommand]
    private void Remove(KeyValueRowViewModel? row)
    {
        if (row is null || ReferenceEquals(row, Rows.LastOrDefault()) && row.IsBlank) return;
        row.Changed -= RowChanged;
        Rows.Remove(row);
        if (Rows.Count == 0 || !Rows[^1].IsBlank) AddBlank();
        Notify();
    }

    [RelayCommand]
    private async Task BrowseFileAsync(KeyValueRowViewModel? row)
    {
        if (row is null || _pickFile is null) return;
        if (await _pickFile() is { } path) { row.IsFile = true; row.Value = path; if (row.Key.Length == 0) row.Key = System.IO.Path.GetFileNameWithoutExtension(path); }
    }

    private void Add(KeyValueRowViewModel row)
    {
        row.Changed += RowChanged;
        Rows.Add(row);
    }

    private void AddBlank() => Add(new KeyValueRowViewModel());

    private void RowChanged(KeyValueRowViewModel row)
    {
        if (_loading) return;
        if (ReferenceEquals(row, Rows.LastOrDefault()) && !row.IsBlank) AddBlank();
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(CountLabel));
        Changed?.Invoke();
    }
}

/// <summary>How a request or collection signs in.</summary>
public sealed partial class AuthEditorViewModel : ObservableObject
{
    public AuthEditorViewModel(bool allowInherit) =>
        Kinds = allowInherit
            ? [new(AuthKind.Inherit, "Inherit from collection", "Uses the collection's sign-in."), .. Common]
            : Common;

    private static readonly Choice<AuthKind>[] Common =
    [
        new(AuthKind.None, "No auth", "Sends no credentials."),
        new(AuthKind.Bearer, "Bearer token", "Sends Authorization: Bearer and the token. Use {{variables}} to keep tokens out of requests."),
        new(AuthKind.Basic, "Basic auth", "Sends the user name and password, base64-encoded, in Authorization."),
        new(AuthKind.ApiKey, "API key", "Sends a key in a header or the query string."),
    ];

    public IReadOnlyList<Choice<AuthKind>> Kinds { get; }
    public IReadOnlyList<Choice<ApiKeyLocation>> KeyLocations { get; } = [new(ApiKeyLocation.Header, "Header"), new(ApiKeyLocation.Query, "Query parameter")];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Explanation))] public partial AuthKind Kind { get; set; } = AuthKind.None;
    [ObservableProperty] public partial string Token { get; set; } = "";
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string KeyName { get; set; } = "X-API-Key";
    [ObservableProperty] public partial string KeyValue { get; set; } = "";
    [ObservableProperty] public partial ApiKeyLocation KeyLocation { get; set; } = ApiKeyLocation.Header;

    public string Explanation => Kinds.FirstOrDefault(choice => choice.Value == Kind)?.Description ?? "";
    public event Action? Changed;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(Explanation)) Changed?.Invoke();
    }

    public void Load(ApiAuth auth)
    {
        Kind = auth.Kind; Token = auth.Token; UserName = auth.UserName; Password = auth.Password; KeyName = auth.KeyName; KeyValue = auth.KeyValue; KeyLocation = auth.KeyLocation;
    }

    public ApiAuth ToModel() => new() { Kind = Kind, Token = Token, UserName = UserName, Password = Password, KeyName = KeyName, KeyValue = KeyValue, KeyLocation = KeyLocation };
}

/// <summary>Which proxy a request or collection uses instead of the one in Settings.</summary>
public sealed partial class ProxyEditorViewModel : ObservableObject
{
    private readonly bool _forRequest;

    public ProxyEditorViewModel(bool forRequest)
    {
        _forRequest = forRequest;
        Modes =
        [
            new(ProxyOverrideMode.Inherit, forRequest ? "Inherit (collection, then Settings)" : "Use Settings › Network", forRequest
                ? "Uses the collection's proxy, or the one in Settings › Network when the collection does not choose one."
                : "Uses the proxy chosen in Settings › Network."),
            new(ProxyOverrideMode.System, "System proxy", "Uses the proxy Windows is configured with, whatever Settings says."),
            new(ProxyOverrideMode.None, "No proxy", "Connects straight to the server, whatever Settings says."),
            new(ProxyOverrideMode.Custom, "Custom proxy", "Sends through the proxy server below."),
        ];
    }

    public IReadOnlyList<Choice<ProxyOverrideMode>> Modes { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Explanation), nameof(IsCustom), nameof(HasCredentials))] public partial ProxyOverrideMode Mode { get; set; } = ProxyOverrideMode.Inherit;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddressError))] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string Bypass { get; set; } = "";
    [ObservableProperty] public partial bool BypassLocalAddresses { get; set; } = true;
    [ObservableProperty] public partial bool UseDefaultCredentials { get; set; }
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    /// <summary>Where requests go now, worked out by the owner from Settings, the collection and this choice.</summary>
    [ObservableProperty] public partial string Effective { get; set; } = "";

    public bool IsCustom => Mode == ProxyOverrideMode.Custom;
    public bool HasCredentials => Mode is ProxyOverrideMode.Custom or ProxyOverrideMode.System;
    public string Explanation => Modes.FirstOrDefault(choice => choice.Value == Mode)?.Description ?? "";
    public string AddressError => IsCustom && !ProxyConfiguration.TryParseAddress(Address, out _, out var error) ? error : "";
    public string Scope => _forRequest ? "this request" : "every request in this collection";
    public event Action? Changed;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Effective) or nameof(Explanation) or nameof(AddressError) or nameof(IsCustom) or nameof(HasCredentials))) Changed?.Invoke();
        if (e.PropertyName == nameof(Mode)) OnPropertyChanged(nameof(AddressError));
    }

    public void Load(ProxyOverride proxy)
    {
        Mode = proxy.Mode; Address = proxy.Address; Bypass = proxy.Bypass; BypassLocalAddresses = proxy.BypassLocalAddresses;
        UseDefaultCredentials = proxy.UseDefaultCredentials; UserName = proxy.UserName; Password = proxy.Password;
    }

    public ProxyOverride ToModel() => new()
    {
        Mode = Mode, Address = Address, Bypass = Bypass, BypassLocalAddresses = BypassLocalAddresses, UseDefaultCredentials = UseDefaultCredentials, UserName = UserName, Password = Password,
    };

    public void Describe(EffectiveProxy proxy) => Effective = proxy.Describe();
}
