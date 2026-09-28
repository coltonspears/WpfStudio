using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.App.Features.Inspection;

public sealed record RuntimeBindingIssueGroup(string Title, string Explanation, string SourceDescription,
    IReadOnlyList<InspectionBindingIssue> Instances);

public sealed partial class InspectionViewModel
{
    private readonly InspectionBindingIssueTracker _issues = new();
    private long _pickSequence, _selectedPickSequence;
    private bool _pickAwaitingSelection;
    private CancellationTokenSource? _highlight;
    private bool _supportsPick, _supportsHighlight, _hasConnected;
    private bool _scanIncomplete;
    public ObservableCollection<RuntimeBindingIssueGroup> IssueGroups { get; } = [];
    [ObservableProperty] public partial bool ShowIssueHistory { get; set; } = true;
    [ObservableProperty] public partial string IssueSummary { get; set; } = "No binding observations yet.";
    [ObservableProperty] public partial string PickStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsPicking { get; set; }
    [ObservableProperty] public partial bool HighlightSelection { get; set; } = true;
    [ObservableProperty] public partial int SelectedTabIndex { get; set; }
    public string PickButtonLabel => IsPicking ? "Cancel picking" : "Pick from app";
    public bool CanHighlight => IsConnected && !IsPaused && _supportsHighlight;
    private bool CanPick() => IsConnected && !IsPaused && _supportsPick;
    private bool CanSelectBindingIssue(InspectionBindingIssue? issue) => IsConnected && !IsPaused &&
        issue is not null && _nodes.ContainsKey(issue.NodeId);

    private void ResetDevTools()
    {
        LayoutDetails.Clear("Select an element to inspect its layout.");
        _issues.Clear(); IssueGroups.Clear(); _pickSequence = 0; _selectedPickSequence = 0; _scanIncomplete = false;
        _pickAwaitingSelection = false;
        _hasConnected = false;
        ResetPropertyEditing();
        IsPicking = false; PickStatus = ""; IssueSummary = "No binding observations yet.";
    }

