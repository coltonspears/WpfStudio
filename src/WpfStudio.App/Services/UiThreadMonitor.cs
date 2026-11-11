using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using WpfStudio.Contracts.Diagnostics;

namespace WpfStudio.App.Services;

/// <summary>
/// Measures what the UI thread spends its time on, without the code it measures knowing. Three things are watched:
/// <list type="bullet">
/// <item>how long the thread takes to reach work it was just given, which is the delay a keystroke meets;</item>
/// <item>every dispatcher operation: layout, rendering, a posted callback, an await resuming on this thread;</item>
/// <item>every input event, from before its handlers run to after them. Typing a character is one of these.</item>
/// </list>
/// Each is recorded under the name of the code that ran, so the report says where the thread's time went and a
/// reported stall says what it was made of. Only names of events and methods are recorded, never a key or any text.
/// </summary>
public sealed class UiThreadMonitor : IDisposable
{
    private const int MaxDepth = 16;
    // Below a millisecond an operation is counted without being named: finding its name would cost more than it took.
    private static readonly TimeSpan Named = TimeSpan.FromMilliseconds(1);
    // WPF does not say what an operation runs. These two fields hold it; without them an operation is still timed, unnamed.
    private static readonly FieldInfo? CallbackField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic),
        ArgumentField = typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly Dispatcher _dispatcher;
    private readonly PerfMonitor _perf;
    private readonly PerfCounter _shortOperations, _shortInput;
    private readonly ResponsivenessProbe _probe;
    private readonly Running[] _running = new Running[MaxDepth];
    private int _depth;
    private InputManager? _inputManager;
    private StagingAreaInputItem? _input;
    private long _inputStarted;
    private bool _inputPumped;
    // What the thread is in at this moment, for the probe's thread to name while this one cannot answer.
    private DispatcherOperation? _currentOperation;
    private string? _currentInput;
    private long _currentStarted;
    // What ran since the probe was last answered. A stall is described by it.
    private TimeSpan _windowBusy, _windowLongest;
    private int _windowCount;
    private object? _windowLongestSource;
    private GcSnapshot _windowCollector;

    private struct Running { public DispatcherOperation? Operation; public long Started; public bool Pumped; }

    public UiThreadMonitor(Dispatcher dispatcher, PerfMonitor perf)
    {
        _dispatcher = dispatcher; _perf = perf;
        _shortOperations = perf.Counter(PerfAreas.Ui, "operations under 1 ms");
        _shortInput = perf.Counter(PerfAreas.Input, "events under 1 ms");
        _probe = new(perf, PerfAreas.UiLatency, "UI thread", Post, Describe, NextWindow);
    }

    /// <summary>Starts watching. Call on the UI thread: the input manager it listens to belongs to the calling thread.</summary>
    public void Start()
    {
        if (!_dispatcher.CheckAccess()) throw new InvalidOperationException("The UI thread monitor must be started on the UI thread.");
        if (_inputManager is not null) return;
        _dispatcher.Hooks.OperationStarted += OperationStarted;
        _dispatcher.Hooks.OperationCompleted += OperationCompleted;
        _inputManager = InputManager.Current;
        _inputManager.PreProcessInput += InputStarted;
        _inputManager.PostProcessInput += InputFinished;
        _windowCollector = GcSnapshot.Take();
        _probe.Start();
    }

    // Loaded is the lowest priority that does not wait for pending input, so the answer comes when everything more urgent
    // has run: the same wait a key press has before WPF starts on it.
    private void Post(Action answer)
    {
        if (_dispatcher.HasShutdownStarted) throw new InvalidOperationException("The UI thread is shutting down.");
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Loaded, answer);
    }

    // These four run inside the dispatcher, for every operation and input event. A fault in one would be raised again by
    // the next, so the first one ends the watching instead: the application goes on unmeasured.
    private void OperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        try { Started(e.Operation); }
        catch (Exception) { Stop(); }
    }
    private void OperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        try { Completed(e.Operation); }
        catch (Exception) { Stop(); }
    }
    private void InputStarted(object sender, PreProcessInputEventArgs e)
    {
        try { Started(e.StagingItem); }
        catch (Exception) { Stop(); }
    }
    private void InputFinished(object sender, ProcessInputEventArgs e)
    {
        try { Finished(e.StagingItem); }
        catch (Exception) { Stop(); }
    }

    private void Started(DispatcherOperation operation)
    {
        // An operation that threw never reports its end. Left here, it would pass for the parent of everything after it.
        while (_depth > 0 && _running[_depth - 1].Operation is not { Status: DispatcherOperationStatus.Executing }) _running[--_depth] = default;
        // An operation that starts inside another means the outer one is running a message loop: a dialog, or a wait.
        if (_depth > 0) _running[_depth - 1].Pumped = true;
        if (_input is not null) _inputPumped = true;
        if (_depth == MaxDepth) return;
        long now = Stopwatch.GetTimestamp();
        _running[_depth++] = new() { Operation = operation, Started = now };
        Volatile.Write(ref _currentStarted, now);
        Volatile.Write(ref _currentOperation, operation);
    }

    private void Completed(DispatcherOperation operation)
    {
        long now = Stopwatch.GetTimestamp();
        int index = _depth - 1;
        while (index >= 0 && !ReferenceEquals(_running[index].Operation, operation)) index--;
        if (index < 0) return;
        var finished = _running[index];
        // Anything above it never reported its end, as an operation that threw does not.
        Array.Clear(_running, index, _depth - index);
        _depth = index;
        Volatile.Write(ref _currentOperation, index > 0 ? _running[index - 1].Operation : null);
        // The time a message loop inside it ran is not the operation's own, and what ran in that loop is counted by itself.
        if (finished.Pumped) return;
        var elapsed = Stopwatch.GetElapsedTime(finished.Started, now);
        if (elapsed < Named) _shortOperations.Record(elapsed);
        else _perf.Record(PerfAreas.Ui, Name(operation), elapsed);
        if (index == 0) Account(elapsed, operation);
    }

    private void Started(StagingAreaInputItem item)
    {
        _input = item;
        _inputPumped = false;
        _inputStarted = Stopwatch.GetTimestamp();
        Volatile.Write(ref _currentStarted, _inputStarted);
        Volatile.Write(ref _currentInput, item.Input.RoutedEvent?.Name);
    }

    private void Finished(StagingAreaInputItem item)
    {
        // A cancelled event never gets here, and an event raised from inside a handler replaces the one it interrupts.
        if (!ReferenceEquals(item, _input)) return;
        _input = null;
        Volatile.Write(ref _currentInput, null);
        if (_inputPumped) return;
        var elapsed = Stopwatch.GetElapsedTime(_inputStarted);
        string name = item.Input.RoutedEvent?.Name ?? "input";
        if (elapsed < Named) _shortInput.Record(elapsed);
        else _perf.Record(PerfAreas.Input, name, elapsed);
        if (_depth == 0) Account(elapsed, name);
    }

    private void Account(TimeSpan elapsed, object source)
    {
        _windowBusy += elapsed;
        _windowCount++;
        if (elapsed > _windowLongest) { _windowLongest = elapsed; _windowLongestSource = source; }
    }

    private void NextWindow()
    {
        _windowBusy = _windowLongest = TimeSpan.Zero;
        _windowCount = 0;
        _windowLongestSource = null;
        _windowCollector = GcSnapshot.Take();
    }

    // Called on this thread once it has answered a probe late, and on the probe's thread while it still has not.
    private string? Describe(bool answered)
    {
        if (!answered)
        {
            string running = Volatile.Read(ref _currentOperation) is { } operation ? Name(operation) : Volatile.Read(ref _currentInput) is { } input ? "input " + input : "";
            return running.Length == 0 ? "not in a dispatcher operation or an input event"
                : $"in {running} for {PerfReport.Duration(Stopwatch.GetElapsedTime(Volatile.Read(ref _currentStarted)).TotalMilliseconds).Trim()}";
        }
        string work = _windowCount == 0 ? "nothing the dispatcher ran accounts for it"
            : $"{_windowCount} operations and input events took {PerfReport.Duration(_windowBusy.TotalMilliseconds).Trim()}, the longest {PerfReport.Duration(_windowLongest.TotalMilliseconds).Trim()}: {Name(_windowLongestSource)}";
        return _windowCollector.Since() is { } collector ? work + " · " + collector : work;
    }

    private static string Name(object? source) => source switch
    {
        DispatcherOperation operation => Name(operation),
        string input => "input " + input,
        _ => "unknown"
    };

    private static string Name(DispatcherOperation operation)
    {
        try
        {
            if (CallbackField?.GetValue(operation) is not Delegate callback) return "dispatcher operation";
            // An await resumes through one callback the runtime shares between all awaits. The continuation it is handed says which method resumes.
            if (ArgumentField?.GetValue(operation) is Delegate { Target: { } state } && PerfNames.AsyncMethod(state.GetType()) is { } resumed) return resumed;
            return PerfNames.Of(callback);
        }
        catch (Exception) { return "dispatcher operation"; }
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        _probe.Dispose();
        // The input manager belongs to the UI thread. From any other thread the probe stops and the handlers stay, which costs a few clock reads.
        if (_inputManager is null || !_dispatcher.CheckAccess()) return;
        _dispatcher.Hooks.OperationStarted -= OperationStarted;
        _dispatcher.Hooks.OperationCompleted -= OperationCompleted;
        _inputManager.PreProcessInput -= InputStarted;
        _inputManager.PostProcessInput -= InputFinished;
        _inputManager = null;
    }
}
