using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.Runtime.Debugging;

public sealed partial class DebuggerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DebugSession session;
    private readonly IUiDispatcher dispatcher;
    private readonly IDebugAdapterProvider adapters;
    private readonly DapClient client;
    private readonly BreakpointStore store = new();
    private readonly OutputBuffer output;
    private int threadId;
    private int stopVersion;
    private int watchRefresh;
    private int sessionEnds;
    public DebuggerViewModel(DebugSession session, IUiDispatcher dispatcher, IDebugAdapterProvider adapters)
    {
        this.session = session; this.dispatcher = dispatcher; this.adapters = adapters;
        client = new DapClient(session);
        output = new OutputBuffer(dispatcher, text => Output = text);
        if (adapters.Locate() is { } adapter) session.AdapterPath = adapter;
        session.EventReceived += OnDebugEvent;
        session.Output += output.Append;
        session.Error += OnError;
    }
    public event Action<string, int>? SourceRequested;
    public string AdapterDescription => adapters.Description;
    public bool IsAdapterMissing => !File.Exists(session.AdapterPath);
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallAdapterCommand))] public partial bool IsInstallingAdapter { get; set; }
    [ObservableProperty] public partial string AdapterStatus { get; set; } = "";
    private bool CanInstallAdapter() => !IsInstallingAdapter;
    [RelayCommand(CanExecute = nameof(CanInstallAdapter))] private async Task InstallAdapterAsync()
    {
        IsInstallingAdapter = true; Error = "";
        try
        {
            session.AdapterPath = await adapters.InstallAsync(new Progress<string>(message => AdapterStatus = message), CancellationToken.None);
            AdapterStatus = "Installed " + AdapterDescription;
            Status = "Ready · .NET 8–10 managed debugging";
        }
        catch (Exception ex) { AdapterStatus = ""; Error = "Debugger installation failed: " + ex.Message; }
        finally { IsInstallingAdapter = false; OnPropertyChanged(nameof(IsAdapterMissing)); }
    }
    /// <summary>True when the adapter is present, installing it first if the user agrees.</summary>
    public async Task<bool> EnsureAdapterAsync(Func<Task<bool>> confirm)
    {
        if (!IsAdapterMissing) return true;
        if (!await confirm()) return false;
        await InstallAdapterAsync();
        return !IsAdapterMissing;
    }
    public ObservableCollection<BreakpointViewModel> Breakpoints => store.Breakpoints;
    public ObservableCollection<DebugThread> Threads { get; } = [];
    public ObservableCollection<DebugFrame> Frames { get; } = [];
    public ObservableCollection<DebugVariableViewModel> Locals { get; } = [];
    public ObservableCollection<DebugVariableViewModel> Watches { get; } = [];
    [ObservableProperty] public partial string Status { get; set; } = "Ready · .NET 8–10 managed debugging";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string Output { get; set; } = "";
    [ObservableProperty] public partial string ExceptionDetails { get; set; } = "";
    [ObservableProperty] public partial int AttachProcessId { get; set; }
    [ObservableProperty] public partial string WatchExpression { get; set; } = "";
    [ObservableProperty] public partial bool BreakOnThrown { get; set; }
    [ObservableProperty] public partial DebugThread? SelectedThread { get; set; }
    [ObservableProperty] public partial DebugFrame? SelectedFrame { get; set; }
    [ObservableProperty] public partial BreakpointViewModel? SelectedBreakpoint { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDetach)), NotifyCanExecuteChangedFor(nameof(DetachCommand))]
    public partial bool IsActive { get; set; }
    public bool CanDetach => IsActive && session.CanDetach;
    [ObservableProperty] public partial bool IsStopped { get; set; }
    [ObservableProperty] public partial int SelectedTab { get; set; }
    partial void OnSelectedThreadChanged(DebugThread? value) { if (value is not null && IsStopped) { threadId = value.Id; _ = GuardAsync(RefreshFramesAsync); } }
    partial void OnSelectedFrameChanged(DebugFrame? value) { if (value is not null && IsStopped) _ = GuardAsync(() => RefreshFrameAsync(value)); }
    partial void OnBreakOnThrownChanged(bool value) { if (IsActive) _ = GuardAsync(() => session.SetExceptionsAsync(value)); }

    public async Task SetWorkspaceAsync(string workspacePath)
    {
        ResetWatchRows();
        try
        {
            await store.LoadAsync(workspacePath);
            foreach (var expression in store.WatchExpressions) Watches.Add(new DebugVariableViewModel(expression, "Run to evaluate", null, 0));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Error = "Could not load debug settings: " + ex.Message; }
    }

    public async Task LaunchAsync(DebugLaunchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Error = ""; ExceptionDetails = ""; Status = "Starting debugger…";
        store.ResetForSession();
        var ends = sessionEnds;
        try
        {
            await session.LaunchAsync(configuration, store.ToModels(), BreakOnThrown, cancellationToken);
            await EnterRunningAsync(ends);
        }
        catch (Exception ex) { Error = ex.Message; Status = "Debug launch failed"; IsActive = false; throw; }
    }

    public async Task ToggleBreakpointAsync(string path, int line)
    {
        if (line < 1) return;
        path = Path.GetFullPath(path);
        store.Toggle(path, line);
        await GuardAsync(async () => { await store.SaveAsync(); if (IsActive) await ApplyFileBreakpointsAsync(path); });
    }

    [RelayCommand] private Task AttachAsync() => GuardAsync(async () =>
    {
        if (IsAdapterMissing) { Error = "Install the debugger first: choose Install debugger above."; return; }
        Status = "Attaching…";
        store.ResetForSession();
        var ends = sessionEnds;
        await session.AttachAsync(AttachProcessId, store.ToModels(), BreakOnThrown);
        await EnterRunningAsync(ends);
    });
    /// <summary>The adapter accepted the start, but a program that exits at once may already have ended the session, and nothing here may reopen it.</summary>
    private async Task EnterRunningAsync(int ends)
    {
        if (ends != sessionEnds) return;
        IsActive = true; if (!IsStopped) Status = "Running";
        try { foreach (var path in store.Files) await ApplyFileBreakpointsAsync(path); }
        catch (Exception) when (ends != sessionEnds) { } // The session ending is why this failed, and the pane already says it ended.
    }
    [RelayCommand] private Task ContinueAsync() => GuardAsync(async () => { await session.ContinueAsync(threadId); IsStopped = false; Status = "Running"; });
    [RelayCommand] private Task PauseAsync() => GuardAsync(() => session.PauseAsync(threadId));
    [RelayCommand] private Task StepOverAsync() => GuardAsync(() => session.StepAsync("next", threadId));
    [RelayCommand] private Task StepIntoAsync() => GuardAsync(() => session.StepAsync("stepIn", threadId));
    [RelayCommand] private Task StepOutAsync() => GuardAsync(() => session.StepAsync("stepOut", threadId));
    [RelayCommand] private Task StopAsync() => GuardAsync(async () => { await session.StopAsync(); EndSession(); });
    [RelayCommand(CanExecute = nameof(CanDetach))] private Task DetachAsync() => GuardAsync(async () => { await session.StopAsync(false); EndSession(); });
    [RelayCommand] private Task ApplyBreakpointsAsync() => GuardAsync(async () =>
    {
        await store.SaveAsync();
        if (IsActive) foreach (var path in store.Files) await ApplyFileBreakpointsAsync(path);
    });
    [RelayCommand] private Task RemoveBreakpointAsync() => GuardAsync(async () =>
    {
        if (SelectedBreakpoint is not { } selected) return;
        Breakpoints.Remove(selected); await store.SaveAsync(); if (IsActive) await ApplyFileBreakpointsAsync(selected.Path);
    });
    [RelayCommand] private void NavigateBreakpoint() { if (SelectedBreakpoint is { } selected) SourceRequested?.Invoke(selected.Path, selected.Line); }
    [RelayCommand] private Task EnableAllBreakpointsAsync() => SetAllBreakpointsAsync(true);
    [RelayCommand] private Task DisableAllBreakpointsAsync() => SetAllBreakpointsAsync(false);
    private Task SetAllBreakpointsAsync(bool enabled) => GuardAsync(async () =>
    {
        foreach (var breakpoint in Breakpoints) breakpoint.Enabled = enabled;
        await store.SaveAsync();
        if (IsActive) foreach (var path in store.Files) await ApplyFileBreakpointsAsync(path);
    });
    [RelayCommand] private Task AddWatchAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(WatchExpression)) return;
        var expression = WatchExpression.Trim(); if (!store.WatchExpressions.Contains(expression)) store.WatchExpressions.Add(expression);
        WatchExpression = ""; await store.SaveAsync();
        if (SelectedFrame is not null && IsStopped) await RefreshWatchesAsync(SelectedFrame.Id);
        else { ResetWatchRows(); foreach (var value in store.WatchExpressions) Watches.Add(new DebugVariableViewModel(value, "Run to evaluate", null, 0)); }
    });
    [RelayCommand] private Task ClearWatchesAsync() => GuardAsync(async () => { store.WatchExpressions.Clear(); ResetWatchRows(); await store.SaveAsync(); });
    [RelayCommand] private void ClearOutput() => output.Clear();

    private async Task ApplyFileBreakpointsAsync(string path)
    {
        var items = store.EnabledIn(path);
        var answers = await client.SetBreakpointsAsync(path, items.Select(x => x.ToModel()));
        foreach (var (item, answer) in items.Zip(answers)) item.Apply(answer);
    }

    private void OnDebugEvent(DebugEvent message) => dispatcher.Post(() =>
    {
        if (message.Name == "stopped")
        {
            var stopped = DapMessages.Stopped(message.Body);
            // Hit counts and tracepoints decide whether this stop is shown or silently resumed.
            if (stopped.Reason == "breakpoint" && Breakpoints.Any(b => b.Enabled && b.HasClientBehavior))
            {
                var version = ++stopVersion;
                _ = GuardAsync(() => HandleBreakpointStopAsync(stopped, version));
                return;
            }
            EnterStopped(stopped);
        }
        else if (message.Name == "continued") { IsStopped = false; Status = "Running"; stopVersion++; Locals.Clear(); }
        else if (message.Name is "terminated" or "exited") { EndSession(); _ = GuardAsync(async () => await session.DisposeAsync()); }
        else if (message.Name == "breakpoint" && DapMessages.ChangedBreakpoint(message.Body) is { SourcePath: not null } changed)
            foreach (var item in store.Matching(changed)) item.ApplyChange(changed);
    });

    private void EnterStopped(DapStopped stopped)
    {
        IsActive = true; IsStopped = true; threadId = stopped.ThreadId; stopVersion++;
        Status = "Paused · " + (stopped.Reason ?? "breakpoint");
        _ = GuardAsync(async () =>
        {
            var threads = await client.GetThreadsAsync();
            SelectedThread = null;
            Threads.Clear(); foreach (var thread in threads) Threads.Add(thread);
            SelectedThread = Threads.FirstOrDefault(x => x.Id == threadId) ?? Threads.FirstOrDefault();
            if (stopped.Reason == "exception")
            {
                var info = await client.GetExceptionInfoAsync(threadId);
                ExceptionDetails = info.ExceptionId + "\n" + info.Description + (info.Details is { } details ? "\n" + details : "");
            }
        });
    }

    /// <summary>
    /// Applies a breakpoint's hit count and log message. The adapter stops on every hit whose condition is true;
    /// stops that do not reach the hit count, and every tracepoint hit, continue without showing a paused state.
    /// </summary>
    private async Task HandleBreakpointStopAsync(DapStopped stopped, int version)
    {
        var stoppedThread = stopped.ThreadId;
        var top = (await client.GetStackTraceAsync(stoppedThread, levels: 1)).FirstOrDefault();
        if (version != stopVersion) return;
        var path = top?.Path ?? "";
        var line = top?.Line ?? 0;
        var breakpoint = store.FindHit(stopped.HitBreakpointIds, path, line);
        if (breakpoint is null || !breakpoint.HasClientBehavior) { EnterStopped(stopped); return; }
        breakpoint.HitCount++;
        if (!WpfStudio.Contracts.HitCondition.TryParse(breakpoint.HitCondition, out var condition, out _)) condition = WpfStudio.Contracts.HitCondition.Always;
        if (condition.ShouldBreak(breakpoint.HitCount) && breakpoint.IsTracepoint && top is { } logged)
        {
            var frameId = logged.Id;
            var keywords = new Dictionary<string, string>
            {
                ["FUNCTION"] = logged.Name, ["TID"] = stoppedThread.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["FILEPOS"] = $"{Path.GetFileName(path)}({line})", ["HITCOUNT"] = breakpoint.HitCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            var text = await WpfStudio.Contracts.TracepointMessage.FormatAsync(breakpoint.LogMessage, expression => EvaluateValueAsync(expression, frameId), keywords);
            output.Append(text + Environment.NewLine);
        }
        else if (condition.ShouldBreak(breakpoint.HitCount)) { EnterStopped(stopped); return; }
        if (version != stopVersion) return;
        await session.ContinueAsync(stoppedThread);
    }

    private async Task<string> EvaluateValueAsync(string expression, int frameId) => (await client.EvaluateAsync(expression, frameId, "watch")).Result;

    /// <summary>Evaluates a hovered expression in the selected frame for an editor DataTip; null when not paused or not evaluable.</summary>
    public async Task<string?> EvaluateDataTipAsync(string expression, CancellationToken cancellationToken = default)
    {
        if (!IsStopped || SelectedFrame is not { } frame) return null;
        var version = stopVersion;
        try
        {
            var value = await client.EvaluateAsync(expression, frame.Id, "hover", cancellationToken);
            if (version != stopVersion || !IsStopped) return null;
            return expression + " = " + value.Result + (value.Type.Length > 0 ? "  (" + value.Type + ")" : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Adds an expression to Watches, evaluating it immediately when paused.</summary>
    public Task AddWatchAsync(string expression)
    {
        WatchExpression = expression;
        return AddWatchCommand.ExecuteAsync(null);
    }

    private async Task RefreshFramesAsync()
    {
        var version = stopVersion;
        var frames = await client.GetStackTraceAsync(threadId, levels: 100);
        if (version != stopVersion || !IsStopped) return;
        SelectedFrame = null; Frames.Clear();
        foreach (var frame in frames) Frames.Add(frame);
        SelectedFrame = Frames.FirstOrDefault();
    }
    private async Task RefreshFrameAsync(DebugFrame frame)
    {
        if (!string.IsNullOrWhiteSpace(frame.Path) && frame.Line > 0) SourceRequested?.Invoke(frame.Path, frame.Line);
        var version = stopVersion;
        var scopes = await client.GetScopesAsync(frame.Id);
        if (version != stopVersion || SelectedFrame?.Id != frame.Id || !IsStopped) return;
        Locals.Clear();
        foreach (var scope in scopes)
        {
            var variables = await LoadVariablesAsync(scope.VariablesReference);
            if (version != stopVersion || SelectedFrame?.Id != frame.Id) return;
            foreach (var item in variables) Locals.Add(item);
        }
        await RefreshWatchesAsync(frame.Id);
    }
    private Task<IReadOnlyList<DebugVariableViewModel>> LoadVariablesAsync(int reference) => LoadVariablesPageAsync(reference, 0);
    private async Task<IReadOnlyList<DebugVariableViewModel>> LoadVariablesPageAsync(int reference, int start)
    {
        var variables = await client.GetVariablesAsync(reference, start, 200);
        var items = variables.Select(value => new DebugVariableViewModel(value.Name, value.Value, value.Type, value.VariablesReference, LoadVariablesAsync)).ToList();
        if (items.Count == 200) items.Add(new DebugVariableViewModel("Next 200 values…", "Expand to load", null, reference, _ => LoadVariablesPageAsync(reference, start + 200)));
        return items;
    }
    private async Task RefreshWatchesAsync(int frameId)
    {
        var version = stopVersion;
        var refresh = ResetWatchRows();
        bool Current() => refresh == watchRefresh && version == stopVersion && IsStopped && SelectedFrame?.Id == frameId;
        foreach (var expression in store.WatchExpressions.ToArray())
        {
            try
            {
                var value = await client.EvaluateAsync(expression, frameId, "watch");
                if (!Current()) return;
                Watches.Add(new DebugVariableViewModel(expression, value.Result, value.Type, value.VariablesReference, LoadVariablesAsync));
            }
            catch (Exception ex) { if (!Current()) return; Watches.Add(new DebugVariableViewModel(expression, ex.Message, null, 0)); }
        }
    }
    /// <summary>Empties the rows and supersedes any refresh still awaiting the adapter, whose answers would land on top of the newer rows.</summary>
    private int ResetWatchRows() { Watches.Clear(); return ++watchRefresh; }
    private void EndSession() { sessionEnds++; IsActive = false; IsStopped = false; Status = "Session ended"; store.MarkPending(); stopVersion++; SelectedThread = null; SelectedFrame = null; Threads.Clear(); Frames.Clear(); Locals.Clear(); }
    private void OnError(string value) => dispatcher.Post(() => { Error = value; EndSession(); });
    private async Task GuardAsync(Func<Task> action) { try { Error = ""; await action(); } catch (Exception ex) { Error = ex.Message; } }
    public async ValueTask DisposeAsync() { output.Close(); session.EventReceived -= OnDebugEvent; session.Output -= output.Append; session.Error -= OnError; await session.DisposeAsync(); }
}
