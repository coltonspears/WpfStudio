using WpfStudio.App.Features.Workbench;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.App.Features.Debugging;

/// <summary>What breakpoint editing needs from the shell: the editors and dialogs.</summary>
public interface IBreakpointEditingHost
{
    EditorViewModel? ActiveDocument { get; }
    IReadOnlyList<EditorViewModel> Documents { get; }
    IUserDialogService Dialogs { get; }
    /// <summary>Runs an operation, reporting a failure in the status bar and Output instead of faulting the caller.</summary>
    Task GuardAsync(Func<Task> action);
    void ReportStatus(string status);
}

/// <summary>
/// Breakpoints as edited from the code editor: toggling at the caret, conditions, hit counts, tracepoint messages,
/// watches, and the margin markers each open editor shows for its file.
/// </summary>
public sealed partial class BreakpointEditingViewModel : ObservableObject, IDisposable
{
    private const int BreakpointsTab = 2, WatchesTab = 1;
    private readonly IBreakpointEditingHost _host;
    private readonly DebuggerViewModel _debugger;
    private readonly IMessenger _messenger;
    private readonly HashSet<BreakpointViewModel> _observed = [];
    private bool _disposed;

    public BreakpointEditingViewModel(IBreakpointEditingHost host, DebuggerViewModel debugger, IMessenger messenger)
    {
        _host = host; _debugger = debugger; _messenger = messenger;
        _debugger.Breakpoints.CollectionChanged += BreakpointsChanged;
    }

    private void BreakpointsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Observe();
    private void Observe()
    {
        foreach (var removed in _observed.Where(b => !_debugger.Breakpoints.Contains(b)).ToArray()) { removed.PropertyChanged -= BreakpointChanged; _observed.Remove(removed); }
        foreach (var added in _debugger.Breakpoints.Where(b => !_observed.Contains(b))) { added.PropertyChanged += BreakpointChanged; _observed.Add(added); }
        RefreshMarkers();
    }
    private void BreakpointChanged(object? sender, PropertyChangedEventArgs e) => RefreshMarkers();

    /// <summary>Rebuilds every open editor's breakpoint margin from the debugger's list; call after opening a document.</summary>
    public void RefreshMarkers()
    {
        foreach (var doc in _host.Documents)
        {
            doc.BreakpointLines.Clear(); doc.BreakpointMarkers.Clear();
            foreach (var bp in _debugger.Breakpoints.Where(b => b.Path.Equals(doc.State.Path, StringComparison.OrdinalIgnoreCase)))
            {
                if (bp.Enabled) doc.BreakpointLines.Add(bp.Line);
                doc.BreakpointMarkers.Add(new(bp.Line, bp.Enabled, bp.Status == "Bound", bp.Condition, bp.Status, bp.HitCondition, bp.LogMessage));
            }
        }
    }

    [RelayCommand] private void Show() { _debugger.SelectedTab = BreakpointsTab; _messenger.Send(new ShowToolMessage(Workbenches.Debugger)); }

    [RelayCommand] private Task ToggleAsync() => _host.ActiveDocument is not { State.Extension: ".cs" } doc ? Task.CompletedTask
        : _host.GuardAsync(() => _debugger.ToggleBreakpointAsync(doc.State.Path, doc.State.CaretLine));

    [RelayCommand] private Task ConditionAsync() => _host.GuardAsync(async () =>
    {
        if (_host.ActiveDocument is not { State.Extension: ".cs" } doc) return;
        var breakpoint = AtCaret(doc);
        var condition = await _host.Dialogs.PromptAsync("Breakpoint condition", "Break when this C# expression is true (leave empty for every hit):", breakpoint?.Condition ?? "");
        if (condition == null) return;
        breakpoint ??= await AddAtCaretAsync(doc);
        if (breakpoint == null) return;
        breakpoint.Condition = condition; _debugger.SelectedBreakpoint = breakpoint;
        await _debugger.ApplyBreakpointsCommand.ExecuteAsync(null); Show();
    });

