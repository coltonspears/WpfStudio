using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;
using WpfStudio.Contracts;
using WpfStudio.Core.Net;

namespace WpfStudio.App.Features.Api;

/// <summary>Where a request can be saved: a collection, or a folder in one.</summary>
public sealed record SaveTarget(ApiCollection? Collection, ApiFolder? Folder, string Label);

/// <summary>The answer of the save panel: the request's name and where it goes, or a new collection's name.</summary>
public sealed record SaveChoice(string Name, ApiCollection? Collection, ApiFolder? Folder, string NewCollectionName);

/// <summary>"Save request": a name, and a collection or folder to put it in, or a new collection.</summary>
public sealed partial class SaveRequestViewModel : ObservableObject
{
    private readonly TaskCompletionSource<SaveChoice?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly SaveTarget NewCollection = new(null, null, "New collection…");

    public SaveRequestViewModel(IReadOnlyList<ApiCollection> collections, ApiCollection? preferred, string name)
    {
        foreach (var collection in collections.OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Targets.Add(new SaveTarget(collection, null, collection.Name));
            AddFolders(collection, collection.Folders, collection.Name);
        }
        Targets.Add(NewCollection);
        Name = name.Trim().Length > 0 && name != "Untitled request" ? name.Trim() : "New request";
        SelectedTarget = Targets.FirstOrDefault(target => target.Collection == preferred && target.Folder is null) ?? Targets[0];

        void AddFolders(ApiCollection collection, List<ApiFolder> folders, string path)
        {
            foreach (var folder in folders)
            {
                var label = path + " › " + folder.Name;
                Targets.Add(new SaveTarget(collection, folder, label));
                AddFolders(collection, folder.Folders, label);
            }
        }
    }

    public ObservableCollection<SaveTarget> Targets { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial string Name { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNewCollection)), NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial SaveTarget SelectedTarget { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial string NewCollectionName { get; set; } = "My requests";
    public bool IsNewCollection => SelectedTarget == NewCollection;
    public Task<SaveChoice?> Completion => _completion.Task;

    private bool CanSave() => Name.Trim().Length > 0 && (!IsNewCollection || NewCollectionName.Trim().Length > 0);
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => _completion.TrySetResult(new SaveChoice(Name.Trim(), SelectedTarget.Collection, SelectedTarget.Folder, NewCollectionName.Trim()));
    [RelayCommand] private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>A collection's name, description, variables, sign-in and proxy, edited on a copy until saved.</summary>
public sealed partial class CollectionSettingsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ApiCollection _collection;
    private readonly ProxyConfiguration _application;

    public CollectionSettingsViewModel(ApiCollection collection, ProxyConfiguration application)
    {
        _collection = collection;
        _application = application;
        Name = collection.Name;
        Description = collection.Description;
        Variables = new("Variable", "Value", allowSecrets: true);
        Variables.Load(collection.Variables);
        Auth = new(allowInherit: false);
        Auth.Load(collection.Auth);
        Proxy = new(forRequest: false);
        Proxy.Load(collection.Proxy);
        Proxy.Changed += Describe;
        Describe();
        Source = collection.Source is { } source ? $"Imported from {source.Format} · {source.Location} · {source.ImportedAt.LocalDateTime:g}" + (collection.HasSpecification ? " · its schemas can generate C# models" : "") : "Created in WpfStudio";
    }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial string Name { get; set; }
    [ObservableProperty] public partial string Description { get; set; }
    [ObservableProperty] public partial string Section { get; set; } = "Variables";
    public KeyValueTableViewModel Variables { get; }
    public AuthEditorViewModel Auth { get; }
    public ProxyEditorViewModel Proxy { get; }
    public string Source { get; }
    public Task<bool> Completion => _completion.Task;

    private void Describe() => Proxy.Describe(ProxyResolution.Resolve(_application, Proxy.ToModel(), null));

    [RelayCommand] private void ShowSection(string? section) { if (!string.IsNullOrEmpty(section)) Section = section; }

    private bool CanSave() => Name.Trim().Length > 0;
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        _collection.Name = Name.Trim();
        _collection.Description = Description;
        _collection.Variables = Variables.ToModels();
        _collection.Auth = Auth.ToModel();
        _collection.Proxy = Proxy.ToModel();
        _completion.TrySetResult(true);
    }

    [RelayCommand] private void Cancel() => _completion.TrySetResult(false);
}

/// <summary>Environments: sets of variables, such as Development and Production, that requests switch between.</summary>
public sealed partial class EnvironmentsEditorViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IReadOnlyList<ApiEnvironment>?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IUserDialogService _dialogs;
    private ApiEnvironment? _shown;

    public EnvironmentsEditorViewModel(IReadOnlyList<ApiEnvironment> environments, ApiEnvironment active, IUserDialogService dialogs)
    {
        _dialogs = dialogs;
        foreach (var environment in environments) Items.Add(ApiJson.Clone(environment));
        Selected = Items.FirstOrDefault(environment => environment.Id == active.Id) ?? Items.FirstOrDefault();
    }

    public ObservableCollection<ApiEnvironment> Items { get; } = [];
    public KeyValueTableViewModel Variables { get; } = new("Variable", "Value", allowSecrets: true);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] public partial ApiEnvironment? Selected { get; set; }
    [ObservableProperty] public partial string SelectedName { get; set; } = "";
    public bool HasSelection => Selected is not null;
    public Task<IReadOnlyList<ApiEnvironment>?> Completion => _completion.Task;

    partial void OnSelectedChanged(ApiEnvironment? value)
    {
        Keep();
        _shown = value;
        SelectedName = value?.Name ?? "";
        Variables.Load(value?.Variables ?? []);
    }

    partial void OnSelectedNameChanged(string value)
    {
        if (_shown is not { } environment || value.Trim().Length == 0 || environment.Name == value.Trim()) return;
        environment.Name = value.Trim();
        // The list shows names; replacing the item refreshes it, and may clear the selection on the way.
        Keep();
        var index = Items.IndexOf(environment);
        if (index >= 0) { Items[index] = environment; Selected = environment; }
    }

    private void Keep()
    {
        if (_shown is not null) _shown.Variables = Variables.ToModels();
    }

    [RelayCommand]
    private void Add()
    {
        var environment = new ApiEnvironment { Name = Items.Count == 0 ? "Development" : "Environment " + (Items.Count + 1), Variables = [new KeyValue("baseUrl", "https://localhost:5001")] };
        Items.Add(environment);
        Selected = environment;
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is null) return;
        Keep();
        var copy = ApiJson.Clone(Selected);
        copy.Id = ApiRequest.NewId();
        copy.Name = Selected.Name + " (copy)";
        Items.Add(copy);
        Selected = copy;
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is not { } environment || !await _dialogs.ConfirmAsync("Delete environment", $"Delete the environment '{environment.Name}' and its variables?")) return;
        _shown = null;
        Items.Remove(environment);
        Selected = Items.FirstOrDefault();
    }

    [RelayCommand]
    private void Save()
    {
        Keep();
        _completion.TrySetResult(Items.ToList());
    }

    [RelayCommand] private void Cancel() => _completion.TrySetResult(null);
}