    private void UpdateDevToolsState()
    {
        _supportsPick = _session?.Hello?.Capabilities.Contains("pick") == true;
        _supportsHighlight = _session?.Hello?.Capabilities.Contains("highlight") == true;
        if (IsConnected) _hasConnected = true;
        else if (_hasConnected) EndDevToolsSession();
        TogglePickingCommand.NotifyCanExecuteChanged();
        SelectBindingIssueCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanHighlight));
        UpdatePropertyEditingState();
    }

    private void EndDevToolsSession()
    {
        LayoutDetails.Clear("Inspector disconnected. Connect and refresh to observe current layout.");
        _highlight?.Cancel(); _highlight?.Dispose(); _highlight = null;
        IsPicking = false; _pickAwaitingSelection = false; PickStatus = "";
        UpdatePropertyEditingState();
        _issues.EndSession();
        RefreshIssueGroups();
    }

    private void UpdateDevToolsSnapshot(InspectionTree tree)
    {
        _issues.Apply(tree);
        _scanIncomplete = tree.BindingScanTruncated || tree.Truncated || tree.BindingObservations is null || tree.ScannedBindingNodes is null;
        RefreshIssueGroups();
        SelectBindingIssueCommand.NotifyCanExecuteChanged();
        if (tree.Pick is not { } pick) return;
        if (pick.Sequence < _pickSequence) return;
        IsPicking = pick.IsActive;
        _pickAwaitingSelection = false;
        PickStatus = pick.Status ?? (pick.IsActive ? "Click an element in the application. Press Escape there to cancel." : "");
        _pickSequence = pick.Sequence;
        if (pick.Sequence <= _selectedPickSequence || pick.NodeId is not { } id) return;
        if (!_nodes.TryGetValue(id, out var node))
        {
            PickStatus = "The picked element is outside this snapshot. Refresh or increase the tree limit.";
            return;
        }
        if (!node.Node.IsVisual) ShowLogicalTree = true;
        _selectedPickSequence = pick.Sequence;
        _rebuilding = true;
        try { SelectedNode = node; SelectedTabIndex = 1; }
        finally { _rebuilding = false; }
    }

    private void RefreshIssueGroups()
    {
        static bool Outstanding(InspectionBindingIssue issue) => issue.State is "Active" or "NotObserved";
        var issues = _issues.Issues;
        IssueGroups.Clear();
        foreach (var group in issues.Where(issue => ShowIssueHistory || Outstanding(issue))
                     .GroupBy(issue => (issue.Category, issue.Path, issue.SourceType, issue.TargetProperty))
                     .OrderByDescending(group => group.Count(Outstanding)).ThenBy(group => group.Key.Category, StringComparer.Ordinal))
        {
            var instances = group.OrderByDescending(Outstanding).ThenBy(issue => issue.ElementLabel, StringComparer.Ordinal).ToArray();
            int active = instances.Count(issue => issue.State == "Active");
            IssueGroups.Add(new($"{group.Key.TargetProperty} ← {group.Key.Path ?? "(entire source)"} · {group.Key.Category} · {instances.Length} instances, {active} active",
                instances[0].Explanation, "Observed source: " + (group.Key.SourceType ?? "unavailable"), instances));
        }
        IssueSummary = $"{issues.Count(issue => issue.State == "Active")} active · {issues.Count(issue => issue.State == "NotObserved")} awaiting observation · {issues.Count(issue => issue.State == "Resolved")} verified recovered";
        if (_scanIncomplete) IssueSummary += " · scan incomplete; missing observations do not prove recovery";
        if (_issues.IsRetentionTruncated) IssueSummary += " · retained issue limit reached";
    }

    partial void OnShowIssueHistoryChanged(bool value) => RefreshIssueGroups();
    partial void OnIsPickingChanged(bool value) => OnPropertyChanged(nameof(PickButtonLabel));
    partial void OnHighlightSelectionChanged(bool value) => _ = HighlightSelectedAsync();

    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task TogglePickingAsync()
    {
        if (_session is not { IsConnected: true, IsDebuggerPaused: false } session) return;
        var generation = _generation;
        try
        {
            var state = await session.SetPickingAsync(new(!IsPicking), _poll?.Token ?? default);
            if (generation != _generation || IsPaused || state.Sequence < _pickSequence) return;
            _pickSequence = state.Sequence;
            IsPicking = state.IsActive;
            _pickAwaitingSelection = state.NodeId is not null && state.Sequence > _selectedPickSequence;
            PickStatus = state.Status ?? (state.IsActive ? "Click an element in the application. Press Escape there to cancel." : "Picking cancelled.");
            if (_pickAwaitingSelection) await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (generation == _generation) PickStatus = exception.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanSelectBindingIssue))]
    private async Task SelectBindingIssueAsync(InspectionBindingIssue? issue)
    {
        if (issue is null || !CanSelectBindingIssue(issue) || !_nodes.TryGetValue(issue.NodeId, out var node)) return;
        var generation = _generation;
        if (!node.Node.IsVisual) ShowLogicalTree = true;
        _rebuilding = true;
        try { SelectedNode = node; SelectedTabIndex = 1; }
        finally { _rebuilding = false; }
        var sourceEpoch = _sourceEpoch;
        var userSelection = _bindingSourceUserSelection;
        await InspectAndHighlightSelectedAsync();
        if (generation != _generation || sourceEpoch != _sourceEpoch || userSelection != _bindingSourceUserSelection
            || !IsConnected || IsPaused || SelectedNode?.Node.Id != issue.NodeId) return;
        var roots = BindingDeclarations.Where(item => item.BindingId == issue.Id && item.Declaration.ParentExpressionId is null).ToArray();
        // Choosing an issue changes diagnosis selection, never a property's edit draft.
        _updatingBindingDeclarations = true;
        try { SelectedBindingDeclaration = roots.Length == 1 ? roots[0] : null; }
        finally { _updatingBindingDeclarations = false; }
        BindingSourceSelectionChanged();
        if (roots.Length != 1)
        {
            _bindingSelectionRemoved = true;
            BindingSourceStatus = "That issue's expression is no longer present. A replacement is a different binding.";
            BindingExplanation.Clear(BindingSourceStatus);
        }
    }

    private async Task InspectAndHighlightSelectedAsync()
    {
        await InspectSelectedAsync();
        await HighlightSelectedAsync();
    }

    private async Task HighlightSelectedAsync(bool clear = false)
    {
        _highlight?.Cancel(); _highlight?.Dispose(); _highlight = null;
        if (_session is not { IsConnected: true, IsDebuggerPaused: false } session || (!_supportsHighlight && !_supportsLayoutOverlay) || IsPicking) return;
        var generation = _generation; var selection = _selection;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_poll?.Token ?? default);
        _highlight = cancellation;
        try
        {
            bool showLayout = !clear && CanShowLayoutOverlay && ShowLayoutOverlay;
            var nodeId = !clear && (HighlightSelection && _supportsHighlight || showLayout) ? SelectedNode?.Node.Id : null;
            var result = await session.HighlightAsync(new(_revision, nodeId, ShowLayout: showLayout), cancellation.Token);
            if (generation == _generation && selection == _selection && !result.Applied && result.Status is not null)
                PickStatus = result.Status;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (generation == _generation && selection == _selection) PickStatus = exception.Message; }
        finally { if (ReferenceEquals(_highlight, cancellation)) _highlight = null; }
    }
}
