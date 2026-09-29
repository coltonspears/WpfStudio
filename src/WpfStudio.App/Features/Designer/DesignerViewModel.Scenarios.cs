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

public sealed partial class DesignerViewModel
{
    private static readonly DesignerScenario DefaultScenario = new(null);
    private FileSystemWatcher? _scenarioWatcher;
    private string? _scenarioProjectDirectory;
    private string? _scenarioSourcePath;
    private string? _scenarioFingerprint;
    private bool _updatingScenarios;
    private bool _scenarioSelectionPending;
    private bool _scenariosLoaded;
    public ObservableCollection<DesignerScenario> Scenarios { get; } = [DefaultScenario];
    public ObservableCollection<string> ScenarioWarnings { get; } = [];
    [ObservableProperty] public partial DesignerScenario? SelectedScenario { get; set; } = DefaultScenario;
    [ObservableProperty] public partial bool UseDesignTimeValues { get; set; } = true;
    [ObservableProperty] public partial string? ScenarioConfigurationPath { get; set; }
    public string ScenarioDescription => IsCurrent
        ? _snapshot?.Scenario is { } applied
            ? $"Active scenario: {applied.Configuration.Name} · {Path.GetFileName(applied.AssemblyPath)} · build {applied.AssemblySha256[..Math.Min(12, applied.AssemblySha256.Length)]}"
            : IsCompiledPreview || !UseDesignTimeValues ? "Active scenario: Default · view data"
            : "Active scenario: Default · view data and supported design-time declarations"
        : $"Selected scenario: {SelectedScenario?.Name ?? "Default"} · refresh to apply";
    public string ScenarioConfigurationDescription => ScenarioConfigurationPath is { } path
        ? $"Saved configuration: {path}"
        : "Open a project view to use wpfstudio.preview.json scenarios.";
    public bool CanOpenScenarioConfiguration => ScenarioConfigurationPath is { } path && File.Exists(path);
    public string ScenarioConfigurationTitle => ScenarioWarnings.Count == 0 ? "Scenario configuration"
        : $"Scenario configuration · {ScenarioWarnings.Count} issue(s)";

