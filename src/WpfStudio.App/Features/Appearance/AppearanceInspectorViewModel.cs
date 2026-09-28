using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Appearance;

/// <summary>Displays an observation without inferring an active setter from candidate declarations.</summary>
public sealed partial class AppearanceInspectorViewModel : ObservableObject
{
    public ObservableCollection<AppearanceFact> Facts { get; } = [];
    public ObservableCollection<AppearanceFact> KeyFacts { get; } = [];
    public ObservableCollection<AppearanceFact> OtherFacts { get; } = [];
    public ObservableCollection<AppearanceDeclaration> Declarations { get; } = [];
    public ObservableCollection<AppearanceResourceScope> ResourceScopes { get; } = [];
    public ObservableCollection<AppearanceResourceEvidence> ResourceEvents { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];
    [ObservableProperty] public partial AppearanceSnapshot? Snapshot { get; set; }
    [ObservableProperty] public partial bool Available { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Select a property to inspect its appearance.";
    [ObservableProperty] public partial string SelectionContext { get; set; } = "";
    [ObservableProperty] public partial bool Truncated { get; set; }

    public void Begin(string valueSource, bool overridden)
    {
        Clear("Reading appearance for the selected property…");
        SelectionContext = overridden ? "Property inspector: temporary override · " + valueSource
            : valueSource.Contains("design baseline", StringComparison.OrdinalIgnoreCase) || valueSource == "Scenario"
                ? "Property inspector: " + valueSource : "";
        IsBusy = true;
    }
    public void Apply(AppearanceSnapshot snapshot)
    {
        ResetLists();
        Snapshot = snapshot; Available = snapshot.Available; IsBusy = false; Truncated = snapshot.Truncated;
        if (snapshot.Available)
        {
            foreach (var fact in snapshot.Facts)
            {
                Facts.Add(fact);
                if (fact.Name is "Effective value" or "Base value source") KeyFacts.Add(fact);
                else OtherFacts.Add(fact);
            }
            foreach (var declaration in snapshot.Declarations) Declarations.Add(declaration);
            foreach (var scope in snapshot.ResourceScopes) ResourceScopes.Add(scope);
            foreach (var evidence in snapshot.ResourceEvents) ResourceEvents.Add(evidence);
        }
        foreach (var notice in snapshot.Notices) Notices.Add(notice);
        Status = snapshot.Status ?? (snapshot.Available
            ? "Last appearance observation. Refresh after appearance changes."
            : "Appearance attribution is unavailable for this property.");
    }
    public void Clear(string status)
    {
        Snapshot = null; Available = false; IsBusy = false; Truncated = false; SelectionContext = "";
        ResetLists(); Status = status;
    }
    private void ResetLists()
    {
        Facts.Clear(); KeyFacts.Clear(); OtherFacts.Clear(); Declarations.Clear(); ResourceScopes.Clear(); ResourceEvents.Clear(); Notices.Clear();
    }
}
