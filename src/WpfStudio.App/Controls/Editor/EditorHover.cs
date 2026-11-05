using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace WpfStudio.App.Controls.Editor;

/// <summary>The information that opens over the text under the pointer: the hover card, filled in as its slower parts arrive.</summary>
internal sealed class EditorHover
{
    private readonly EditorSurface _owner;
    private CancellationTokenSource? _request;
    // Hover information is a popup the editor owns, below the pointer. A tooltip at the pointer covered the
    // text, ended the hover and reopened on the next one, so it flashed; ToolTipService could also close it.
    private readonly Popup _popup = new()
    {
        Placement = PlacementMode.Mouse, StaysOpen = true, AllowsTransparency = true,
        PopupAnimation = PopupAnimation.None
    };
    private readonly Border _content = new();

    public EditorHover(EditorSurface owner)
    {
        _owner = owner;
        var chrome = new Border
        {
            Child = _content, Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 2, 8, 8),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.28, Direction = 270 }
        };
        chrome.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "StrongBorderBrush");
        _popup.Child = chrome;
        _popup.MouseLeave += (_, _) => { if (!owner.IsMouseOver) Cancel(); };
    }

#pragma warning disable VSTHRD100 // A WPF event handler; the awaits below are guarded.
    public async void OnMouseHover(object? sender, MouseEventArgs e)
#pragma warning restore VSTHRD100
    {
        Cancel();
        if (EditorAdornment.IsUnder(e.OriginalSource) || EditorAdornment.IsUnder(Mouse.DirectlyOver)) return; // Folded blocks and lens rows have their own tips.
        if (_owner.ViewModel is not { } vm || _owner.GetPositionFromPoint(e.GetPosition(_owner)) is not { } position) return;
        int offset = _owner.Document.GetOffset(position.Location);
        long version = vm.State.Version;
        _request = new CancellationTokenSource();
        var token = _request.Token;
        var parts = new HoverParts(null, null, null, vm.DiagnosticsAt(offset));
        if (!vm.ShowQuickInfo && parts.Diagnostics.Count == 0 && vm.DataTipEvaluator is null) return;
        Show(parts);
        try
        {
            if (await vm.DataTipAsync(offset, token) is { } dataTip)
            {
                if (token.IsCancellationRequested || vm != _owner.ViewModel || version != vm.State.Version) return;
                Show(parts = parts with { DataTip = dataTip });
            }
            if (!vm.ShowQuickInfo) return;
            if (vm.IsCSharp)
            {
                var info = await vm.QuickInfoAsync(offset, token);
                if (token.IsCancellationRequested || vm != _owner.ViewModel || version != vm.State.Version) return;
                if (info is not null) Show(parts = parts with { QuickInfo = info });
            }
            else if (await vm.HoverAsync(offset, token) is { } hover)
            {
                if (token.IsCancellationRequested || vm != _owner.ViewModel || version != vm.State.Version) return;
                Show(parts = parts with { Xaml = hover.Text });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && vm == _owner.ViewModel) vm.LanguageStatus = ex.Message; }
    }
    public void OnMouseHoverStopped(object? sender, MouseEventArgs e)
    {
        // Moving onto the popup (to read or select from it) keeps it open.
        if (!_popup.IsMouseOver) Cancel();
    }

    public void Cancel()
    {
        _request?.Cancel(); _request?.Dispose(); _request = null;
        _popup.IsOpen = false;
    }

    private void Show(HoverParts parts)
    {
        if (parts.IsEmpty) return;
        // Updating an open popup in place avoids moving it when slower information arrives.
        _content.Child = new HoverCard(_owner).Build(parts);
        if (!_popup.IsOpen) { _popup.PlacementTarget = _owner; _popup.IsOpen = true; }
    }
}