    [RelayCommand] private Task HitCountAsync() => _host.GuardAsync(async () =>
    {
        if (_host.ActiveDocument is not { State.Extension: ".cs" } doc) return;
        var breakpoint = AtCaret(doc);
        var text = await _host.Dialogs.PromptAsync("Breakpoint hit count", "Break on hit 5, >=5 from the fifth hit, or %5 on every fifth hit (leave empty for every hit):", breakpoint?.HitCondition ?? "");
        if (text == null) return;
        if (!HitCondition.TryParse(text, out _, out var error)) { await _host.Dialogs.ShowErrorAsync("Breakpoint hit count", error!); return; }
        breakpoint ??= await AddAtCaretAsync(doc);
        if (breakpoint == null) return;
        breakpoint.HitCondition = text.Trim(); _debugger.SelectedBreakpoint = breakpoint;
        await _debugger.ApplyBreakpointsCommand.ExecuteAsync(null);
        _host.ReportStatus("Breakpoint " + breakpoint.Summary.ToLowerInvariant());
    });

    [RelayCommand] private Task LogMessageAsync() => _host.GuardAsync(async () =>
    {
        if (_host.ActiveDocument is not { State.Extension: ".cs" } doc) return;
        var breakpoint = AtCaret(doc);
        var text = await _host.Dialogs.PromptAsync("Tracepoint", "Write this message to Output and continue. Use {expression} for values and $FUNCTION, $TID, $FILEPOS or $HITCOUNT (leave empty to break instead):", breakpoint?.LogMessage ?? "");
        if (text == null) return;
        breakpoint ??= await AddAtCaretAsync(doc);
        if (breakpoint == null) return;
        breakpoint.LogMessage = text.Trim(); _debugger.SelectedBreakpoint = breakpoint;
        await _debugger.ApplyBreakpointsCommand.ExecuteAsync(null);
        _host.ReportStatus(breakpoint.IsTracepoint ? "Tracepoint set; its messages appear in Debugger > Output" : "Breakpoint breaks again instead of logging");
    });

    [RelayCommand] private Task AddWatchAsync() => _host.GuardAsync(async () =>
    {
        if (_host.ActiveDocument is not { IsCSharp: true } doc || doc.DebugExpressionAt(doc.State.CaretOffset) is not { } expression)
        { _host.ReportStatus("Place the caret on a variable or member, or select an expression, to add a watch"); return; }
        await _debugger.AddWatchAsync(expression);
        _debugger.SelectedTab = WatchesTab; _messenger.Send(new ShowToolMessage(Workbenches.Debugger));
    });

    [RelayCommand] private Task DisableAsync() => _host.GuardAsync(async () =>
    {
        if (_host.ActiveDocument is not { } doc) return;
        var breakpoint = AtCaret(doc);
        if (breakpoint == null) { _host.ReportStatus("No breakpoint on this line"); return; }
        breakpoint.Enabled = !breakpoint.Enabled; await _debugger.ApplyBreakpointsCommand.ExecuteAsync(null);
    });

    private BreakpointViewModel? AtCaret(EditorViewModel doc) =>
        _debugger.Breakpoints.FirstOrDefault(b => b.Path.Equals(doc.State.Path, StringComparison.OrdinalIgnoreCase) && b.Line == doc.State.CaretLine);
    private async Task<BreakpointViewModel?> AddAtCaretAsync(EditorViewModel doc)
    {
        await _debugger.ToggleBreakpointAsync(doc.State.Path, doc.State.CaretLine);
        return AtCaret(doc);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debugger.Breakpoints.CollectionChanged -= BreakpointsChanged;
        foreach (var breakpoint in _observed) breakpoint.PropertyChanged -= BreakpointChanged;
        _observed.Clear();
    }
}
