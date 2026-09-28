using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.Runtime.Debugging;

public sealed record DebugThread(int Id, string Name);
public sealed record DebugFrame(int Id, string Name, string? Path, int Line);

public sealed partial class BreakpointViewModel : ObservableObject
{
    public required string Path { get; init; }
    public required int Line { get; init; }
    [ObservableProperty] public partial string Condition { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial string Status { get; set; } = "Pending";
    public SourceBreakpoint ToModel() => new(Path, Line, Condition, Enabled);
}

public sealed partial class DebugVariableViewModel : ObservableObject
{
    private readonly Func<int, Task<IReadOnlyList<DebugVariableViewModel>>>? loader;
    private bool loaded;
    public DebugVariableViewModel(string name, string value, string? type, int reference, Func<int, Task<IReadOnlyList<DebugVariableViewModel>>>? loader = null)
    {
        Name = name; Value = value; Type = type; Reference = reference; this.loader = loader;
        if (reference > 0) Children.Add(new DebugVariableViewModel("Expand to load", "", null, 0));
    }
    public string Name { get; }
    public string Value { get; }
    public string? Type { get; }
    public int Reference { get; }
    public ObservableCollection<DebugVariableViewModel> Children { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    partial void OnIsExpandedChanged(bool value) { if (value && !loaded && Reference > 0) _ = LoadAsync(); }
    private async Task LoadAsync()
    {
        loaded = true;
        try
        {
            var values = loader is null ? [] : await loader(Reference);
            Children.Clear(); foreach (var value in values) Children.Add(value);
        }
        catch (Exception ex) { Children.Clear(); Children.Add(new DebugVariableViewModel("Error", ex.Message, null, 0)); loaded = false; }
    }
}

public sealed partial class DebuggerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DebugSession session;
    private readonly IUiDispatcher dispatcher;
    private readonly object outputGate = new();
    private readonly StringBuilder pendingOutput = new();
    private bool outputScheduled;
    private bool disposed;
    private string? statePath;
    private int threadId;
    private int stopVersion;
    private readonly List<string> watchExpressions = [];
    public DebuggerViewModel(DebugSession session, IUiDispatcher dispatcher)
    {
        this.session = session; this.dispatcher = dispatcher;
        session.EventReceived += OnDebugEvent;
        session.Output += AppendOutput;
        session.Error += OnError;
    }
    public event Action<string, int>? SourceRequested;
    public ObservableCollection<BreakpointViewModel> Breakpoints { get; } = [];
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
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspacePath).ToUpperInvariant())))[..20];
        statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio", "debugging", hash + ".json");
        Breakpoints.Clear(); watchExpressions.Clear(); Watches.Clear();
        if (!File.Exists(statePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<DebugWorkspaceState>(await File.ReadAllTextAsync(statePath));
            if (state is null) return;
            foreach (var item in state.Breakpoints) Breakpoints.Add(new BreakpointViewModel { Path = item.Path, Line = item.Line, Condition = item.Condition ?? "", Enabled = item.Enabled });
            watchExpressions.AddRange(state.Watches);
            foreach (var expression in watchExpressions) Watches.Add(new DebugVariableViewModel(expression, "Run to evaluate", null, 0));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Error = "Could not load debug settings: " + ex.Message; }
    }

    public async Task LaunchAsync(DebugLaunchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Error = ""; ExceptionDetails = ""; Status = "Starting debugger…";
        try
        {
            await session.LaunchAsync(configuration, Breakpoints.Select(x => x.ToModel()).ToArray(), BreakOnThrown, cancellationToken);
            IsActive = true; if (!IsStopped) Status = "Running";
            foreach (var path in Breakpoints.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase)) await ApplyFileBreakpointsAsync(path);
        }
        catch (Exception ex) { Error = ex.Message; Status = "Debug launch failed"; IsActive = false; throw; }
    }

    public async Task ToggleBreakpointAsync(string path, int line)
    {
        if (line < 1) return;
        path = Path.GetFullPath(path);
        var existing = Breakpoints.FirstOrDefault(x => x.Line == line && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) Breakpoints.Add(new BreakpointViewModel { Path = path, Line = line }); else Breakpoints.Remove(existing);
        await GuardAsync(async () => { await SaveStateAsync(); if (IsActive) await ApplyFileBreakpointsAsync(path); });
    }

    [RelayCommand] private Task AttachAsync() => GuardAsync(async () =>
    {
        Status = "Attaching…";
        await session.AttachAsync(AttachProcessId, Breakpoints.Select(x => x.ToModel()).ToArray(), BreakOnThrown);
        IsActive = true; if (!IsStopped) Status = "Running";
        foreach (var path in Breakpoints.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase)) await ApplyFileBreakpointsAsync(path);
    });
    [RelayCommand] private Task ContinueAsync() => GuardAsync(async () => { await session.ContinueAsync(threadId); IsStopped = false; Status = "Running"; });
    [RelayCommand] private Task PauseAsync() => GuardAsync(() => session.PauseAsync(threadId));
    [RelayCommand] private Task StepOverAsync() => GuardAsync(() => session.StepAsync("next", threadId));
    [RelayCommand] private Task StepIntoAsync() => GuardAsync(() => session.StepAsync("stepIn", threadId));
    [RelayCommand] private Task StepOutAsync() => GuardAsync(() => session.StepAsync("stepOut", threadId));
    [RelayCommand] private Task StopAsync() => GuardAsync(async () => { await session.StopAsync(); EndSession(); });
    [RelayCommand(CanExecute = nameof(CanDetach))] private Task DetachAsync() => GuardAsync(async () => { await session.StopAsync(false); EndSession(); });
    [RelayCommand] private Task ApplyBreakpointsAsync() => GuardAsync(async () =>
    {
        await SaveStateAsync();
        if (IsActive) foreach (var path in Breakpoints.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase)) await ApplyFileBreakpointsAsync(path);
    });
    [RelayCommand] private Task RemoveBreakpointAsync() => GuardAsync(async () =>
    {
        if (SelectedBreakpoint is not { } selected) return;
        Breakpoints.Remove(selected); await SaveStateAsync(); if (IsActive) await ApplyFileBreakpointsAsync(selected.Path);
    });
    [RelayCommand] private void NavigateBreakpoint() { if (SelectedBreakpoint is { } selected) SourceRequested?.Invoke(selected.Path, selected.Line); }
    [RelayCommand] private Task EnableAllBreakpointsAsync() => SetAllBreakpointsAsync(true);
    [RelayCommand] private Task DisableAllBreakpointsAsync() => SetAllBreakpointsAsync(false);
    private Task SetAllBreakpointsAsync(bool enabled) => GuardAsync(async () =>
    {
        foreach (var breakpoint in Breakpoints) breakpoint.Enabled = enabled;
        await SaveStateAsync();
        if (IsActive) foreach (var path in Breakpoints.Select(b => b.Path).Distinct(StringComparer.OrdinalIgnoreCase)) await ApplyFileBreakpointsAsync(path);
    });
    [RelayCommand] private Task AddWatchAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(WatchExpression)) return;
        var expression = WatchExpression.Trim(); if (!watchExpressions.Contains(expression)) watchExpressions.Add(expression);
        WatchExpression = ""; await SaveStateAsync();
        if (SelectedFrame is not null && IsStopped) await RefreshWatchesAsync(SelectedFrame.Id);
        else { Watches.Clear(); foreach (var value in watchExpressions) Watches.Add(new DebugVariableViewModel(value, "Run to evaluate", null, 0)); }
    });
    [RelayCommand] private Task ClearWatchesAsync() => GuardAsync(async () => { watchExpressions.Clear(); Watches.Clear(); await SaveStateAsync(); });
    [RelayCommand] private void ClearOutput() { lock (outputGate) pendingOutput.Clear(); Output = ""; }

    private async Task ApplyFileBreakpointsAsync(string path)
    {
        var items = Breakpoints.Where(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase) && x.Enabled).ToArray();
        var result = await session.SetBreakpointsAsync(path, items.Select(x => x.ToModel()));
        if (!result.TryGetProperty("breakpoints", out var values)) return;
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (index >= items.Length) break;
            items[index++].Status = value.TryGetProperty("verified", out var verified) && verified.GetBoolean() ? "Bound" : Text(value, "message", "Unbound · symbols not loaded");
        }
    }

    private void OnDebugEvent(DebugEvent message) => dispatcher.Post(() =>
    {
        if (message.Name == "stopped")
        {
            IsActive = true; IsStopped = true; threadId = Int(message.Body, "threadId"); stopVersion++;
            Status = "Paused · " + Text(message.Body, "reason", "breakpoint");
            _ = GuardAsync(async () =>
            {
                var response = await session.RequestAsync("threads");
                SelectedThread = null;
                Threads.Clear(); if (response.TryGetProperty("threads", out var threads)) foreach (var thread in threads.EnumerateArray()) Threads.Add(new DebugThread(Int(thread, "id"), Text(thread, "name")));
                SelectedThread = Threads.FirstOrDefault(x => x.Id == threadId) ?? Threads.FirstOrDefault();
                if (Text(message.Body, "reason") == "exception")
                {
                    var info = await session.RequestAsync("exceptionInfo", new { threadId });
                    ExceptionDetails = Text(info, "exceptionId") + "\n" + Text(info, "description") + (info.TryGetProperty("details", out var details) ? "\n" + details.ToString() : "");
                }
            });
        }
        else if (message.Name == "continued") { IsStopped = false; Status = "Running"; stopVersion++; Locals.Clear(); }
        else if (message.Name is "terminated" or "exited") { EndSession(); _ = GuardAsync(async () => await session.DisposeAsync()); }
        else if (message.Name == "breakpoint" && message.Body.TryGetProperty("breakpoint", out var changed) && changed.TryGetProperty("source", out var source))
        {
            var path = Text(source, "path"); var line = Int(changed, "line");
            foreach (var item in Breakpoints.Where(x => x.Line == line && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                item.Status = changed.TryGetProperty("verified", out var verified) && verified.GetBoolean() ? "Bound" : Text(changed, "message", "Unbound");
        }
    });

    private async Task RefreshFramesAsync()
    {
        var version = stopVersion;
        var response = await session.RequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 100 });
        if (version != stopVersion || !IsStopped) return;
        SelectedFrame = null; Frames.Clear();
        if (response.TryGetProperty("stackFrames", out var frames)) foreach (var frame in frames.EnumerateArray())
            Frames.Add(new DebugFrame(Int(frame, "id"), Text(frame, "name"), frame.TryGetProperty("source", out var source) ? Text(source, "path") : null, Int(frame, "line")));
        SelectedFrame = Frames.FirstOrDefault();
    }
    private async Task RefreshFrameAsync(DebugFrame frame)
    {
        if (!string.IsNullOrWhiteSpace(frame.Path) && frame.Line > 0) SourceRequested?.Invoke(frame.Path, frame.Line);
        var version = stopVersion;
        var result = await session.RequestAsync("scopes", new { frameId = frame.Id });
        if (version != stopVersion || SelectedFrame?.Id != frame.Id || !IsStopped) return;
        Locals.Clear();
        if (result.TryGetProperty("scopes", out var scopes)) foreach (var scope in scopes.EnumerateArray())
        {
            var variables = await LoadVariablesAsync(Int(scope, "variablesReference"));
            if (version != stopVersion || SelectedFrame?.Id != frame.Id) return;
            foreach (var item in variables) Locals.Add(item);
        }
        await RefreshWatchesAsync(frame.Id);
    }
    private Task<IReadOnlyList<DebugVariableViewModel>> LoadVariablesAsync(int reference) => LoadVariablesPageAsync(reference, 0);
    private async Task<IReadOnlyList<DebugVariableViewModel>> LoadVariablesPageAsync(int reference, int start)
    {
        var result = await session.RequestAsync("variables", new { variablesReference = reference, start, count = 200 });
        var items = result.TryGetProperty("variables", out var variables) ? variables.EnumerateArray().Select(value => new DebugVariableViewModel(Text(value, "name"), Text(value, "value"), Text(value, "type"), Int(value, "variablesReference"), LoadVariablesAsync)).ToList() : [];
        if (items.Count == 200) items.Add(new DebugVariableViewModel("Next 200 values…", "Expand to load", null, reference, _ => LoadVariablesPageAsync(reference, start + 200)));
        return items;
    }
    private async Task RefreshWatchesAsync(int frameId)
    {
        var version = stopVersion;
        Watches.Clear();
        foreach (var expression in watchExpressions.ToArray())
        {
            try
            {
                var value = await session.RequestAsync("evaluate", new { expression, frameId, context = "watch" });
                if (version != stopVersion || !IsStopped || SelectedFrame?.Id != frameId) return;
                Watches.Add(new DebugVariableViewModel(expression, Text(value, "result"), Text(value, "type"), Int(value, "variablesReference"), LoadVariablesAsync));
            }
            catch (Exception ex) { Watches.Add(new DebugVariableViewModel(expression, ex.Message, null, 0)); }
        }
    }
    private async Task SaveStateAsync()
    {
        if (statePath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary = statePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new DebugWorkspaceState(Breakpoints.Select(x => x.ToModel()).ToArray(), watchExpressions.ToArray())));
        File.Move(temporary, statePath, true);
    }
    private void EndSession() { IsActive = false; IsStopped = false; Status = "Session ended"; foreach (var breakpoint in Breakpoints) breakpoint.Status = "Pending"; stopVersion++; SelectedThread = null; SelectedFrame = null; Threads.Clear(); Frames.Clear(); Locals.Clear(); }
    private void AppendOutput(string value)
    {
        lock (outputGate)
        {
            if (disposed) return;
            const int limit = 250_000;
            if (value.Length >= limit) { pendingOutput.Clear(); pendingOutput.Append(value.AsSpan(value.Length - limit)); }
            else
            {
                int excess = pendingOutput.Length + value.Length - limit;
                if (excess > 0) pendingOutput.Remove(0, excess);
                pendingOutput.Append(value);
            }
            if (outputScheduled) return;
            outputScheduled = true;
        }
        _ = ScheduleOutputAsync();
    }
    private async Task ScheduleOutputAsync()
    {
        // One bounded batch per interval keeps high-volume debug output off the dispatcher queue.
        await Task.Delay(50).ConfigureAwait(false);
        lock (outputGate) { if (disposed) return; }
        dispatcher.Post(() =>
        {
            string batch;
            lock (outputGate)
            {
                outputScheduled = false;
                if (disposed) return;
                batch = pendingOutput.ToString(); pendingOutput.Clear();
            }
            if (batch.Length == 0) return;
            var next = Output + batch;
            Output = next.Length > 250_000 ? next[^200_000..] : next;
        });
    }
    private void OnError(string value) => dispatcher.Post(() => { Error = value; EndSession(); });
    private async Task GuardAsync(Func<Task> action) { try { Error = ""; await action(); } catch (Exception ex) { Error = ex.Message; } }
    private static string Text(JsonElement value, string property, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) ? result.ToString() : fallback;
    private static int Int(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.TryGetInt32(out var number) ? number : 0;
    public async ValueTask DisposeAsync() { lock (outputGate) { disposed = true; pendingOutput.Clear(); } session.EventReceived -= OnDebugEvent; session.Output -= AppendOutput; session.Error -= OnError; await session.DisposeAsync(); }
    private sealed record DebugWorkspaceState(SourceBreakpoint[] Breakpoints, string[] Watches);
}
