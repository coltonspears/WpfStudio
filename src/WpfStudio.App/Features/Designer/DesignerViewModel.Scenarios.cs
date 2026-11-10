using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel : IScenarioCatalogHost
{
    public ScenarioCatalogViewModel Scenarios { get; }
    [ObservableProperty] public partial bool UseDesignTimeValues { get; set; } = true;
    /// <summary>Lets a view that declares no DataContext show sample data from the view model its name pairs it with.</summary>
    [ObservableProperty] public partial bool InferDesignData { get; set; } = true;
    public string ScenarioDescription => IsCurrent
        ? _snapshot?.Scenario is { } applied
            ? $"Active scenario: {applied.Configuration.Name} · {Path.GetFileName(applied.AssemblyPath)} · build {applied.AssemblySha256[..Math.Min(12, applied.AssemblySha256.Length)]}"
            : IsCompiledPreview || !UseDesignTimeValues ? "Active scenario: Default · view data"
            : HasDesignData ? "Active scenario: Default · design data: " + DesignDataSummary
            : "Active scenario: Default · view data and design-time declarations"
        : $"Selected scenario: {Scenarios.Selected?.Name ?? "Default"} · refresh to apply";

    /// <summary>Whether the current preview shows design-time data (d:DataContext, d:SampleData and the like, or inferred sample data).</summary>
    public bool HasDesignData => IsCurrent && _snapshot?.DesignData is { Count: > 0 };

    /// <summary>The data the view is drawn with, in a few words: "OrdersViewModel · sample data".</summary>
    public string DesignDataSummary
    {
        get
        {
            if (!HasDesignData) return "";
            var sources = _snapshot!.DesignData!;
            // The view's own DataContext is what the rest of its bindings read, so it names the preview.
            var main = sources.FirstOrDefault(source => source.Property == "DataContext") ?? sources[0];
            string type = main.TypeName is { Length: > 0 } name ? name[(name.LastIndexOf('.') + 1)..] : "";
            string kind = main.Kind switch
            {
                PreviewDesignDataKinds.Created => "constructed",
                PreviewDesignDataKinds.Inferred => "sample data, matched by name",
                PreviewDesignDataKinds.File => "design data file",
                _ => "sample data"
            };
            return (type.Length == 0 ? char.ToUpperInvariant(kind[0]) + kind[1..] : type + " · " + kind) + (sources.Count > 1 ? $" +{sources.Count - 1}" : "");
        }
    }

    /// <summary>Every design data source of the current preview, one to a line, and how to change them.</summary>
    public string DesignDataToolTip => !HasDesignData ? ""
        : string.Join("\n", _snapshot!.DesignData!.Select(source => (source.Line is { } line ? $"Line {line}: " : "") + source.Summary))
            + "\n\nDeclare d:DataContext on the view to choose its data, or pick a scenario for data from your own factory.";

    private void NotifyDesignData()
    {
        OnPropertyChanged(nameof(HasDesignData));
        OnPropertyChanged(nameof(DesignDataSummary));
        OnPropertyChanged(nameof(DesignDataToolTip));
        OnPropertyChanged(nameof(ScenarioDescription));
        OnPropertyChanged(nameof(CanDeclareDesignData));
        DeclareDesignDataCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The view model the preview matched to this view by name. Declaring it makes the choice the author's and gives the editor its type.</summary>
    private PreviewDesignData? MatchedDesignData => HasDesignData
        ? _snapshot!.DesignData!.FirstOrDefault(source => source.Kind == PreviewDesignDataKinds.Inferred && !string.IsNullOrEmpty(source.TypeName)) : null;

    public bool CanDeclareDesignData => !_disposed && !IsCompiledPreview && MatchedDesignData is not null && _sourceHost is not null
        && _document is { } document && document.Version == _sourceVersion;

    /// <summary>Writes d:DataContext for the matched view model into the view, through the change review.</summary>
    [RelayCommand(CanExecute = nameof(CanDeclareDesignData))]
    private async Task DeclareDesignDataAsync()
    {
        if (!CanDeclareDesignData) return;
        var document = _document!;
        var matched = MatchedDesignData!;
        var revision = _revision;
        // Markup names the types of its own project without an assembly.
        string? assembly = string.Equals(matched.AssemblyName, _sourceAssembly, StringComparison.OrdinalIgnoreCase) ? null : matched.AssemblyName;
        var proposal = XamlDesignDataEditService.CreateEdit(new(document.Path, document.Content, document.Version, matched.TypeName!, assembly));
        if (!proposal.Success) { Status = proposal.Error ?? proposal.Explanation; return; }
        try
        {
            bool applied = await _sourceHost!.ReviewDesignDataEditAsync(proposal, () => Current(revision) && ReferenceEquals(document, _document));
            if (!_disposed && ReferenceEquals(document, _document))
                Status = applied ? "d:DataContext declared in the editor. Save to write the file; Undo workspace edit restores it." : "Declaration cancelled.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed && ReferenceEquals(document, _document)) Status = exception.Message; }
    }

    partial void OnUseDesignTimeValuesChanged(bool value)
    {
        if (!IsCompiledPreview) { Scenarios.SelectionPending = true; Invalidate(); Status = "Design-time values changed. Refresh the source preview."; }
    }

    partial void OnInferDesignDataChanged(bool value)
    {
        if (!IsCompiledPreview && UseDesignTimeValues) { Scenarios.SelectionPending = true; Invalidate(); Status = "Sample data setting changed. Refresh the source preview."; }
    }

    string? IScenarioCatalogHost.DocumentPath => _document?.Path;
    long IScenarioCatalogHost.Revision => _revision;
    bool IScenarioCatalogHost.IsDisposed => _disposed;
    CancellationToken IScenarioCatalogHost.Lifetime => _lifetime.Token;
    void IScenarioCatalogHost.InvalidatePreview() => Invalidate();
    void IScenarioCatalogHost.ScenarioChosen(DesignerScenario? scenario)
    {
        Invalidate();
        Status = scenario?.Configuration?.ViewFactory is not null && !IsCompiledPreview
            ? "This scenario creates a view. Select Compiled mode, then Refresh."
            : "Scenario selected. Refresh to apply it in the isolated preview.";
    }
    void IScenarioCatalogHost.ScenariosChanged()
    {
        OnPropertyChanged(nameof(UsesApplicationResources));
        OnPropertyChanged(nameof(ScenarioDescription));
    }
    void IScenarioCatalogHost.ReportStatus(string status) => Status = status;
    void IScenarioCatalogHost.ReportFault(Exception fault) => ReportFault(fault);
    void IScenarioCatalogHost.OpenSource(SourceLocation location) => SourceRequested?.Invoke(location);
}
