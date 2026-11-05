using System.Diagnostics;
using System.Globalization;
using System.Xml;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

/// <summary>A render's diagnostics and WPF's binding trace. Every access takes a lock, because WPF writes the trace from wherever a binding is evaluated.</summary>
internal sealed class PreviewDiagnosticLog : IDisposable
{
    private readonly List<PreviewDiagnostic> _diagnostics = [];
    private readonly List<PreviewDiagnostic> _traceDiagnostics = [];
    private BindingTraceListener? _trace;
    private TraceSource? _bindingTraceSource;
    private SourceLevels _previousTraceLevel;

    public void StartBindingTrace()
    {
        _trace = new BindingTraceListener(message =>
        {
            lock (_traceDiagnostics)
                if (_traceDiagnostics.Count < 200)
                    _traceDiagnostics.Add(new PreviewDiagnostic("Historical WPF binding trace: " + message, "Warning"));
        });
        _bindingTraceSource = PresentationTraceSources.DataBindingSource;
        _previousTraceLevel = _bindingTraceSource.Switch.Level;
        _bindingTraceSource.Switch.Level = SourceLevels.Information;
        _bindingTraceSource.Listeners.Add(_trace);
    }

    public void Add(PreviewDiagnostic diagnostic) { lock (_diagnostics) _diagnostics.Add(diagnostic); }
    public void AddRange(IEnumerable<PreviewDiagnostic> diagnostics) { lock (_diagnostics) _diagnostics.AddRange(diagnostics); }
    public void Clear() { lock (_diagnostics) _diagnostics.Clear(); }
    public void ClearTrace() { lock (_traceDiagnostics) _traceDiagnostics.Clear(); }

    public IReadOnlyList<PreviewDiagnostic> Snapshot()
    {
        lock (_diagnostics)
        lock (_traceDiagnostics)
            return _diagnostics.Concat(_traceDiagnostics).Distinct().ToArray();
    }

    public void Record(Exception exception, bool sourceCoordinates)
    {
        int? line = sourceCoordinates && exception is XmlException xml ? xml.LineNumber : null;
        int? column = sourceCoordinates && exception is XmlException xmlError ? xmlError.LinePosition : null;
        // XamlReader positions refer to transformed markup, so never present them
        // as precise positions in the original source buffer.
        var cause = exception.GetBaseException();
        Add(new PreviewDiagnostic(cause.Message + Origin(cause), "Error", line, column));
    }

    /// <summary>A runtime mismatch names the member that is missing, which says nothing about whose code wanted it.</summary>
    private static string Origin(Exception exception)
    {
        if (exception is not (MissingMemberException or TypeLoadException)) return "";
        var type = new StackTrace(exception, false).GetFrames()?.Select(frame => frame.GetMethod()?.DeclaringType).FirstOrDefault(declaring => declaring is not null);
        return type is null ? "" : $" Called from {type.FullName} in {type.Assembly.GetName().Name}.";
    }

    public void Dispose()
    {
        if (_bindingTraceSource is null || _trace is null) return;
        _bindingTraceSource.Listeners.Remove(_trace);
        _bindingTraceSource.Switch.Level = _previousTraceLevel;
        _trace.Dispose();
    }

    private sealed class BindingTraceListener(Action<string> report) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) report(message!); }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (Keep(eventType, id) && !string.IsNullOrWhiteSpace(message)) report(Limit(message!, 4096));
        }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (!Keep(eventType, id) || string.IsNullOrWhiteSpace(format)) return;
            // Composite formatting may execute arbitrary application ToString or
            // IFormattable implementations. Preserve bounded text and safe facts.
            var text = Limit(format!, 3072);
            if (args is { Length: > 0 }) text += " | " + string.Join(", ", args.Take(16).Select(Describe));
            report(Limit(text, 4096));
        }

        private static bool Keep(TraceEventType type, int id) =>
            (type & (TraceEventType.Critical | TraceEventType.Error | TraceEventType.Warning)) != 0 || id is 40 or 41 or 42;
        private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
        private static string Describe(object? value) => value switch
        {
            null => "null",
            string text => Limit(text, 512),
            bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            _ => "(" + Limit(value.GetType().FullName ?? value.GetType().Name, 512) + ")"
        };
    }
}
