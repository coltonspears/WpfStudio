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
        Settings = new(SaveSettingsAsync);
        _debugger.Breakpoints.CollectionChanged += BreakpointsChanged;
        _messenger.Register<BreakpointEditingViewModel, EditBreakpointMessage>(this, static (editing, message) => editing.Settings.Open(message.Breakpoint.Path, message.Breakpoint.Line, message.Breakpoint));
    }

    /// <summary>The settings form of one breakpoint: its condition, hit count and tracepoint message.</summary>
    public BreakpointSettingsViewModel Settings { get; }

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

    // Each opens the same settings form at the caret line, with its own part turned on and focused.
    [RelayCommand] private void EditSettings() => OpenSettingsAtCaret(null);
    [RelayCommand] private void Condition() => OpenSettingsAtCaret(BreakpointSettingsFocus.Condition);
    [RelayCommand] private void HitCount() => OpenSettingsAtCaret(BreakpointSettingsFocus.HitCount);
    [RelayCommand] private void LogMessage() => OpenSettingsAtCaret(BreakpointSettingsFocus.LogMessage);

    private void OpenSettingsAtCaret(BreakpointSettingsFocus? focus)
    {
        if (_host.ActiveDocument is not { State.Extension: ".cs" } doc) { _host.ReportStatus("Place the caret on a line of C# code to edit its breakpoint"); return; }
        Settings.Open(doc.State.Path, doc.State.CaretLine, AtCaret(doc), focus);
    }

    private Task SaveSettingsAsync(BreakpointSettingsViewModel form) => _host.GuardAsync(async () =>
    {
        var breakpoint = At(form.Path, form.Line);
        if (breakpoint == null)
        {
            await _debugger.ToggleBreakpointAsync(form.Path, form.Line);
            breakpoint = At(form.Path, form.Line);
        }
        if (breakpoint == null) return;
        breakpoint.Condition = form.ConditionValue;
        breakpoint.HitCondition = form.HitConditionValue;
        breakpoint.LogMessage = form.LogMessageValue;
        _debugger.SelectedBreakpoint = breakpoint;
        await _debugger.ApplyBreakpointsCommand.ExecuteAsync(null);
        _host.ReportStatus(breakpoint.IsTracepoint ? $"Tracepoint at {form.Location}; its messages appear in Debugger > Output" : $"Breakpoint at {form.Location}: {form.Summary}");
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

    private BreakpointViewModel? AtCaret(EditorViewModel doc) => At(doc.State.Path, doc.State.CaretLine);
    private BreakpointViewModel? At(string path, int line) =>
        _debugger.Breakpoints.FirstOrDefault(b => b.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && b.Line == line);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messenger.UnregisterAll(this);
        _debugger.Breakpoints.CollectionChanged -= BreakpointsChanged;
        foreach (var breakpoint in _observed) breakpoint.PropertyChanged -= BreakpointChanged;
        _observed.Clear();
    }
}
