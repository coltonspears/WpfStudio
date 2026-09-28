using WpfStudio.App.Features.Layout;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    private LayoutInspectorViewModel? _layoutDetails;
    public LayoutInspectorViewModel LayoutDetails => _layoutDetails ??= new(NotifyLayoutChanged);
    public LayoutSnapshot? Layout => LayoutDetails.Snapshot;
    public bool HasLayout => LayoutDetails.Available;
    public string LayoutStatus => LayoutDetails.Status;
    public bool CanShowLayoutOverlay => LayoutDetails.CanShowOverlay;
    public bool ShowLayoutOverlay { get => LayoutDetails.ShowOverlay; set => LayoutDetails.ShowOverlay = value; }

    private void NotifyLayoutChanged()
    {
        OnPropertyChanged(nameof(Layout)); OnPropertyChanged(nameof(HasLayout));
        OnPropertyChanged(nameof(LayoutStatus)); OnPropertyChanged(nameof(CanShowLayoutOverlay));
        OnPropertyChanged(nameof(ShowLayoutOverlay));
    }
}
