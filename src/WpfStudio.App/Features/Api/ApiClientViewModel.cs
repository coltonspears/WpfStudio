using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;
using WpfStudio.Api.Storage;
using WpfStudio.Contracts;
using WpfStudio.Core.Net;

namespace WpfStudio.App.Features.Api;

/// <summary>
/// The API client: collections of saved requests, request tabs, environments, history, and C# model generation. It
/// sends through the proxy in Settings › Network unless a collection or request chooses another.
/// </summary>
public sealed partial class ApiClientViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly ApiEnvironment NoEnvironment = new() { Id = "", Name = "No environment" };
    private readonly ApiStore _store;
    private readonly IFileDialogService _files;
    private readonly IUserDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly StudioWebProxy _proxy;
    private readonly List<ApiCollection> _collections = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _sessionSave;
    private Task _initialization = Task.CompletedTask;
    private bool _initialized;
    private bool _restoring;
    private bool _disposed;
    private Action<string>? _openFile;

    public ApiClientViewModel(ApiStore store, StudioWebProxy proxy, IFileDialogService files, IUserDialogService dialogs, IClipboardService clipboard)
    {
        _store = store; _proxy = proxy; _files = files; _dialogs = dialogs; _clipboard = clipboard;
        Sender = new ApiSender(() => _proxy.Current);
        CodeGenerator = new ApiCodeGeneratorViewModel(this, store, files, clipboard);
        Environments.Add(NoEnvironment);
        ActiveEnvironment = NoEnvironment;
        _proxy.Changed += ProxySettingsChanged;
    }

    public ApiSender Sender { get; }
    public ApiCodeGeneratorViewModel CodeGenerator { get; }
    public ObservableCollection<ApiTreeNode> Tree { get; } = [];
    public ObservableCollection<ApiRequestTabViewModel> Tabs { get; } = [];
    public ObservableCollection<ApiEnvironment> Environments { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];
    public IReadOnlyList<ApiCollection> Collections => _collections;

    [ObservableProperty] public partial ApiTreeNode? SelectedNode { get; set; }
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTabs))] public partial ApiRequestTabViewModel? SelectedTab { get; set; }
    [ObservableProperty] public partial ApiEnvironment ActiveEnvironment { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Import an OpenAPI document or a Postman collection, or start a new request.";
    [ObservableProperty] public partial string SidebarView { get; set; } = "Collections";
    [ObservableProperty] public partial EnvironmentsEditorViewModel? EnvironmentsEditor { get; set; }
    [ObservableProperty] public partial CollectionSettingsViewModel? CollectionSettings { get; set; }
    [ObservableProperty] public partial SaveRequestViewModel? SaveDialog { get; set; }

    public bool HasTabs => Tabs.Count > 0;
    public bool HasCollections => _collections.Count > 0;
    public string ProxySummary => "Settings › Network: " + _proxy.Current.Describe();
    /// <summary>The folder model files are offered to be saved in: the open solution's.</summary>
    public string? WorkspaceDirectory { get; private set; }

    /// <summary>Connects the client to the shell, which opens files in editors.</summary>
    public void Attach(Action<string> openFile) => _openFile = openFile;

    public void SetWorkspace(WorkspaceSnapshot? workspace)
    {
        WorkspaceDirectory = workspace is null ? null : Path.GetDirectoryName(workspace.Path);
        CodeGenerator.WorkspaceChanged(workspace);
    }

    internal void OpenFile(string path) => _openFile?.Invoke(path);

    // ------------------------------------------------------------------ start and stop

    /// <summary>Loads collections, environments, history and the tabs that were open; the first call does the work, later ones wait for it.</summary>
    [RelayCommand]
    public Task InitializeAsync()
    {
        if (_initialized) return _initialization;
        _initialized = true;
        return _initialization = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var token = _lifetime.Token;
        _restoring = true;
        try
        {
            _collections.AddRange(await _store.LoadCollectionsAsync(token));
            var environments = await _store.LoadEnvironmentsAsync(token);
            foreach (var environment in environments.Environments) Environments.Add(environment);
            ActiveEnvironment = Environments.FirstOrDefault(environment => environment.Id.Length > 0 && environment.Id == environments.ActiveEnvironmentId) ?? NoEnvironment;
            foreach (var entry in await _store.LoadHistoryAsync(token)) History.Add(entry);
            RebuildTree();
            var session = await _store.LoadSessionAsync(token);
            foreach (var tab in session.Tabs)
            {
                // A saved request deleted since is reopened as a draft rather than lost.
                var collection = FindCollection(tab.CollectionId);
                var saved = tab.SavedRequestId is null ? null : collection?.AllRequests().FirstOrDefault(entry => entry.Request.Id == tab.SavedRequestId).Request;
                Tabs.Add(new ApiRequestTabViewModel(this, tab.Request, collection?.Id, saved?.Id, tab.IsDirty || saved is null && tab.SavedRequestId is not null));
            }
            SelectedTab = Tabs.ElementAtOrDefault(Math.Clamp(session.SelectedIndex, 0, Math.Max(0, Tabs.Count - 1)));
        }
        finally { _restoring = false; }
        OnPropertyChanged(nameof(HasTabs)); OnPropertyChanged(nameof(HasCollections));
        if (_store.Problems.Count > 0) Status = string.Join(" ", _store.Problems);
        else if (_collections.Count > 0) Status = _collections.Count == 1 ? "1 collection" : _collections.Count + " collections";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _proxy.Changed -= ProxySettingsChanged;
        _sessionSave?.Cancel();
        _sessionSave?.Dispose();
        _sessionSave = null;
        if (_initialized)
        {
            try { await _initialization; await SaveSessionNowAsync(CancellationToken.None); await CodeGenerator.SaveOptionsAsync(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        }
        _lifetime.Cancel();
        Sender.Dispose();
        _lifetime.Dispose();
    }

    private void ProxySettingsChanged(ProxyConfiguration configuration)
    {
        OnPropertyChanged(nameof(ProxySummary));
        foreach (var tab in Tabs) tab.DescribeProxy();
    }

    internal void Report(string status) => Status = status;

    // ------------------------------------------------------------------ tabs

    partial void OnSelectedTabChanged(ApiRequestTabViewModel? value) => ScheduleSessionSave();

    // Selecting a request whose tab is open shows that tab; double-click or Enter opens one.
    partial void OnSelectedNodeChanged(ApiTreeNode? value)
    {
        if (value?.Request is { } request && Tabs.FirstOrDefault(tab => tab.SavedRequestId == request.Id && tab.CollectionId == value.Collection.Id) is { } open) SelectedTab = open;
    }

    partial void OnActiveEnvironmentChanged(ApiEnvironment value)
    {
        // The combo box clears its selection while its list is replaced; requests then use no environment, not a missing one.
        if (value is null) { ActiveEnvironment = NoEnvironment; return; }
        if (_initialized && !_restoring) SaveEnvironmentsAsync().Forget(fault => Report("Environments could not be saved: " + fault.Message));
    }

    partial void OnFilterChanged(string value) { foreach (var node in Tree) node.ApplyFilter(value.Trim()); }

    [RelayCommand]
    private void NewRequest()
    {
        var request = new ApiRequest { Name = "Untitled request" };
        // Started from a collection: it inherits that collection's variables and sign-in when sent.
        var collection = SelectedNode?.Collection;
        Open(new ApiRequestTabViewModel(this, request, collection?.Id, null, isDirty: false));
        Status = collection is null ? "New request. Save it to a collection to keep it." : $"New request using {collection.Name}'s variables. Save it to keep it.";
    }

    internal void Open(ApiRequestTabViewModel tab)
    {
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(HasTabs));
        ScheduleSessionSave();
    }

    [RelayCommand]
    private async Task CloseTabAsync(ApiRequestTabViewModel? tab)
    {
        if (tab is null) return;
        if (tab.IsDirty)
        {
            var decision = await _dialogs.AskSaveAsync(tab.Name.Length > 0 ? tab.Name : "Untitled request");
            if (decision == SaveDecision.Cancel) return;
            if (decision == SaveDecision.Save && !await SaveTabAsync(tab, saveAs: false)) return;
        }
        tab.SendCancelCommand.Execute(null);
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null) SelectedTab = Tabs.ElementAtOrDefault(Math.Min(index, Tabs.Count - 1));
        OnPropertyChanged(nameof(HasTabs));
        ScheduleSessionSave();
    }

    internal void TabEdited(ApiRequestTabViewModel tab) => ScheduleSessionSave();

    // ------------------------------------------------------------------ sending

    internal async Task SendAsync(ApiRequestTabViewModel tab, CancellationToken cancellationToken)
    {
        var request = tab.ToModel();
        var collection = FindCollection(tab.CollectionId);
        var environment = ActiveEnvironment.Id.Length == 0 ? null : ActiveEnvironment;
        Status = $"Sending {request.Method} {request.Url}…";
        ApiResponse? response = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            response = await Sender.SendAsync(request, collection, environment, linked.Token);
            tab.Response = ApiResponseViewModel.From(response);
            Status = $"{response.StatusCode} {response.ReasonPhrase} in {response.ElapsedMilliseconds:N0} ms · {ApiResponseViewModel.FormatSize(response.Body.LongLength)}";
        }
        catch (ApiRequestException exception) { tab.Response = ApiResponseViewModel.Failed(exception.Message); Status = "The request failed: " + exception.Message; }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { tab.Response = ApiResponseViewModel.Failed("Cancelled before a response arrived."); Status = "Request cancelled"; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException) { tab.Response = ApiResponseViewModel.Failed(exception.Message); Status = "The request failed: " + exception.Message; }
        if (_lifetime.IsCancellationRequested) return;
        History.Insert(0, new HistoryEntry { Request = request, CollectionId = collection?.Id, StatusCode = response?.StatusCode, ElapsedMilliseconds = response?.ElapsedMilliseconds ?? 0 });
        while (History.Count > ApiStore.HistoryLimit) History.RemoveAt(History.Count - 1);
        await SaveQuietlyAsync(() => _store.SaveHistoryAsync(History, _lifetime.Token), "History");
    }

    internal void CopyCurl(ApiRequestTabViewModel tab)
    {
        try
        {
            var command = CurlExporter.Export(tab.ToModel(), FindCollection(tab.CollectionId), ActiveEnvironment.Id.Length == 0 ? null : ActiveEnvironment);
            Status = _clipboard.TrySetText(command) ? "Copied as a curl command, with variables filled in" : "Another application is using the clipboard. Try again.";
        }
        catch (ApiRequestException exception) { Status = exception.Message; }
    }

    [RelayCommand]
    private void CopyResponse()
    {
        if (SelectedTab?.Response is not { } response) return;
        Status = _clipboard.TrySetText(response.Body) ? "Response body copied" : "Another application is using the clipboard. Try again.";
    }

    [RelayCommand]
    private async Task SaveResponseAsync()
    {
        if (SelectedTab?.Response?.Response is not { } response) return;
        var path = await _files.SaveFileAsync("Save the response body", "All files (*.*)|*.*", SelectedTab.Response.SuggestedFileName);
        if (path is null) return;
        try
        {
            await File.WriteAllBytesAsync(path, response.Body, _lifetime.Token);
            Status = "Saved the response to " + path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = "The response could not be saved: " + exception.Message;
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void ClearCookies()
    {
        Sender.ClearCookies();
        Status = "Cookies cleared. The next requests start without any.";
    }

    // ------------------------------------------------------------------ history

    [RelayCommand]
    private void OpenHistory(HistoryEntry? entry)
    {
        if (entry is null) return;
        var request = ApiJson.Clone(entry.Request);
        request.Id = ApiRequest.NewId();
        Open(new ApiRequestTabViewModel(this, request, FindCollection(entry.CollectionId)?.Id, null, isDirty: false));
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (History.Count == 0 || !await _dialogs.ConfirmAsync("Clear history", "Forget every request sent so far? Saved requests stay.")) return;
        History.Clear();
        await SaveQuietlyAsync(() => _store.SaveHistoryAsync(History, _lifetime.Token), "History");
    }

    // ------------------------------------------------------------------ persistence

    private void ScheduleSessionSave()
    {
        if (!_initialized || _restoring || _disposed) return;
        _sessionSave?.Cancel();
        _sessionSave?.Dispose();
        var pending = _sessionSave = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SaveSessionLaterAsync(pending.Token).Forget(fault => Report("Open tabs could not be saved: " + fault.Message));
    }

    private async Task SaveSessionLaterAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(800, cancellationToken); }
        catch (OperationCanceledException) { return; }
        await SaveSessionNowAsync(cancellationToken);
    }

    private Task SaveSessionNowAsync(CancellationToken cancellationToken)
    {
        var session = new SessionFile
        {
            Tabs = Tabs.Select(tab => new SessionTab { Request = tab.ToModel(), CollectionId = tab.CollectionId, SavedRequestId = tab.SavedRequestId, IsDirty = tab.IsDirty }).ToList(),
            SelectedIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab)),
        };
        return _store.SaveSessionAsync(session, cancellationToken);
    }

    internal Task SaveEnvironmentsAsync() =>
        _store.SaveEnvironmentsAsync(new EnvironmentFile { Environments = Environments.Where(environment => environment.Id.Length > 0).ToList(), ActiveEnvironmentId = ActiveEnvironment.Id.Length == 0 ? null : ActiveEnvironment.Id }, _lifetime.Token);

    private async Task<bool> SaveQuietlyAsync(Func<Task> save, string what)
    {
        try { await save(); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Status = $"{what} could not be saved: {exception.Message}"; return false; }
        catch (OperationCanceledException) { return false; }
    }

    internal async Task<string?> PickFileAsync(string title) => await _files.OpenFileAsync(title, "All files (*.*)|*.*");

    internal Task<bool> ConfirmAsync(string title, string message) => _dialogs.ConfirmAsync(title, message);
}
