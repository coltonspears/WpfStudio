using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;

namespace WpfStudio.App.Features.Api;

/// <summary>
/// One open request: the address bar, its tables, body, sign-in, proxy and settings, and its latest response. The
/// address bar and the Params table are two views of the same query: editing either updates the other.
/// </summary>
public sealed partial class ApiRequestTabViewModel : ObservableObject
{
    public const string ParamsSection = "Params", HeadersSection = "Headers", BodySection = "Body", AuthSection = "Auth", ProxySection = "Proxy", SettingsSection = "Settings", DocsSection = "Docs";
    private readonly ApiClientViewModel _owner;
    private string _address = "";
    private bool _synchronizing;
    private bool _loading;

    internal ApiRequestTabViewModel(ApiClientViewModel owner, ApiRequest request, string? collectionId, string? savedRequestId, bool isDirty)
    {
        _owner = owner;
        Query = new("Parameter", "Value");
        PathParameters = new("Name", "Value");
        Headers = new("Header", "Value");
        Form = new("Field", "Value", allowFiles: true, pickFile: () => owner.PickFileAsync("Choose a file to upload"));
        Auth = new(allowInherit: true);
        Proxy = new(forRequest: true);
        Load(request, collectionId, savedRequestId);
        IsDirty = isDirty;
        Query.Changed += QueryChanged;
        PathParameters.Changed += Edited;
        Headers.Changed += Edited;
        Form.Changed += Edited;
        Auth.Changed += Edited;
        Proxy.Changed += () => { Edited(); DescribeProxy(); };
    }

