using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace WpfStudio.Contracts.Diagnostics;

/// <summary>What is being timed. An area decides how long is long enough to report, and groups the report.</summary>
public static class PerfAreas
{
    /// <summary>How long the UI thread took to reach work it was just given. This is the wait a keystroke sees.</summary>
    public const string UiLatency = "ui wait";
    /// <summary>One input event on the UI thread, from before its handlers to after them.</summary>
    public const string Input = "input";
    /// <summary>One dispatcher operation on the UI thread: layout, rendering, a posted callback or an await resuming.</summary>
    public const string Ui = "ui";
    /// <summary>How long the thread pool took to start work it was just given.</summary>
    public const string PoolLatency = "pool wait";
    /// <summary>A request to the language worker, as the IDE sees it: sent to answered.</summary>
    public const string WorkerCall = "worker call";
    /// <summary>The same request inside the worker: read to answered.</summary>
    public const string Worker = "worker";
    /// <summary>Waiting for something another operation holds.</summary>
    public const string Wait = "wait";
    /// <summary>A request to the preview process, including its wait for the request before it.</summary>
    public const string PreviewCall = "preview call";
    /// <summary>A Code Assist model run.</summary>
    public const string Assist = "assist";
    /// <summary>One part of what a text change sets off inside the key press that made it.</summary>
    public const string Edit = "edit";
    /// <summary>A completion list, from being asked for to being on screen.</summary>
    public const string Completion = "completion";
    public const string Startup = "startup";

    /// <summary>
    /// The duration from which one measurement is reported by itself. Input is judged hardest, because a handler that
    /// takes this long is a keystroke that lags; the others are background work whose answer the person is waiting for.
    /// </summary>
    public static TimeSpan SlowAfter(string area) => TimeSpan.FromMilliseconds(area switch
    {
        Edit => 30,
        Input => 50,
        Ui or Assist => 100,
        UiLatency or PoolLatency => 150,
        Startup => 0,
        _ => 250
    });
}

/// <summary>A measurement long enough to be reported by itself. <paramref name="Source"/> is the process it was taken in.</summary>
public sealed record SlowOperation(DateTimeOffset Time, string Source, string Area, string Name, double Milliseconds, string? Detail = null);

/// <summary>Everything recorded under one name. The median and 95th percentile are upper bounds, a quarter above the true value at most.</summary>
public sealed record PerfStat(string Area, string Name, long Count, double TotalMilliseconds, double MedianMilliseconds, double P95Milliseconds, double MaxMilliseconds);

/// <summary>
/// Records how long named operations take, in the process that owns it. Every measurement is counted, so the report
/// shows what is typical as well as what was slow; a measurement over its area's limit is also raised by itself.
/// Recording takes two clock reads and a few interlocked additions, and nothing is written or allocated for an
/// operation that was fast, so call sites need no switch to turn it off.
/// </summary>
public sealed class PerfMonitor(string source = "shell", Func<string, TimeSpan>? slowAfter = null)
{
    // Names come from code, not from data, so this is a guard against a mistake and not a working limit.
    private const int MaxNames = 1024;
    private readonly ConcurrentDictionary<(string Area, string Name), PerfCounter> _counters = new();
    private readonly Func<string, TimeSpan> _slowAfter = slowAfter ?? PerfAreas.SlowAfter;
    private long _since = Stopwatch.GetTimestamp();

    /// <summary>The process this monitor measures: "shell" or "worker".</summary>
    public string Source { get; } = source;
    /// <summary>Raised on the recording thread, which may be the UI thread: handlers must only queue.</summary>
    public event Action<SlowOperation>? Slow;
    public static long Now => Stopwatch.GetTimestamp();
    /// <summary>How long this monitor has been counting since it was created or last reset.</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(Volatile.Read(ref _since));

