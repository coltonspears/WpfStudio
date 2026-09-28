using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Layout;

/// <summary>Presentation of a single current observation; clearing never preserves stale geometry.</summary>
public sealed partial class LayoutInspectorViewModel(Action changed, Action? overlayChanged = null) : ObservableObject
{
    public ObservableCollection<LayoutFact> Facts { get; } = [];
    public ObservableCollection<LayoutFact> KeyFacts { get; } = [];
    public ObservableCollection<LayoutFact> OtherFacts { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];
    public LayoutSnapshot? Snapshot { get; private set; }
    [ObservableProperty] public partial bool Available { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Select an element to inspect its layout.";
    [ObservableProperty] public partial bool CanShowOverlay { get; set; }
    [ObservableProperty] public partial bool ShowOverlay { get; set; }
    [ObservableProperty] public partial string OverlayStatus { get; set; } = "Select an element to display layout outlines.";
    public IReadOnlyList<LayoutOverlay> VisibleOverlays => Available && CanShowOverlay && ShowOverlay ? Snapshot?.Overlays ?? [] : [];

    public void Apply(LayoutSnapshot? snapshot, bool supportsOverlay, string unavailable)
    {
        Snapshot = snapshot is { Available: true } ? snapshot : null;
        Available = Snapshot is not null;
        Facts.Clear(); KeyFacts.Clear(); OtherFacts.Clear(); Notices.Clear();
        if (Snapshot is { } current)
        {
            foreach (var fact in current.Facts)
            {
                Facts.Add(fact);
                if (fact.Name is "Desired size" or "Render size" or "Layout slot") KeyFacts.Add(fact);
                else OtherFacts.Add(fact);
            }
            foreach (var notice in current.Notices) Notices.Add(notice);
        }
        else if (snapshot is not null)
            foreach (var notice in snapshot.Notices) Notices.Add(notice);
        Status = snapshot?.Status ?? (Available ? "Observed layout · sizes and coordinates use device-independent pixels (DIPs). Refresh after layout changes." : unavailable);
        CanShowOverlay = Available && supportsOverlay && Snapshot!.Overlays.Count > 0;
        OverlayStatus = !Available ? "Layout outlines are unavailable until a current element is observed."
            : !supportsOverlay ? "This inspection agent does not support layout overlays."
            : Snapshot!.Overlays.Count == 0 ? "No drawable layout outlines are available for this element." : "Outlines show the last layout observation. Refresh after layout changes; preview zoom scales the outlines with the image.";
        Publish();
    }

    public void Clear(string status) => Apply(null, false, status);
    partial void OnShowOverlayChanged(bool value) { Publish(); overlayChanged?.Invoke(); }
    private void Publish()
    {
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(VisibleOverlays));
        changed();
    }
}
