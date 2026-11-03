using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.App.Features.BindingSources;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.BindingDiagnostics;

public sealed record BindingPathStep(InspectionBindingPathSegment Segment)
{
    public string Label => $"{Segment.Level + 1}. {Segment.Name ?? "(direct source)"} · {Segment.State}";
    public string? OwnerType => Segment.OwnerType;
    public string? ValueType => Segment.ValueType;
    public string? AccessorKind => Segment.AccessorKind;
}

/// <summary>Displays one observed expression; historical notifications never become current causes.</summary>
public sealed partial class BindingExplanationViewModel : ObservableObject
{
    [ObservableProperty] public partial InspectionBinding? Observation { get; set; }
    [ObservableProperty] public partial bool Available { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Select a binding declaration to inspect it.";
    [ObservableProperty] public partial string SelectionLabel { get; set; } = "";
    [ObservableProperty] public partial string TargetValue { get; set; } = "";
    [ObservableProperty] public partial string ValueSource { get; set; } = "";
    [ObservableProperty] public partial string PathStatus { get; set; } = "";
    [ObservableProperty] public partial string DetailsStatus { get; set; } = "";
    [ObservableProperty] public partial bool ValidationTruncated { get; set; }
    [ObservableProperty] public partial bool EvidenceTruncated { get; set; }
    public ObservableCollection<BindingPathStep> Segments { get; } = [];
    public ObservableCollection<InspectionBindingValidation> ValidationErrors { get; } = [];
    public ObservableCollection<InspectionBindingEvidence> Evidence { get; } = [];

    /// <summary>Explains the selected declaration from the current observation, or names why it cannot.</summary>
    /// <param name="noIdentities">What to say when bindings were observed without expression identities.</param>
    /// <param name="locate">The observed property holding a declaration; null once it has left the observation.</param>
    public void ExplainSelected(BindingDeclarationsViewModel declarations, bool hasBindings, string noIdentities,
        Func<BindingDeclarationItem, (InspectionBinding? Binding, string Value, string ValueSource)?> locate)
    {
        if (declarations.Items.Count == 0 && hasBindings) { Clear(noIdentities); return; }
        if (declarations.Selected is not { } item || !declarations.Items.Any(current => current.SameIdentity(item)))
        { Clear("Select a binding declaration. A removed or replaced expression is not the previous binding."); return; }
        if (locate(item) is not { } property) { Clear("The selected binding is no longer in this observation."); return; }
        var observation = item.Declaration.Observation ?? (item.Declaration.ParentExpressionId is null ? property.Binding : null);
        Apply(item, observation, property.Value, property.ValueSource);
    }

    public void Apply(BindingDeclarationItem item, InspectionBinding? observation, string targetValue, string valueSource)
    {
        Clear(observation is null ? "This host did not provide an explanation for the selected expression. Refresh with a compatible host."
            : "Current observation from the last inspection. Refresh after application changes.");
        SelectionLabel = item.Label;
        if (observation is null) return;
        Observation = observation;
        Available = true;
        // A composite child's transfer value is not exposed by WPF. This value is
        // deliberately labeled as the owning dependency property's target value.
        TargetValue = targetValue; ValueSource = valueSource;
        if (observation.Details is not { } details)
        {
            DetailsStatus = "Additional binding details were not supplied by this host.";
            PathStatus = "Cached path state is unavailable.";
            return;
        }
        DetailsStatus = details.SourceDescription;
        ValidationTruncated = details.ValidationTruncated;
        EvidenceTruncated = details.EvidenceTruncated;
        foreach (var error in details.ValidationErrors) ValidationErrors.Add(error);
        foreach (var evidence in details.Evidence) Evidence.Add(evidence);
        if (details.PathState is not { } path)
        { PathStatus = "Cached path state is unavailable from this host."; return; }
        PathStatus = path.Status;
        if (!string.IsNullOrWhiteSpace(path.UnavailableReason) && path.UnavailableReason != path.Status) PathStatus += " " + path.UnavailableReason;
        if (path.FirstUnresolvedLevel is { } level) PathStatus += $" First unresolved step: {level + 1}.";
        if (path.UsesFallbackValue is true) PathStatus += " WPF reports use of the fallback value.";
        if (path.Truncated) PathStatus += " Some path steps were omitted because the inspection limit was reached.";
        if (path.Available) foreach (var segment in path.Segments) Segments.Add(new(segment));
    }

    public void Clear(string status)
    {
        Observation = null; Available = false; Status = status; SelectionLabel = "";
        TargetValue = ""; ValueSource = ""; PathStatus = ""; DetailsStatus = "";
        ValidationTruncated = false; EvidenceTruncated = false;
        Segments.Clear(); ValidationErrors.Clear(); Evidence.Clear();
    }
}