    partial void OnSelectedScenarioChanged(DesignerScenario? value)
    {
        OnPropertyChanged(nameof(UsesApplicationResources));
        if (_updatingScenarios) return;
        _scenarioSelectionPending = true;
        Invalidate();
        Status = value?.Configuration?.ViewFactory is not null && !IsCompiledPreview
            ? "This scenario creates a view. Select Compiled mode, then Refresh."
            : "Scenario selected. Refresh to apply it in the isolated preview.";
        OnPropertyChanged(nameof(ScenarioDescription));
    }
    partial void OnUseDesignTimeValuesChanged(bool value)
    {
        if (!IsCompiledPreview) { _scenarioSelectionPending = true; Invalidate(); Status = "Design-time values changed. Refresh the source preview."; }
    }
    partial void OnScenarioConfigurationPathChanged(string? value)
    {
        OnPropertyChanged(nameof(ScenarioConfigurationDescription));
        NotifyScenarioConfiguration();
    }
    private void NotifyScenarioConfiguration()
    {
        OnPropertyChanged(nameof(CanOpenScenarioConfiguration));
        OnPropertyChanged(nameof(ScenarioConfigurationTitle));
        OpenScenarioConfigurationCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanOpenScenarioConfiguration))]
    private void OpenScenarioConfiguration()
    {
        if (ScenarioConfigurationPath is { } path && File.Exists(path))
            SourceRequested?.Invoke(new(path, 0, 0, 1, 1));
    }
    [RelayCommand]
    private async Task ReloadScenariosAsync()
    {
        if (_document is null || _disposed) return;
        _scenarioSelectionPending = true;
        Invalidate();
        var revision = _revision;
        try
        {
            if (await LoadScenariosAsync(revision, _lifetime.Token) && Current(revision))
                Status = ScenarioWarnings.FirstOrDefault() ?? "Scenarios reloaded. Select a scenario and Refresh to apply it.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(revision)) Status = exception.Message; }
    }
    private void ConfigureScenarios(string sourcePath, string? projectDirectory)
    {
        if (string.Equals(_scenarioSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_scenarioProjectDirectory, projectDirectory, StringComparison.OrdinalIgnoreCase)) return;
        _scenarioWatcher?.Dispose(); _scenarioWatcher = null;
        _scenarioSourcePath = sourcePath; _scenarioProjectDirectory = projectDirectory;
        _scenarioFingerprint = null; _scenariosLoaded = false;
        _scenarioSelectionPending = false;
        _updatingScenarios = true;
        try { Scenarios.Clear(); Scenarios.Add(DefaultScenario); SelectedScenario = DefaultScenario; }
        finally { _updatingScenarios = false; }
        ScenarioWarnings.Clear();
        ScenarioConfigurationPath = string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.Combine(projectDirectory, PreviewScenarioCatalog.FileName);
        OnPropertyChanged(nameof(ScenarioDescription));
        if (projectDirectory is null || !Directory.Exists(projectDirectory)) return;
        try
        {
            var watcher = new FileSystemWatcher(projectDirectory, PreviewScenarioCatalog.FileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _scenarioWatcher = watcher;
            watcher.Changed += (_, _) => ScenarioConfigurationChanged(watcher);
            watcher.Created += (_, _) => ScenarioConfigurationChanged(watcher);
            watcher.Deleted += (_, _) => ScenarioConfigurationChanged(watcher);
            watcher.Renamed += (_, _) => ScenarioConfigurationChanged(watcher);
            watcher.Error += (_, _) => _dispatcher.Post(() =>
            {
                if (_disposed || !ReferenceEquals(_scenarioWatcher, watcher)) return;
                _scenarioSelectionPending = true;
                Invalidate(); Status = "Scenario configuration monitoring stopped. Refresh to verify the saved configuration.";
            });
            watcher.EnableRaisingEvents = true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private void ScenarioConfigurationChanged(FileSystemWatcher watcher) => _dispatcher.Post(() => _ = CheckScenarioConfigurationAsync(watcher));
    private async Task CheckScenarioConfigurationAsync(FileSystemWatcher watcher)
    {
        if (_disposed || !ReferenceEquals(_scenarioWatcher, watcher)) return;
        var revision = _revision;
        try
        {
            if (_document is null) return;
            var catalog = await PreviewScenarioCatalog.LoadAsync(_scenarioProjectDirectory, _document.Path, _lifetime.Token);
            if (!Current(revision) || !ReferenceEquals(_scenarioWatcher, watcher) || catalog.Fingerprint == _scenarioFingerprint) return;
            _scenarioSelectionPending = true;
            Invalidate();
            NotifyScenarioConfiguration();
            Status = "Scenario configuration changed. Reload scenarios or Refresh to use the saved configuration.";
        }
        catch (OperationCanceledException) { }
    }
    // The transaction's final guard cannot await. Hash at most the catalog's
    // bounded input size so timestamp-preserving changes cannot bypass it.
    private bool IsScenarioConfigurationCurrent()
    {
        if (ScenarioConfigurationPath is not { } path) return _scenarioFingerprint is null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_scenarioFingerprint is null) return false;
            byte[] bytes = new byte[262145];
            int length = 0;
            while (length < bytes.Length)
            {
                int read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            return length <= 262144 && Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length))) == _scenarioFingerprint;
        }
        catch (FileNotFoundException) { return _scenarioFingerprint is null; }
        catch (DirectoryNotFoundException) { return _scenarioFingerprint is null; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private async Task<bool> VerifyScenarioConfigurationAsync(long revision, CancellationToken token)
    {
        if (_document is null) return false;
        var catalog = await PreviewScenarioCatalog.LoadAsync(_scenarioProjectDirectory, _document.Path, token);
        if (!Current(revision)) return false;
        if (catalog.Fingerprint == _scenarioFingerprint) return true;
        Invalidate(); Status = "Scenario configuration changed. Refresh to use the saved configuration.";
        return false;
    }
    private async Task<bool> LoadScenariosAsync(long revision, CancellationToken token)
    {
        if (_document is null) return false;
        var selected = SelectedScenario?.Configuration;
        var catalog = await PreviewScenarioCatalog.LoadAsync(_scenarioProjectDirectory, _document.Path, token);
        if (!Current(revision)) return false;
        // Live preview reloads the catalog on every render. An unchanged catalog keeps its
        // items so an open scenario list and its selection are not reset while typing.
        if (_scenariosLoaded && catalog.Fingerprint == _scenarioFingerprint && SelectedScenario is not null) return true;
        _scenarioFingerprint = catalog.Fingerprint;
        ScenarioWarnings.Clear();
        foreach (var warning in catalog.Warnings) ScenarioWarnings.Add(warning);
        _updatingScenarios = true;
        try
        {
            Scenarios.Clear(); Scenarios.Add(DefaultScenario);
            foreach (var scenario in catalog.Scenarios) Scenarios.Add(new(scenario));
            SelectedScenario = selected is null ? DefaultScenario
                : Scenarios.FirstOrDefault(scenario => scenario.Configuration?.Name == selected.Name) ?? DefaultScenario;
        }
        finally { _updatingScenarios = false; }
        NotifyScenarioConfiguration();
        OnPropertyChanged(nameof(ScenarioDescription));
        _scenariosLoaded = selected is null || SelectedScenario?.Configuration is not null;
        if (selected is not null && SelectedScenario?.Configuration is null)
        {
            var message = $"Scenario '{selected.Name}' is no longer available. Choose a scenario and Refresh.";
            ScenarioWarnings.Add(message); Status = message;
            OnPropertyChanged(nameof(ScenarioConfigurationTitle));
            return false;
        }
        return true;
    }
    private void ClearScenarios()
    {
        _scenarioWatcher?.Dispose(); _scenarioWatcher = null;
        _scenarioSourcePath = null; _scenarioProjectDirectory = null; _scenarioFingerprint = null; _scenariosLoaded = false;
        _scenarioSelectionPending = false;
        ScenarioConfigurationPath = null;
        _updatingScenarios = true;
        try { Scenarios.Clear(); Scenarios.Add(DefaultScenario); SelectedScenario = DefaultScenario; }
        finally { _updatingScenarios = false; }
        ScenarioWarnings.Clear();
        NotifyScenarioConfiguration();
        OnPropertyChanged(nameof(ScenarioDescription));
    }
}
