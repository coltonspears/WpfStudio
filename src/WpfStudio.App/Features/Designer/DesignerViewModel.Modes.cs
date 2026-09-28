using System.IO;
using System.Xml;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    public IReadOnlyList<PreviewMode> PreviewModes { get; } = [PreviewMode.Source, PreviewMode.Compiled];
    [ObservableProperty] public partial PreviewMode Mode { get; set; }
    [ObservableProperty] public partial string ViewTypeName { get; set; } = "";
    [ObservableProperty] public partial string ApplicationResourcePath { get; set; } = "App.xaml";
    public bool IsCompiledPreview => Mode == PreviewMode.Compiled;
    public bool SupportsLivePreview => !IsCompiledPreview;
    public bool UsesApplicationResources => IsCompiledPreview || SelectedScenario?.Configuration is not null;
    public string BuildDescription => _snapshot?.Build is { } build
        ? $"{build.ViewTypeName} · {Path.GetFileName(build.AssemblyPath)} · SHA-256 {build.AssemblySha256[..Math.Min(12, build.AssemblySha256.Length)]} · module {build.ModuleVersionId}"
        : IsCompiledPreview ? "Build the project before loading a compiled view. Unsaved source changes are not in that build." : "";

    partial void OnModeChanged(PreviewMode value)
    {
        _scenarioSelectionPending = true;
        Invalidate();
        OnPropertyChanged(nameof(IsCompiledPreview));
        OnPropertyChanged(nameof(SupportsLivePreview));
        OnPropertyChanged(nameof(UsesApplicationResources));
        OnPropertyChanged(nameof(SessionDescription));
        OnPropertyChanged(nameof(BuildDescription));
        OnPropertyChanged(nameof(ScenarioDescription));
        Status = "Refresh to load the selected preview mode.";
    }
    partial void OnViewTypeNameChanged(string value)
    {
        if (IsCompiledPreview) { _scenarioSelectionPending = true; Invalidate(); Status = "Refresh to load the selected compiled view."; }
    }
    partial void OnApplicationResourcePathChanged(string value)
    {
        if (UsesApplicationResources) { _scenarioSelectionPending = true; Invalidate(); Status = "Refresh to load the selected application resources."; }
    }
    private static string ReadViewType(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return (string?)XDocument.Load(reader).Root?.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? "";
        }
        catch (XmlException) { return ""; }
    }
}