    /// <summary>The counter for one name, for a caller that records often enough to keep it.</summary>
    public PerfCounter Counter(string area, string name)
    {
        if (_counters.TryGetValue((area, name), out var counter)) return counter;
        if (_counters.Count >= MaxNames) name = "(other)";
        return _counters.GetOrAdd((area, name), key => new PerfCounter(this, key.Area, key.Name, _slowAfter(key.Area)));
    }

    public void Record(string area, string name, TimeSpan elapsed, string? detail = null) => Counter(area, name).Record(elapsed, detail);
    /// <summary>As <see cref="Record(string, string, TimeSpan, string?)"/>, for detail that costs something to describe: it is asked for only when the measurement is reported.</summary>
    public void Record(string area, string name, TimeSpan elapsed, Func<string?> detail) => Counter(area, name).Record(elapsed, detail);
    /// <summary>Records the time since <paramref name="started"/>, a value of <see cref="Now"/>.</summary>
    public void RecordSince(string area, string name, long started, string? detail = null) => Record(area, name, Stopwatch.GetElapsedTime(started), detail);

    /// <summary>Times a block: <c>using (perf.Measure(area, name)) { … }</c>.</summary>
    public PerfScope Measure(string area, string name) => new(Counter(area, name));

    /// <summary>
    /// Times a call that was started at <paramref name="started"/>. A call that was cancelled or failed is counted
    /// under its own name, so abandoned requests neither hide in the timings of answered ones nor disappear.
    /// </summary>
    public Task<T> TimeAsync<T>(string area, string name, long started, Task<T> call)
    {
        if (call.IsCompletedSuccessfully) { RecordSince(area, name, started); return call; }
        return AwaitAsync(area, name, started, call);
    }
    /// <inheritdoc cref="TimeAsync{T}(string, string, long, Task{T})"/>
    public Task TimeAsync(string area, string name, long started, Task call)
    {
        if (call.IsCompletedSuccessfully) { RecordSince(area, name, started); return call; }
        return AwaitAsync(area, name, started, call);
    }
    private async Task<T> AwaitAsync<T>(string area, string name, long started, Task<T> call)
    {
        try { var result = await call.ConfigureAwait(false); RecordSince(area, name, started); return result; }
        catch (OperationCanceledException) { RecordSince(area, name + " (cancelled)", started); throw; }
        catch { RecordSince(area, name + " (failed)", started); throw; }
    }
    private async Task AwaitAsync(string area, string name, long started, Task call)
    {
        try { await call.ConfigureAwait(false); RecordSince(area, name, started); }
        catch (OperationCanceledException) { RecordSince(area, name + " (cancelled)", started); throw; }
        catch { RecordSince(area, name + " (failed)", started); throw; }
    }

    /// <summary>Reports a measurement taken elsewhere, such as in the worker, without counting it here.</summary>
    public void Publish(SlowOperation operation)
    {
        try { Slow?.Invoke(operation); }
        catch (Exception) { /* Measuring must never fault what it measures. */ }
    }

    /// <summary>What was recorded since the start or the last <see cref="Reset"/>, the largest total first within each area.</summary>
    public IReadOnlyList<PerfStat> Snapshot() => _counters.Values.Select(counter => counter.Read()).Where(stat => stat.Count > 0)
        .OrderBy(stat => stat.Area, StringComparer.Ordinal).ThenByDescending(stat => stat.TotalMilliseconds).ToArray();

    /// <summary>Starts counting again, so a report covers only what is done after it.</summary>
    public void Reset()
    {
        foreach (var counter in _counters.Values) counter.Clear();
        Volatile.Write(ref _since, Stopwatch.GetTimestamp());
    }
}

/// <summary>Counts and times one named operation. Safe from any thread.</summary>
public sealed class PerfCounter
{
    // Durations fall into buckets a quarter of an octave wide, from 64 microseconds to over two minutes. A percentile is
    // then known to within 25% from 86 counters, where keeping the measurements themselves would grow without bound.
    private const int FirstOctave = 6, LastOctave = 26, BucketCount = (LastOctave - FirstOctave + 1) * 4 + 2;
    private readonly long[] _buckets = new long[BucketCount];
    private readonly PerfMonitor _owner;
    private readonly long _slowTicks;
    private long _totalTicks, _maxTicks;

