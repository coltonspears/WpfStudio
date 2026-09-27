using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WpfStudio.App.Features.ColtonGpt;

public sealed partial class AssistantSettingsViewModel(AssistantSettingsStore store, OpenRouterClient client) : ObservableObject
{
    private AssistantConfiguration _saved = new();
    private bool _loaded;
    public event Action? Saved;
    public ObservableCollection<AssistantModel> Models { get; } = [];
    [ObservableProperty] public partial string ModelId { get; set; } = "";
    [ObservableProperty] public partial string PendingApiKey { get; set; } = "";
    [ObservableProperty] public partial int MaxOutputTokens { get; set; } = 4096;
    [ObservableProperty] public partial bool UseTemperature { get; set; }
    [ObservableProperty] public partial double Temperature { get; set; } = 0.2;
    [ObservableProperty] public partial string Status { get; set; } = "Choose a model and save your OpenRouter key to get started.";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CredentialStatus))] public partial bool HasApiKey { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] [NotifyCanExecuteChangedFor(nameof(ClearKeyCommand))]
    public partial bool IsSaving { get; set; }
    public string CredentialStatus => HasApiKey ? "API key saved for this Windows user" : "No API key saved";
    public string SavedModelId => _saved.ModelId;
    public int SavedMaxOutputTokens => _saved.MaxOutputTokens;
    public double? SavedTemperature => _saved.UseTemperature ? _saved.Temperature : null;
    public bool IsReady => HasApiKey && !string.IsNullOrWhiteSpace(_saved.ModelId);

    public async Task LoadAsync(CancellationToken token = default)
    {
        if (_loaded) return;
        try
        {
            _saved = await store.LoadAsync(token);
            ModelId = _saved.ModelId; MaxOutputTokens = _saved.MaxOutputTokens;
            Temperature = _saved.Temperature; UseTemperature = _saved.UseTemperature;
            HasApiKey = !string.IsNullOrEmpty(_saved.ProtectedApiKey);
            Status = HasApiKey ? "Your key is protected with your Windows account. Leave the key field blank to keep it." : Status;
            _loaded = true;
            NotifySaved();
        }
        catch (Exception ex) when (IsRecoverable(ex)) { Status = ErrorMessage(ex); }
    }

    public string? GetApiKey() => store.ReadKey(_saved);

    private bool CanChangeSettings() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanChangeSettings))]
    private async Task SaveAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 200 || ModelId.Any(char.IsWhiteSpace))
        { Status = "Enter a model ID such as the ID from OpenRouter's model catalog."; return; }
        if (MaxOutputTokens is < 256 or > 16_384 || !double.IsFinite(Temperature) || Temperature is < 0 or > 2)
        { Status = "Output tokens must be 256–16,384 and temperature must be 0–2."; return; }
        string key = PendingApiKey.Trim();
        if (key.Length > 512 || key.Any(c => c < 33 || c > 126))
        { Status = "The API key contains invalid characters. Paste the key without spaces or line breaks."; return; }
        IsSaving = true;
        try
        {
            var updated = new AssistantConfiguration
            {
                ModelId = ModelId.Trim(), MaxOutputTokens = MaxOutputTokens, Temperature = Temperature, UseTemperature = UseTemperature,
                ProtectedApiKey = key.Length > 0 ? store.ProtectKey(key) : _saved.ProtectedApiKey
            };
            await store.SaveAsync(updated, token);
            _saved = updated; _loaded = true;
            HasApiKey = !string.IsNullOrEmpty(updated.ProtectedApiKey); PendingApiKey = "";
            Status = HasApiKey ? "Settings saved. ColtonGPT is ready; requests start only when you send a message." : "Settings saved. Add an API key before sending a message.";
            NotifySaved();
        }
        catch (Exception ex) when (IsRecoverable(ex)) { Status = ErrorMessage(ex); }
        finally { IsSaving = false; }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSettings))]
    private async Task ClearKeyAsync(CancellationToken token)
    {
        IsSaving = true;
        try
        {
            var updated = _saved with { ProtectedApiKey = null };
            await store.SaveAsync(updated, token);
            _saved = updated; PendingApiKey = ""; HasApiKey = false;
            Status = "Saved key removed. Existing conversation text remains only in memory.";
            NotifySaved();
        }
        catch (Exception ex) when (IsRecoverable(ex)) { Status = ErrorMessage(ex); }
        finally { IsSaving = false; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RefreshModelsAsync(CancellationToken token)
    {
        try
        {
            string? key = string.IsNullOrWhiteSpace(PendingApiKey) ? GetApiKey() : PendingApiKey.Trim();
            if (string.IsNullOrEmpty(key)) { Status = "Paste your OpenRouter API key first, then load models. You can also enter a model ID manually."; return; }
            if (key.Length > 512 || key.Any(c => c < 33 || c > 126)) { Status = "The API key contains invalid characters. Paste the key without spaces or line breaks."; return; }
            Status = "Loading the OpenRouter model catalog…";
            var models = await client.GetModelsAsync(key, token);
            Models.Clear(); foreach (var model in models) Models.Add(model);
            Status = $"Loaded {Models.Count} text models. Select one or enter its exact model ID, then save.";
        }
        catch (OperationCanceledException) { Status = "Model catalog request canceled."; }
        catch (Exception ex) when (IsRecoverable(ex)) { Status = ErrorMessage(ex); }
    }

    private void NotifySaved()
    {
        OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(SavedModelId));
        OnPropertyChanged(nameof(SavedMaxOutputTokens)); OnPropertyChanged(nameof(SavedTemperature));
        Saved?.Invoke();
    }

    internal static bool IsRecoverable(Exception ex) => ex is AssistantException or HttpRequestException or IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException;
    internal static string ErrorMessage(Exception ex) => ex is AssistantException ? ex.Message : ex switch
    {
        HttpRequestException => "Cannot reach OpenRouter. Check your network connection and try again.",
        CryptographicException => "Windows could not protect the API key. Try saving it again from this user account.",
        System.Text.Json.JsonException => "OpenRouter returned an invalid response. Try again later or enter a model ID manually.",
        _ => "ColtonGPT settings could not be accessed. Check that your local profile folder is writable and has free space."
    };
}
