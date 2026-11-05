using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.CodeAssist;
using WpfStudio.Contracts;
using WpfStudio.Core;

namespace WpfStudio.App.Services;

/// <summary>A model version the settings page offers.</summary>
public sealed record AssistModelChoice(string Version, string Label, string Summary, string Limitations);

/// <summary>An installed version as the page's list describes it. Reading about a version never selects it.</summary>
public sealed partial class AssistModelVersionInfo(AssistModel model) : ObservableObject
{
    public string Version { get; } = model.Manifest.Version;
    public string Summary { get; } = model.Manifest.Summary;
    public string Limitations { get; } = model.Manifest.Limitations;
    public string Headline { get; } = model.TrainedOn is { } trained ? $"Trained {trained}, {model.InstalledSize}" : model.InstalledSize;
    public IReadOnlyList<AssistModelFact> Facts { get; } = model.Facts();

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>
/// The experimental Code Assist settings, shared by the settings page and the engine they configure. <see cref="Load"/> and
/// <see cref="ToSettings"/> map them to the saved settings file.
/// </summary>
public sealed partial class CodeAssistPreferences : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private bool _loading;

    public CodeAssistPreferences(CodeAssistEngine engine, IUiDispatcher dispatcher)
    {
        Engine = engine; _dispatcher = dispatcher; Status = engine.Status;
        engine.StatusChanged += (_, _) => _dispatcher.Post(() => Status = Engine.Status);
    }

    public CodeAssistEngine Engine { get; }

    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedMemberModel))] public partial string MemberModelVersion { get; set; } = CodeAssistSettings.LatestVersion;
    [ObservableProperty] public partial bool LineSuggestions { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedLineModel))] public partial string LineModelVersion { get; set; } = CodeAssistSettings.LatestVersion;
    [ObservableProperty] public partial string Status { get; set; }

    public IReadOnlyList<AssistModelChoice> MemberModels => field ??= Choices(AssistModelKind.Members);
    public IReadOnlyList<AssistModelChoice> LineModels => field ??= Choices(AssistModelKind.Lines);
    public IReadOnlyList<AssistModelVersionInfo> MemberModelVersions => field ??= VersionList(AssistModelKind.Members, MemberModelVersion);
    public IReadOnlyList<AssistModelVersionInfo> LineModelVersions => field ??= VersionList(AssistModelKind.Lines, LineModelVersion);
    public AssistModelChoice? SelectedMemberModel => MemberModels.FirstOrDefault(choice => choice.Version == MemberModelVersion);
    public AssistModelChoice? SelectedLineModel => LineModels.FirstOrDefault(choice => choice.Version == LineModelVersion);

    partial void OnEnabledChanged(bool value) => Apply();
    partial void OnMemberModelVersionChanged(string value) { MarkSelected(MemberModelVersions, value); Apply(); }
    partial void OnLineSuggestionsChanged(bool value) => Apply();
    partial void OnLineModelVersionChanged(string value) { MarkSelected(LineModelVersions, value); Apply(); }

    /// <summary>Applies saved settings. A version that is no longer installed follows the newest instead of leaving the page's list blank.</summary>
    public void Load(CodeAssistSettings settings)
    {
        _loading = true;
        Enabled = settings.Enabled; LineSuggestions = settings.LineSuggestions;
        MemberModelVersion = Installed(settings.MemberModelVersion, AssistModelKind.Members);
        LineModelVersion = Installed(settings.LineModelVersion, AssistModelKind.Lines);
        _loading = false;
        Apply();
    }

    public CodeAssistSettings ToSettings() => new() { Enabled = Enabled, MemberModelVersion = MemberModelVersion, LineSuggestions = LineSuggestions, LineModelVersion = LineModelVersion };

    private void Apply()
    {
        // Off is what the engine starts as, so an ordinary startup with the feature off does no work at all.
        if (_loading || (!Enabled && !Engine.RanksMembers)) return;
        Engine.ConfigureAsync(new CodeAssistOptions(Enabled, Requested(MemberModelVersion), LineSuggestions, Requested(LineModelVersion))).Forget(fault => _dispatcher.Post(() => Status = fault.Message));
    }

    private static string? Requested(string version) => version == CodeAssistSettings.LatestVersion ? null : version;

    // The default never needs the catalog, which keeps it unread on a startup with the feature off.
    private string Installed(string version, AssistModelKind kind) =>
        version == CodeAssistSettings.LatestVersion || Engine.Catalog.Versions(kind).Any(model => model.Manifest.Version == version) ? version : CodeAssistSettings.LatestVersion;

    private List<AssistModelVersionInfo> VersionList(AssistModelKind kind, string selected)
    {
        List<AssistModelVersionInfo> versions = [.. Engine.Catalog.Versions(kind).Select(model => new AssistModelVersionInfo(model))];
        MarkSelected(versions, selected);
        return versions;
    }

    // Latest means the first in the list, which is the newest. The selection only changes from the default once the catalog has been read, so this costs a default startup nothing.
    private static void MarkSelected(IReadOnlyList<AssistModelVersionInfo> versions, string selected)
    {
        for (int i = 0; i < versions.Count; i++)
            versions[i].IsSelected = selected == CodeAssistSettings.LatestVersion ? i == 0 : versions[i].Version == selected;
    }

    private List<AssistModelChoice> Choices(AssistModelKind kind)
    {
        var versions = Engine.Catalog.Versions(kind);
        var choices = versions.Select(model => new AssistModelChoice(model.Manifest.Version, model.Manifest.Version, model.Manifest.Summary, model.Manifest.Limitations)).ToList();
        if (versions.Count > 0) choices.Insert(0, new AssistModelChoice(CodeAssistSettings.LatestVersion, $"Latest ({versions[0].Manifest.Version})", versions[0].Manifest.Summary, versions[0].Manifest.Limitations));
        return choices;
    }
}