    internal PerfCounter(PerfMonitor owner, string area, string name, TimeSpan slowAfter)
    {
        _owner = owner; Area = area; Name = name;
        _slowTicks = slowAfter.Ticks;
    }
    public string Area { get; }
    public string Name { get; }

    public void Record(TimeSpan elapsed, string? detail = null)
    {
        if (Count(elapsed)) Report(elapsed, detail);
    }
    public void Record(TimeSpan elapsed, Func<string?> detail)
    {
        if (!Count(elapsed)) return;
        string? text;
        try { text = detail(); }
        catch (Exception) { text = null; }
        Report(elapsed, text);
    }

    private bool Count(TimeSpan elapsed)
    {
        long ticks = Math.Max(0, elapsed.Ticks);
        Interlocked.Increment(ref _buckets[Bucket(ticks)]);
        Interlocked.Add(ref _totalTicks, ticks);
        long max;
        while (ticks > (max = Volatile.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, ticks, max) != max) { }
        if (StudioEventSource.Log.IsEnabled()) StudioEventSource.Log.Operation(_owner.Source, Area, Name, elapsed.TotalMilliseconds);
        return ticks >= _slowTicks;
    }

    private void Report(TimeSpan elapsed, string? detail)
    {
        StudioEventSource.Log.SlowOperation(_owner.Source, Area, Name, elapsed.TotalMilliseconds, detail ?? "");
        _owner.Publish(new(DateTimeOffset.Now, _owner.Source, Area, Name, elapsed.TotalMilliseconds, detail));
    }

    private static int Bucket(long ticks)
    {
        long microseconds = ticks / TimeSpan.TicksPerMicrosecond;
        if (microseconds < 1L << FirstOctave) return 0;
        int octave = BitOperations.Log2((ulong)microseconds);
        if (octave > LastOctave) return BucketCount - 1;
        return (octave - FirstOctave) * 4 + (int)((microseconds >> (octave - 2)) & 3) + 1;
    }
    // The longest duration a bucket holds, in milliseconds.
    private static double UpperBound(int bucket)
    {
        if (bucket == 0) return (1L << FirstOctave) / 1000d;
        if (bucket == BucketCount - 1) return double.MaxValue;
        int octave = (bucket - 1) / 4 + FirstOctave, quarter = (bucket - 1) % 4;
        return ((5L + quarter) << (octave - 2)) / 1000d;
    }

    internal PerfStat Read()
    {
        var counts = new long[BucketCount];
        long count = 0;
        for (int index = 0; index < BucketCount; index++) count += counts[index] = Volatile.Read(ref _buckets[index]);
        double max = TimeSpan.FromTicks(Volatile.Read(ref _maxTicks)).TotalMilliseconds;
        return new(Area, Name, count, TimeSpan.FromTicks(Volatile.Read(ref _totalTicks)).TotalMilliseconds, Percentile(counts, count, 0.50, max), Percentile(counts, count, 0.95, max), max);
    }
    private static double Percentile(long[] counts, long count, double fraction, double max)
    {
        long rank = (long)Math.Ceiling(count * fraction), seen = 0;
        for (int index = 0; index < BucketCount; index++)
            if ((seen += counts[index]) >= rank && rank > 0) return Math.Min(max, UpperBound(index));
        return max;
    }

    internal void Clear()
    {
        for (int index = 0; index < BucketCount; index++) Volatile.Write(ref _buckets[index], 0);
        Volatile.Write(ref _totalTicks, 0); Volatile.Write(ref _maxTicks, 0);
    }
}

/// <summary>Records the time from its creation to its disposal.</summary>
public readonly struct PerfScope(PerfCounter? counter) : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    public void Dispose() => counter?.Record(Stopwatch.GetElapsedTime(_started));
}
