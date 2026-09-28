using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Inspection;

public sealed record InspectionSourceRequest(InspectionNode Node, InspectionModuleCatalog Modules, Func<bool> IsCurrent);

public sealed partial class InspectionViewModel
{
    public event Func<InspectionSourceRequest, CancellationToken, Task<string>>? SourceRequested;
    private bool _openingSource;
    private string? _sourceStatus;
    private InspectionNode? _sourceStatusNode;
    private InspectionNode? _sourceContextNode;
    private bool _sourceContextPaused, _sourceContextConnected;
    private long _sourceEpoch;
    [ObservableProperty] public partial bool VerifySourceOnBuild { get; set; } = true;
    public string SourceDescription => _sourceStatus ?? (SelectedNode?.Node.Source is { } hint
        ? $"Source hint: {hint.Uri}:{hint.Line}:{hint.Column}. Show XAML verifies the running module, build source and editor buffer."
        : "WPF supplied no source location for this object. Code-created elements and some generated content cannot be mapped to XAML.");
    private bool CanShowSource() => !_openingSource && !_disposed && IsConnected && !IsPaused
        && SelectedNode?.Node.Source != null && SourceRequested != null
        && _session?.Hello?.Capabilities.Contains("modules") == true;

    private void RefreshSourceState(bool clear = false)
    {
        var current = SelectedNode?.Node;
        if (!SameSource(current, _sourceContextNode) || IsPaused != _sourceContextPaused || IsConnected != _sourceContextConnected)
        {
            _sourceEpoch++;
            _sourceContextNode = current; _sourceContextPaused = IsPaused; _sourceContextConnected = IsConnected;
            if (_sourceStatus == "Verifying compiled XAML source…")
                _sourceStatus = "Source navigation cancelled because the inspection context changed.";
        }
        if (clear || _sourceStatusNode is { } previous && (current == null || current.Id != previous.Id
            || current.Source != previous.Source || current.Type != previous.Type || current.Name != previous.Name)) _sourceStatus = null;
        OnPropertyChanged(nameof(SourceDescription));
        ShowSourceCommand.NotifyCanExecuteChanged();
        RefreshBindingSourceState();
    }

    private static bool SameSource(InspectionNode? left, InspectionNode? right) => left is null ? right is null
        : right is not null && left.Id == right.Id && left.Source == right.Source && left.Type == right.Type && left.Name == right.Name;

    [RelayCommand(CanExecute = nameof(CanShowSource))]
    private async Task ShowSourceAsync()
    {
        if (!CanShowSource() || _session is not { } session || SelectedNode is not { } selected || SourceRequested is not { } navigate) return;
        var generation = _generation; var node = selected.Node; var epoch = _sourceEpoch;
        bool Current() => generation == _generation && epoch == _sourceEpoch && ReferenceEquals(session, _session) && !_disposed
            && IsConnected && !IsPaused && SelectedNode?.Node is { } current && current.Id == node.Id
            && current.Source == node.Source && current.Type == node.Type && current.Name == node.Name;
        _openingSource = true; _sourceStatusNode = node; _sourceStatus = "Verifying compiled XAML source…"; RefreshSourceState();
        try
        {
            var token = _poll?.Token ?? default;
            var modules = await session.GetModulesAsync(token);
            if (!Current()) return;
            var status = await navigate(new(node, modules, Current), token);
            if (Current()) _sourceStatus = status;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (Current()) _sourceStatus = "Source navigation unavailable: " + exception.Message; }
        finally
        {
            if (!Current() && _sourceStatus == "Verifying compiled XAML source…")
                _sourceStatus = "Source navigation cancelled because the inspection context changed.";
            _openingSource = false; RefreshSourceState();
        }
    }
}
