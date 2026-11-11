using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WpfStudio.Contracts.Diagnostics;
using WpfStudio.Core.IO;
using WpfStudio.Workspace;

namespace WpfStudio.App.Services;

/// <summary>
/// Keeps what the performance monitors find in <c>Logs/performance.log</c>: each slow operation as a line when it
/// happens, from this process and from the language worker, and on request a table of everything measured. The file is
/// what to send with a report that the IDE felt slow; it holds names of methods and events and durations, and no
/// source text, file names or keys.
/// </summary>
public sealed class PerfLog : IDisposable
{
    // A burst writes this many lines and then a count of the rest. One slow thing tends to make many things slow at once.
    private const int LinesPerWindow = 60;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10), NoticeEvery = TimeSpan.FromSeconds(10);
    // A stall this long is felt as one, and is worth a line where the person is looking.
    private const double NoticeFromMilliseconds = 500;
    private readonly PerfMonitor _perf;
    private readonly QueuedLogFile _file;
    private readonly object _gate = new();
    private long _windowStarted = Stopwatch.GetTimestamp(), _noticed;
    private int _written, _dropped;
    private bool _headed, _disposed;

    public PerfLog(PerfMonitor perf, string directory)
    {
        _perf = perf;
        _file = new(directory, "performance.log");
        _perf.Slow += Write;
    }

    public string Path => _file.Path;
    /// <summary>The monitor whose findings this log keeps, for code that records into it.</summary>
    public PerfMonitor Monitor => _perf;
    /// <summary>The UI thread did not respond for long enough to be felt. Raised on whichever thread measured it, at most once every ten seconds.</summary>
    public event Action<string>? Noticed;

    // Runs on the thread that measured, which may be the UI thread: it formats one line and queues it.
    private void Write(SlowOperation operation)
    {
        string? notice = null;
        lock (_gate)
        {
            if (_disposed) return;
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_windowStarted, now) > Window)
            {
                if (_dropped > 0) Append($"{Stamp(DateTimeOffset.Now)} … and {_dropped} more slow operations in those {Window.TotalSeconds:0} seconds{Environment.NewLine}");
                _windowStarted = now; _written = _dropped = 0;
            }
            if (_written >= LinesPerWindow) { _dropped++; return; }
            _written++;
            Append($"{Stamp(operation.Time)} {PerfReport.Line(operation)}{Environment.NewLine}");
            if (operation is { Area: PerfAreas.UiLatency, Milliseconds: >= NoticeFromMilliseconds } && (_noticed == 0 || Stopwatch.GetElapsedTime(_noticed, now) > NoticeEvery))
            {
                _noticed = now;
                notice = $"The UI did not respond for {PerfReport.Duration(operation.Milliseconds).Trim()}{(string.IsNullOrEmpty(operation.Detail) ? "" : ": " + operation.Detail)}. Help > Performance report shows where the time goes.";
            }
        }
        if (notice is not null) Noticed?.Invoke(notice);
    }

    /// <summary>
    /// Everything measured since the last report, here and in the language worker, as text. It is also written to the
    /// log. <paramref name="reset"/> starts the counting again, so the next report covers only what is done after this one.
    /// </summary>
    public async Task<string> ReportAsync(WorkspaceClient? workspace, bool reset, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder(PerfReport.Format(_perf, "IDE"));
        if (reset) _perf.Reset();
        if (workspace is not null && await workspace.GetWorkerPerformanceReportAsync(reset, cancellationToken) is { } worker) text.AppendLine().Append(worker);
        string report = text.ToString();
        WriteBlock(report);
        return report;
    }

    /// <summary>Writes this process's measurements to the log, as when the application closes.</summary>
    public void WriteReport() => WriteBlock(PerfReport.Format(_perf, "IDE"));

    private void WriteBlock(string report)
    {
        lock (_gate) { if (!_disposed) Append($"{Stamp(DateTimeOffset.Now)} report{Environment.NewLine}{report}"); }
    }

    // Called with the gate held, so the header is first and lines keep their order.
    private void Append(string text)
    {
        if (!_headed)
        {
            _headed = true;
            string version = typeof(PerfLog).Assembly.GetName().Version?.ToString(3) ?? "";
            _file.Write($"{Environment.NewLine}==== WpfStudio {version} · process {Environment.ProcessId} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {Environment.ProcessorCount} cores ===={Environment.NewLine}");
        }
        _file.Write(text);
    }

    private static string Stamp(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _perf.Slow -= Write;
        _file.Dispose();
    }
}
