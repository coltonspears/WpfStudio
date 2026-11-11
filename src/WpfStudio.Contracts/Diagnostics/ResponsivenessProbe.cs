using System.Diagnostics;

namespace WpfStudio.Contracts.Diagnostics;

/// <summary>
/// Measures how long something that runs queued work takes to reach work it was just given: a UI thread or the thread
/// pool. From its own thread it hands over a callback every <paramref name="interval"/> and times the wait until the
/// callback runs, so it sees a busy target whatever keeps it busy, and it keeps watching while the target is stuck:
/// a target that has not answered for <paramref name="silentAfter"/> is reported once, while it is still silent, so a
/// hang that ends with the process being closed leaves a line behind.
/// </summary>
/// <param name="post">Queues the callback to the target. Called from the probe's thread.</param>
/// <param name="describe">
/// What the target was doing, asked for only when a wait is reported. Its argument says whether the target has answered: if
/// so the call is on the target, and otherwise on the probe's thread, while the target is still busy with something.
/// </param>
/// <param name="answered">Runs on the target after every answer, reported or not, for a caller that describes what happened between two of them.</param>
public sealed class ResponsivenessProbe(PerfMonitor perf, string area, string name, Action<Action> post, Func<bool, string?>? describe = null,
    Action? answered = null, TimeSpan? interval = null, TimeSpan? silentAfter = null) : IDisposable
{
    // A wake this much later than asked means the process was suspended or the machine slept. A wait measured across
    // that says nothing about the target.
    private static readonly TimeSpan SuspendedAfter = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMilliseconds(100), _silentAfter = silentAfter ?? TimeSpan.FromSeconds(3);
    private readonly PerfCounter _counter = perf.Counter(area, name);
    private readonly ManualResetEventSlim _stop = new(false);
    private Func<string?>? _describeAnswer;
    private Thread? _thread;
    private long _posted, _woke;
    private int _pending, _discard, _disposed;

    /// <summary>
    /// A probe of this process's thread pool. Its waits say how long a continuation or a queued request stood before a
    /// thread took it, which a starved pool adds to everything without making any one operation slow to compute.
    /// </summary>
    public static ResponsivenessProbe ForThreadPool(PerfMonitor perf) =>
        new(perf, PerfAreas.PoolLatency, "thread pool", static answer => ThreadPool.UnsafeQueueUserWorkItem(static callback => callback(), answer, preferLocal: false),
            static _ => $"{ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} items queued");

    public void Start()
    {
        if (_thread is not null || Volatile.Read(ref _disposed) != 0) return;
        _thread = new Thread(Watch) { IsBackground = true, Name = "WpfStudio responsiveness: " + name };
        _thread.Start();
    }

    private void Watch()
    {
        Volatile.Write(ref _woke, Stopwatch.GetTimestamp());
        bool silenceReported = false;
        try
        {
            while (!_stop.Wait(_interval))
            {
                long now = Stopwatch.GetTimestamp();
                bool suspended = Suspended(now);
                Volatile.Write(ref _woke, now);
                if (Volatile.Read(ref _pending) == 0)
                {
                    silenceReported = false;
                    Volatile.Write(ref _discard, 0);
                    Volatile.Write(ref _posted, now);
                    Volatile.Write(ref _pending, 1);
                    post(Answer);
                }
                else if (suspended) Volatile.Write(ref _discard, 1);
                else if (!silenceReported && Volatile.Read(ref _discard) == 0 && Stopwatch.GetElapsedTime(Volatile.Read(ref _posted), now) is var silent && silent >= _silentAfter)
                {
                    silenceReported = true;
                    perf.Publish(new(DateTimeOffset.Now, perf.Source, area, name + " (no answer yet)", silent.TotalMilliseconds, Describe(answered: false)));
                }
            }
        }
        // The target is gone, as a dispatcher is once its thread has shut down. There is nothing left to measure.
        catch (Exception) { }
    }

    // This thread wakes every interval whatever the target does, so a longer gap means nothing in the process ran.
    private bool Suspended(long now) => Stopwatch.GetElapsedTime(Volatile.Read(ref _woke), now) > _interval + SuspendedAfter;

    private void Answer()
    {
        long now = Stopwatch.GetTimestamp();
        var waited = Stopwatch.GetElapsedTime(Volatile.Read(ref _posted), now);
        // After a sleep the target can answer before the probe's own thread has woken to see that there was one.
        bool discard = Volatile.Read(ref _discard) != 0 || Suspended(now);
        try
        {
            if (!discard && Volatile.Read(ref _disposed) == 0) _counter.Record(waited, _describeAnswer ??= () => Describe(answered: true));
            answered?.Invoke();
        }
        catch (Exception) { /* Measuring must never fault what it measures. */ }
        finally { Volatile.Write(ref _pending, 0); }
    }

    private string? Describe(bool answered)
    {
        try { return describe?.Invoke(answered); }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        // Not joined: the thread is a background thread that ends at its next wake, and a disposing UI thread must not wait for it.
    }
}

/// <summary>The garbage collector's counters at one moment, to say what it cost between two of them.</summary>
public readonly record struct GcSnapshot(int Gen0, int Gen1, int Gen2, TimeSpan Paused)
{
    public static GcSnapshot Take() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());

    /// <summary>Collections and pause time since this snapshot, or null when the collector did not run.</summary>
    public string? Since()
    {
        var now = Take();
        // CollectionCount(0) counts every collection, CollectionCount(1) those of generation 1 and above.
        int gen2 = now.Gen2 - Gen2, gen1 = now.Gen1 - Gen1 - gen2, gen0 = now.Gen0 - Gen0 - gen1 - gen2;
        if (gen0 + gen1 + gen2 <= 0) return null;
        var parts = new List<string>(3);
        if (gen2 > 0) parts.Add($"{gen2} gen2");
        if (gen1 > 0) parts.Add($"{gen1} gen1");
        if (gen0 > 0) parts.Add($"{gen0} gen0");
        return $"GC paused {(now.Paused - Paused).TotalMilliseconds:0} ms ({string.Join(", ", parts)})";
    }
}