    public string TabId { get; } = ApiRequest.NewId();
    public KeyValueTableViewModel Query { get; }
    public KeyValueTableViewModel PathParameters { get; }
    public KeyValueTableViewModel Headers { get; }
    public KeyValueTableViewModel Form { get; }
    public AuthEditorViewModel Auth { get; }
    public ProxyEditorViewModel Proxy { get; }
    public IReadOnlyList<string> Methods => ApiRequestBuilder.Methods;
    public IReadOnlyList<Choice<BodyMode>> BodyModes { get; } =
    [
        new(BodyMode.None, "None"), new(BodyMode.Json, "JSON"), new(BodyMode.Text, "Text"), new(BodyMode.Xml, "XML"),
        new(BodyMode.FormUrlEncoded, "Form (URL-encoded)"), new(BodyMode.Multipart, "Form data (multipart)"), new(BodyMode.File, "File"),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Method { get; set; } = "GET";
    [ObservableProperty] public partial string UrlText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTextBody), nameof(HasFormBody), nameof(HasFileBody), nameof(BodyLanguage), nameof(BodyLabel))] public partial BodyMode BodyMode { get; set; }
    [ObservableProperty] public partial string Body { get; set; } = "";
    [ObservableProperty] public partial string BodyFilePath { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial int TimeoutSeconds { get; set; } = 100;
    [ObservableProperty] public partial bool FollowRedirects { get; set; } = true;
    [ObservableProperty] public partial bool VerifyCertificates { get; set; } = true;
    [ObservableProperty] public partial bool UseCookies { get; set; } = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial bool IsDirty { get; set; }
    [ObservableProperty] public partial string Section { get; set; } = ParamsSection;
    /// <summary>Body, Headers, Cookies or Details: which part of the response is shown.</summary>
    [ObservableProperty] public partial string ResponseSection { get; set; } = "Body";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResponse))] public partial ApiResponseViewModel? Response { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CollectionName), nameof(Location))] public partial string? CollectionId { get; set; }
    [ObservableProperty] public partial string? SavedRequestId { get; set; }

    public string? RequestSchema { get; private set; }
    public string? ResponseSchema { get; private set; }
    public string Title => (Name.Length > 0 ? Name : "Untitled request") + (IsDirty ? " •" : "");
    public bool HasResponse => Response is not null;
    public bool HasTextBody => BodyMode is BodyMode.Json or BodyMode.Text or BodyMode.Xml;
    public bool HasFormBody => BodyMode is BodyMode.FormUrlEncoded or BodyMode.Multipart;
    public bool HasFileBody => BodyMode == BodyMode.File;
    public string BodyLanguage => BodyMode switch { BodyMode.Json => "json", BodyMode.Xml => "xml", _ => "text" };
    public string BodyLabel => BodyMode == BodyMode.None ? "" : "•";
    public string CollectionName => _owner.FindCollection(CollectionId)?.Name ?? "Not saved to a collection";
    public string Location => _owner.LocationOf(CollectionId, SavedRequestId);

    private void Load(ApiRequest request, string? collectionId, string? savedRequestId)
    {
        _loading = true;
        try
        {
            CollectionId = collectionId; SavedRequestId = savedRequestId;
            Name = request.Name; Method = request.Method; Body = request.Body; BodyMode = request.BodyMode; BodyFilePath = request.BodyFilePath; Description = request.Description;
            TimeoutSeconds = request.Settings.TimeoutSeconds; FollowRedirects = request.Settings.FollowRedirects; VerifyCertificates = request.Settings.VerifyCertificates; UseCookies = request.Settings.UseCookies;
            RequestSchema = request.RequestSchema; ResponseSchema = request.ResponseSchema;
            _address = request.Url;
            Query.Load(request.Query); PathParameters.Load(request.PathParameters); Headers.Load(request.Headers); Form.Load(request.Form);
            Auth.Load(request.Auth); Proxy.Load(request.Proxy);
            _synchronizing = true;
            try { UrlText = RequestUrl.Compose(_address, request.Query); }
            finally { _synchronizing = false; }
            Section = request.BodyMode != BodyMode.None && request.Query.Count == 0 ? BodySection : ParamsSection;
        }
        finally { _loading = false; }
        DescribeProxy();
    }

    public ApiRequest ToModel() => new()
    {
        Id = SavedRequestId ?? ApiRequest.NewId(),
        Name = Name.Trim().Length > 0 ? Name.Trim() : "Untitled request",
        Method = Method.Trim().ToUpperInvariant(),
        Url = _address,
        Query = Query.ToModels(),
        PathParameters = PathParameters.ToModels(),
        Headers = Headers.ToModels(),
        BodyMode = BodyMode,
        Body = Body,
        Form = Form.ToModels(),
        BodyFilePath = BodyFilePath,
        Auth = Auth.ToModel(),
        Proxy = Proxy.ToModel(),
        Settings = new RequestSettings { TimeoutSeconds = Math.Max(0, TimeoutSeconds), FollowRedirects = FollowRedirects, VerifyCertificates = VerifyCertificates, UseCookies = UseCookies },
        Description = Description,
        RequestSchema = RequestSchema,
        ResponseSchema = ResponseSchema,
    };

    /// <summary>After saving: this tab now edits that saved request.</summary>
    internal void MarkSaved(string collectionId, string requestId)
    {
        CollectionId = collectionId;
        SavedRequestId = requestId;
        IsDirty = false;
        OnPropertyChanged(nameof(Location));
    }

    internal void Refresh() { OnPropertyChanged(nameof(CollectionName)); OnPropertyChanged(nameof(Location)); DescribeProxy(); }

    internal void DescribeProxy() => Proxy.Describe(_owner.Sender.ProxyFor(ToModel(), _owner.FindCollection(CollectionId)));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        switch (e.PropertyName)
        {
            case nameof(UrlText): AddressTyped(); break;
            case nameof(Name) or nameof(Method) or nameof(Body) or nameof(BodyMode) or nameof(BodyFilePath) or nameof(Description) or nameof(TimeoutSeconds) or nameof(FollowRedirects) or nameof(VerifyCertificates) or nameof(UseCookies):
                Edited();
                break;
        }
    }

    private void Edited()
    {
        if (_loading) return;
        IsDirty = true;
        _owner.TabEdited(this);
    }

    // The address bar was typed in: its query replaces the enabled rows, and disabled rows stay where they were.
    private void AddressTyped()
    {
        if (_synchronizing) return;
        _synchronizing = true;
        try
        {
            var (address, typed) = RequestUrl.Split(UrlText);
            _address = address;
            var disabled = Query.ToModels().Where(row => !row.Enabled).ToList();
            var existing = Query.ToModels();
            foreach (var row in typed) row.Description = existing.FirstOrDefault(old => old.Enabled && old.Key == row.Key)?.Description ?? "";
            Query.Load(typed.Concat(disabled));
            PathParameters.Load(RequestUrl.SynchronizePathParameters(address, PathParameters.ToModels()));
        }
        finally { _synchronizing = false; }
        Edited();
    }

    private void QueryChanged()
    {
        if (_synchronizing) return;
        _synchronizing = true;
        try { UrlText = RequestUrl.Compose(_address, Query.ToModels()); }
        finally { _synchronizing = false; }
        Edited();
    }

    [RelayCommand] private void ShowSection(string? section) { if (!string.IsNullOrEmpty(section)) Section = section; }

    [RelayCommand(IncludeCancelCommand = true)]
    private Task SendAsync(CancellationToken cancellationToken) => _owner.SendAsync(this, cancellationToken);

    [RelayCommand] private Task SaveAsync() => _owner.SaveTabAsync(this, saveAs: false);
    [RelayCommand] private Task SaveAsAsync() => _owner.SaveTabAsync(this, saveAs: true);
    [RelayCommand] private void CopyCurl() => _owner.CopyCurl(this);
    [RelayCommand] private void GenerateModels() => _owner.OpenCodeGenerator(this);

    [RelayCommand]
    private async Task BrowseBodyFileAsync()
    {
        if (await _owner.PickFileAsync("Choose the file to send") is { } path) BodyFilePath = path;
    }

    /// <summary>Indents a JSON body, so a pasted one-liner can be read and edited.</summary>
    [RelayCommand]
    private void FormatBody()
    {
        if (BodyMode != BodyMode.Json || Body.Trim().Length == 0) return;
        try
        {
            using var document = JsonDocument.Parse(Body, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            Body = JsonSerializer.Serialize(document.RootElement, ApiJson.Readable);
        }
        catch (JsonException exception) { _owner.Report("The body is not valid JSON: " + exception.Message); }
    }
}
