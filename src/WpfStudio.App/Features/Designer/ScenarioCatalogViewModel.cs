using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Designer;

public sealed record DesignerScenario(PreviewScenario? Configuration)
{
    public string Name => Configuration?.Name ?? "Default";
}

/// <summary>A project's saved preview scenarios: the list, the selection, and whether the file still says what was loaded.</summary>
public sealed partial class ScenarioCatalogViewModel(IScenarioCatalogHost host, IUiDispatcher dispatcher) : ObservableObject
{
    private static readonly DesignerScenario DefaultScenario = new(null);
    private FileSystemWatcher? _watcher;
    private string? _projectDirectory;
    private string? _sourcePath;
    private bool _updating;
    private bool _loaded;
    public ObservableCollection<DesignerScenario> Items { get; } = [DefaultScenario];
    public ObservableCollection<string> Warnings { get; } = [];
    [ObservableProperty] public partial DesignerScenario? Selected { get; set; } = DefaultScenario;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConfigurationDescription))] public partial string? ConfigurationPath { get; set; }
    public string ConfigurationDescription => ConfigurationPath is { } path
        ? $"Saved configuration: {path}"
        : "Open a project view to use wpfstudio.preview.json scenarios.";
    public bool CanOpenConfiguration => ConfigurationPath is { } path && File.Exists(path);
    public string ConfigurationTitle => Warnings.Count == 0 ? "Scenario configuration"
        : $"Scenario configuration · {Warnings.Count} issue(s)";
    /// <summary>A configuration change waits for an explicit Refresh; live typing must not apply it.</summary>
    internal bool SelectionPending { get; set; }
    /// <summary>The hash of the catalog file last loaded; null when there was none.</summary>
    internal string? Fingerprint { get; private set; }

    partial void OnSelectedChanged(DesignerScenario? value)
    {
        if (!_updating)
        {
            SelectionPending = true;
            host.ScenarioChosen(value);
        }
        host.ScenariosChanged();
    }
    partial void OnConfigurationPathChanged(string? value) => NotifyConfiguration();
    private void NotifyConfiguration()
    {
        OnPropertyChanged(nameof(CanOpenConfiguration));
        OnPropertyChanged(nameof(ConfigurationTitle));
        OpenConfigurationCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanOpenConfiguration))]
    private void OpenConfiguration()
    {
        if (ConfigurationPath is { } path && File.Exists(path)) host.OpenSource(new(path, 0, 0, 1, 1));
    }
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (host.DocumentPath is null || host.IsDisposed) return;
        SelectionPending = true;
        host.InvalidatePreview();
        var revision = host.Revision;
        try
        {
            if (await LoadAsync(revision, host.Lifetime) && IsLatest(revision))
                host.ReportStatus(Warnings.FirstOrDefault() ?? "Scenarios reloaded. Select a scenario and Refresh to apply it.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (IsLatest(revision)) host.ReportStatus(exception.Message); }
    }
    /// <summary>Starts over for a document in a project: forgets the list and watches that project's configuration file.</summary>
    internal void Configure(string sourcePath, string? projectDirectory)
    {
        if (string.Equals(_sourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_projectDirectory, projectDirectory, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose(); _watcher = null;
        _sourcePath = sourcePath; _projectDirectory = projectDirectory;
        Fingerprint = null; _loaded = false;
        SelectionPending = false;
        ResetToDefault();
        Warnings.Clear();
        ConfigurationPath = string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.Combine(projectDirectory, PreviewScenarioCatalog.FileName);
        host.ScenariosChanged();
        if (projectDirectory is null || !Directory.Exists(projectDirectory)) return;
        try
        {
            var watcher = new FileSystemWatcher(projectDirectory, PreviewScenarioCatalog.FileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher = watcher;
            watcher.Changed += (_, _) => FileChanged(watcher);
            watcher.Created += (_, _) => FileChanged(watcher);
            watcher.Deleted += (_, _) => FileChanged(watcher);
            watcher.Renamed += (_, _) => FileChanged(watcher);
            watcher.Error += (_, _) => dispatcher.Post(() =>
            {
                if (host.IsDisposed || !ReferenceEquals(_watcher, watcher)) return;
                SelectionPending = true;
                host.InvalidatePreview();
                host.ReportStatus("Scenario configuration monitoring stopped. Refresh to verify the saved configuration.");
            });
            watcher.EnableRaisingEvents = true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private void FileChanged(FileSystemWatcher watcher) => dispatcher.Post(() => CheckFileAsync(watcher).Forget(host.ReportFault));
    private async Task CheckFileAsync(FileSystemWatcher watcher)
    {
        if (host.IsDisposed || !ReferenceEquals(_watcher, watcher)) return;
        var revision = host.Revision;
        try
        {
            if (host.DocumentPath is not { } document) return;
            var catalog = await PreviewScenarioCatalog.LoadAsync(_projectDirectory, document, host.Lifetime);
            if (!IsLatest(revision) || !ReferenceEquals(_watcher, watcher) || catalog.Fingerprint == Fingerprint) return;
            SelectionPending = true;
            host.InvalidatePreview();
            NotifyConfiguration();
            host.ReportStatus("Scenario configuration changed. Reload scenarios or Refresh to use the saved configuration.");
        }
        catch (OperationCanceledException) { }
    }
    // The transaction's final guard cannot await. Hash at most the catalog's
    // bounded input size so timestamp-preserving changes cannot bypass it.
    internal bool IsConfigurationCurrent()
    {
        if (ConfigurationPath is not { } path) return Fingerprint is null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Fingerprint is null) return false;
            byte[] bytes = new byte[262145];
            int length = 0;
            while (length < bytes.Length)
            {
                int read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            return length <= 262144 && Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length))) == Fingerprint;
        }
        catch (FileNotFoundException) { return Fingerprint is null; }
        catch (DirectoryNotFoundException) { return Fingerprint is null; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    /// <summary>True when the saved catalog is still the one the render used; otherwise invalidates the preview.</summary>
    internal async Task<bool> VerifyAsync(long revision, CancellationToken token)
    {
        if (host.DocumentPath is not { } document) return false;
        var catalog = await PreviewScenarioCatalog.LoadAsync(_projectDirectory, document, token);
        if (!IsLatest(revision)) return false;
        if (catalog.Fingerprint == Fingerprint) return true;
        host.InvalidatePreview();
        host.ReportStatus("Scenario configuration changed. Refresh to use the saved configuration.");
        return false;
    }
    /// <summary>Reads the saved catalog; false when the render should stop because the selected scenario is gone or the render is stale.</summary>
    internal async Task<bool> LoadAsync(long revision, CancellationToken token)
    {
        if (host.DocumentPath is not { } document) return false;
        var selected = Selected?.Configuration;
        var catalog = await PreviewScenarioCatalog.LoadAsync(_projectDirectory, document, token);
        if (!IsLatest(revision)) return false;
        // Live preview reloads the catalog on every render. An unchanged catalog keeps its
        // items so an open scenario list and its selection are not reset while typing.
        if (_loaded && catalog.Fingerprint == Fingerprint && Selected is not null) return true;
        Fingerprint = catalog.Fingerprint;
        Warnings.Clear();
        foreach (var warning in catalog.Warnings) Warnings.Add(warning);
        _updating = true;
        try
        {
            Items.Clear(); Items.Add(DefaultScenario);
            foreach (var scenario in catalog.Scenarios) Items.Add(new(scenario));
            Selected = selected is null ? DefaultScenario
                : Items.FirstOrDefault(scenario => scenario.Configuration?.Name == selected.Name) ?? DefaultScenario;
        }
        finally { _updating = false; }
        NotifyConfiguration();
        host.ScenariosChanged();
        _loaded = selected is null || Selected?.Configuration is not null;
        if (selected is not null && Selected?.Configuration is null)
        {
            var message = $"Scenario '{selected.Name}' is no longer available. Choose a scenario and Refresh.";
            Warnings.Add(message); host.ReportStatus(message);
            OnPropertyChanged(nameof(ConfigurationTitle));
            return false;
        }
        return true;
    }
    internal void Reset()
    {
        _watcher?.Dispose(); _watcher = null;
        _sourcePath = null; _projectDirectory = null; Fingerprint = null; _loaded = false;
        SelectionPending = false;
        ConfigurationPath = null;
        ResetToDefault();
        Warnings.Clear();
        NotifyConfiguration();
        host.ScenariosChanged();
    }
    private void ResetToDefault()
    {
        _updating = true;
        try { Items.Clear(); Items.Add(DefaultScenario); Selected = DefaultScenario; }
        finally { _updating = false; }
    }
    private bool IsLatest(long revision) => !host.IsDisposed && revision == host.Revision;
}
