using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.App.Features.Inspection;

public sealed partial class RuntimeNodeViewModel(InspectionNode node) : ObservableObject
{
    [ObservableProperty] public partial InspectionNode Node { get; set; } = node;
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public ObservableCollection<RuntimeNodeViewModel> Children { get; } = [];
    public string Label => Node.Type.Split('.').Last() + (string.IsNullOrEmpty(Node.Name) ? "" : " #" + Node.Name) +
        (string.IsNullOrEmpty(Node.WindowTitle) ? "" : " · " + Node.WindowTitle);
    partial void OnNodeChanged(InspectionNode value) => OnPropertyChanged(nameof(Label));
}

/// <summary>Inspection of an application's actual objects, independent of the preview host.</summary>
public sealed partial class InspectionViewModel(IUiDispatcher dispatcher) : ObservableObject, IAsyncDisposable
{
    private IInspectionSession? _session;
    private Action? _stateChanged;
    private CancellationTokenSource? _poll;
    private long _generation, _selection, _revision;
    private bool _refreshing, _rebuilding, _preservingTreeSelection, _debugging, _disposed;
    private readonly Dictionary<string, RuntimeNodeViewModel> _nodes = new(StringComparer.Ordinal);
    public event Func<bool, Task>? LaunchRequested;
    public ObservableCollection<RuntimeNodeViewModel> Tree { get; } = [];
    public ObservableCollection<InspectionProperty> Properties { get; } = [];
    public ObservableCollection<InspectionProperty> Bindings { get; } = [];
    public ObservableCollection<InspectionTrace> Traces { get; } = [];
    [ObservableProperty] public partial RuntimeNodeViewModel? SelectedNode { get; set; }
    [ObservableProperty] public partial InspectionProperty? SelectedProperty { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Launch a WPF project with inspection to observe its actual UI and bindings.";
    [ObservableProperty] public partial string SessionDescription { get; set; } = "No running application connected";
    [ObservableProperty] public partial string DataContextDescription { get; set; } = "";
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial bool AutoRefresh { get; set; } = true;
    [ObservableProperty] public partial bool ShowLogicalTree { get; set; }
    public string Freshness => IsPaused ? "Debugger paused · last snapshot; inspection resumes when execution continues"
        : !IsConnected ? "Disconnected · displayed observations are from the last snapshot" : "Running application · live observations and temporary property edits";

    public async Task AttachAsync(IInspectionSession session, bool debugging = false, CancellationToken cancellationToken = default)
    {
        var disconnect = DisconnectAsync();
        var replacing = _generation;
        await disconnect;
        if (_disposed || replacing != _generation) { await session.DisposeAsync(); return; }
        _session = session; _debugging = debugging;
        var generation = ++_generation;
        _poll = new CancellationTokenSource();
        var token = _poll.Token;
        Tree.Clear(); _nodes.Clear(); _lastNodes = []; _revision = 0;
        Properties.Clear(); Bindings.Clear(); Traces.Clear(); SelectedNode = null;
        ResetDevTools();
        SessionDescription = "Waiting for the launched application…";
        _stateChanged = () => dispatcher.Post(() => { if (generation == _generation && ReferenceEquals(_session, session)) UpdateState(); });
        session.StateChanged += _stateChanged;
        UpdateState();
        try
        {
            var hello = await session.WaitForConnectionAsync(cancellationToken);
            if (generation != _generation || _disposed) return;
            SessionDescription = $"Process {hello.ProcessId} · .NET {hello.RuntimeVersion} · agent {hello.AgentVersion}";
            UpdateState();
            await RefreshAsync();
            _ = PollAsync(generation, token);
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
            {
                await DisconnectAsync();
                Status = "Inspection connection cancelled. The application keeps running.";
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _generation) { UpdateState(); Status = "Inspection unavailable: " + exception.Message; }
        }
    }

    private void UpdateState()
    {
        IsConnected = _session?.IsConnected == true;
        IsPaused = _session?.IsDebuggerPaused == true;
        Status = _session?.Status ?? "Inspector disconnected. The application keeps running.";
        OnPropertyChanged(nameof(Freshness));
        UpdateDevToolsState();
        UpdateLayoutState();
        UpdateAppearanceState();
        RefreshSourceState();
    }
    public void SetDebuggerState(bool stopped)
    {
        if (!_debugging) return;
        _session?.SetDebuggerPaused(stopped);
        UpdateState();
    }
    private async Task PollAsync(long generation, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            int ticks = 0;
            while (await timer.WaitForNextTickAsync(token))
            {
                if (generation != _generation || _disposed || !IsConnected) return;
                if (!IsPaused && (IsPicking || _pickAwaitingSelection || (AutoRefresh && ++ticks >= 8)))
                {
                    ticks = 0;
                    await RefreshAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    [RelayCommand] private Task RunAsync() => LaunchRequested?.Invoke(false) ?? Task.CompletedTask;
    [RelayCommand] private Task DebugAsync() => LaunchRequested?.Invoke(true) ?? Task.CompletedTask;
    [RelayCommand] private async Task RefreshAsync()
    {
        if (_disposed || _refreshing || IsPropertyOperationRunning || _session is not { IsConnected: true, IsDebuggerPaused: false } session) return;
        var generation = _generation;
        _refreshing = true;
        ++_selection;
        ClearBindingExplanation("Refreshing the application's binding observation…");
        ClearAppearance("Refreshing the application. Appearance will refresh for the selected property.", invalidateElement: true);
        ClearLayout("Refreshing the application's layout observation…");
        try
        {
            var tree = await session.SnapshotAsync(cancellationToken: _poll?.Token ?? default);
            if (generation != _generation || _disposed || IsPaused) return;
            _revision = tree.Revision;
            RebuildTree(tree.Nodes);
            UpdateDevToolsSnapshot(tree);
            Traces.Clear(); foreach (var trace in tree.Traces) Traces.Add(trace);
            Status = tree.Status ?? (tree.Truncated ? "Tree snapshot reached its limit; some elements are omitted." : $"{tree.Nodes.Count} elements · {tree.Traces.Count} historical binding trace groups");
            await InspectAndHighlightSelectedAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (generation == _generation) { UpdateState(); Status = exception.Message; } }
        finally { _refreshing = false; }
    }
    private IReadOnlyList<InspectionNode> _lastNodes = [];
    private void RebuildTree(IReadOnlyList<InspectionNode> nodes)
    {
        _lastNodes = nodes;
        var selectedId = SelectedNode?.Node.Id;
        _rebuilding = true; _preservingTreeSelection = true;
        try
        {
            var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var old in _nodes.Keys.Where(id => !ids.Contains(id)).ToArray()) _nodes.Remove(old);
            foreach (var node in nodes)
            {
                if (!_nodes.TryGetValue(node.Id, out var vm)) _nodes[node.Id] = vm = new(node);
                vm.Node = node; vm.Children.Clear();
            }
            Tree.Clear();
            foreach (var node in nodes.Where(n => ShowLogicalTree || n.IsVisual))
            {
                string? parent = ShowLogicalTree ? node.LogicalParentId : node.ParentId;
                var vm = _nodes[node.Id];
                if (parent is not null && parent != node.Id && _nodes.TryGetValue(parent, out var owner)) owner.Children.Add(vm);
                else Tree.Add(vm);
            }
            SelectedNode = selectedId is not null && _nodes.TryGetValue(selectedId, out var selected) ? selected : null;
            if (SelectedNode == null)
            {
                Properties.Clear(); Bindings.Clear(); SelectedProperty = null; DataContextDescription = "";
                ClearLayout("Select an element to inspect its layout.");
                ClearAppearance("Select an element and property to inspect appearance.", invalidateElement: true);
                RefreshBindingDeclarations(available: false);
            }
        }
        finally { _rebuilding = false; _preservingTreeSelection = false; }
        RefreshSourceState();
    }
    partial void OnShowLogicalTreeChanged(bool value) => RebuildTree(_lastNodes);
    partial void OnSelectedNodeChanging(RuntimeNodeViewModel? oldValue, RuntimeNodeViewModel? newValue)
    { if (oldValue != null) oldValue.IsSelected = false; if (newValue != null) newValue.IsSelected = true; }
    partial void OnSelectedNodeChanged(RuntimeNodeViewModel? value)
    {
        _selection++;
        // TreeView reports a transient null selection while containers are
        // rebuilt. Preserve the property selection and draft until restoration.
        if (_preservingTreeSelection) return;
        ClearBindingExplanation("Select an element to inspect its bindings.");
        ClearAppearance("Select a property to inspect its appearance.", invalidateElement: true);
        Properties.Clear(); Bindings.Clear(); SelectedProperty = null; DataContextDescription = "";
        RefreshBindingDeclarations(available: false);
        ClearLayout(value is null ? "Select an element to inspect its layout." : "Reading the selected element's layout…");
        RefreshSourceState(clear: true);
        SelectBindingIssueCommand.NotifyCanExecuteChanged();
        if (!_rebuilding) _ = InspectAndHighlightSelectedAsync();
    }
    private async Task InspectSelectedAsync()
    {
        if (_session is not { IsConnected: true, IsDebuggerPaused: false } session || SelectedNode == null) return;
        var generation = _generation; var selection = ++_selection; var revision = _revision; var id = SelectedNode.Node.Id;
        var operationEpoch = _bindingOperationEpoch;
        bool operationIdle = !IsPropertyOperationRunning && !IsSourceEditRunning;
        ClearBindingExplanation("Reading the selected element's bindings…");
        ClearAppearance("Reading the selected element's properties…", invalidateElement: true);
        LayoutDetails.Clear(_supportsLayout ? "Reading the selected element's layout…" : "This inspection agent does not provide layout details.");
        try
        {
            var result = await session.InspectAsync(new(revision, id), _poll?.Token ?? default);
            if (generation != _generation || selection != _selection || revision != _revision || SelectedNode?.Node.Id != id || IsPaused ||
                _disposed || !IsConnected || result.Revision != revision || result.NodeId != id) return;
            var selected = SelectedProperty;
            _updatingProperties = true;
            try
            {
                Properties.Clear(); Bindings.Clear();
                foreach (var property in result.Properties) { Properties.Add(property); if (property.Binding != null) Bindings.Add(property); }
                SelectedProperty = selected is null ? null : Properties.FirstOrDefault(p =>
                    p.PropertyId is not null && p.PropertyId == selected.PropertyId ||
                    p.PropertyId is null && p.Name == selected.Name && p.OwnerType == selected.OwnerType && p.OwnerAssembly == selected.OwnerAssembly);
            }
            finally { _updatingProperties = false; }
            _appearanceElementAvailable = result.Available;
            // Keep property/token updates flowing so source reviews can detect
            // changes, but do not revive a binding explanation across callbacks.
            _bindingObservationReady = result.Available && operationIdle && operationEpoch == _bindingOperationEpoch
                && !IsPropertyOperationRunning && !IsSourceEditRunning;
            _bindingObservationRevision = revision;
            RefreshPropertyEditSelection();
            RefreshBindingDeclarations(result.Available);
            DataContextDescription = result.DataContextType ?? "(null DataContext)";
            LayoutDetails.Apply(_supportsLayout && result.Available ? result.Layout : null,
                _supportsLayoutOverlay, !_supportsLayout ? "This inspection agent does not provide layout details." : result.Status ?? "Layout details are unavailable for this element.");
            if (!result.Available) Status = result.Status ?? "The element is no longer available. Refresh the tree.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _generation && selection == _selection)
            {
                ClearLayout("Layout could not be refreshed. " + exception.Message);
                ClearAppearance("Appearance could not be refreshed. " + exception.Message, invalidateElement: true);
                UpdateState(); Status = exception.Message;
            }
        }
    }
    [RelayCommand] private async Task DisconnectAsync()
    {
        ++_generation; ++_selection;
        ++_bindingSourceEpoch;
        RefreshBindingDeclarations(available: false);
        ClearAppearance("Inspector disconnected. Connect and refresh to inspect appearance.", invalidateElement: true);
        _sourceStatus = null;
        IsPropertyOperationRunning = false;
        _poll?.Cancel(); _poll?.Dispose(); _poll = null;
        var session = _session; _session = null;
        LayoutDetails.Clear("Inspector disconnected. Connect and refresh to observe current layout.");
        EndDevToolsSession();
        if (session != null)
        {
            if (_stateChanged != null) session.StateChanged -= _stateChanged;
            await session.DisposeAsync();
        }
        _stateChanged = null; _debugging = false;
        UpdateState();
    }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await DisconnectAsync(); }
}
