using System.Diagnostics;
using System.Globalization;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Inspection.Agent;

internal sealed class BindingTraceBuffer : TraceListener
{
    private const int Capacity = 200;
    private readonly object _gate = new();
    private readonly List<InspectionTrace> _messages = [];
    private readonly TraceSource _source;
    private readonly SourceLevels _previousLevel;
    private readonly SourceLevels _enabledLevel;
    private long _nextId;
    private bool _disposed;
    public BindingEvidenceCollector Evidence { get; }

    public BindingTraceBuffer()
    {
        // Explicit refresh enables WPF's diagnostic tracing without requiring a
        // debugger. No application-level exception/validation handler is added.
        _source = PresentationTraceSources.DataBindingSource;
        _previousLevel = _source.Switch.Level;
        Evidence = new BindingEvidenceCollector(Wpf.PropertyEditing.TemporaryPropertyEdits.IsOverrideBinding);
        // WPF reports null path items at Information rather than Warning/Error.
        _enabledLevel = _previousLevel | SourceLevels.Information;
        try
        {
            PresentationTraceSources.Refresh();
            _source.Switch.Level = _enabledLevel;
            _source.Listeners.Add(this);
        }
        catch
        {
            Evidence.Dispose();
            _source.Listeners.Remove(this);
            if (_source.Switch.Level == _enabledLevel) _source.Switch.Level = _previousLevel;
            throw;
        }
    }

    public override bool IsThreadSafe => true;
    public override void Write(string? message) => Add(message);
    public override void WriteLine(string? message) => Add(message);
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (KeepTrace(eventType, id)) Add(message);
    }
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        if (!KeepTrace(eventType, id)) return;
        try
        {
            // Formatting an arbitrary argument can execute its ToString. Keep the
            // original bounded diagnostic text and append only safe descriptions;
            // do not honor user-provided composite-format widths or precision.
            string message = RunningInspector.Limit(format ?? "", 3072);
            if (args is { Length: > 0 }) message += " | " + string.Join(", ", args.Take(16).Select(DescribeArgument));
            Add(message);
        }
        catch (Exception) { Add(format); }
    }

    private static bool KeepTrace(TraceEventType type, int id) =>
        (type & (TraceEventType.Critical | TraceEventType.Error | TraceEventType.Warning)) != 0 || id is 40 or 41 or 42;

    private static string DescribeArgument(object? value)
    {
        if (value is null) return "null";
        if (value is string text) return RunningInspector.Limit(text, 512);
        Type type = value.GetType();
        return type.IsPrimitive || type.IsEnum || value is decimal
            ? RunningInspector.Limit(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", 512)
            : "(" + RunningInspector.TypeName(type) + ")";
    }

    private void Add(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        message = RunningInspector.Limit(message, 4096);
        lock (_gate)
        {
            if (_disposed) return;
            int existing = _messages.FindIndex(item => item.Message == message);
            if (existing >= 0)
            {
                var item = _messages[existing];
                _messages[existing] = item with { Count = item.Count == int.MaxValue ? item.Count : item.Count + 1 };
                return;
            }
            if (_messages.Count == Capacity) _messages.RemoveAt(0);
            _messages.Add(new(Interlocked.Increment(ref _nextId), DateTimeOffset.UtcNow, message, 1));
        }
    }

    public IReadOnlyList<InspectionTrace> Snapshot()
    {
        lock (_gate) return _messages.ToArray();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _messages.Clear();
        }
        _source.Listeners.Remove(this);
        Evidence.Dispose();
        // Preserve a subsequent application change to the diagnostic switch.
        if (_source.Switch.Level == _enabledLevel) _source.Switch.Level = _previousLevel;
        base.Dispose(disposing);
    }
}
