using System.Diagnostics.Tracing;

namespace WpfStudio.Contracts.Diagnostics;

/// <summary>
/// The same measurements as events, for a tool that records the process from outside: <c>dotnet-trace collect
/// --providers WpfStudio</c>, or PerfView. There they sit on one timeline with CPU samples and garbage collections,
/// which says what the code was doing during a slow operation and not only that it was slow. An event is written at
/// the end of the operation it describes, so the operation covers the given duration before the event's time.
/// </summary>
[EventSource(Name = "WpfStudio")]
public sealed class StudioEventSource : EventSource
{
    public static readonly StudioEventSource Log = new();
    private StudioEventSource() { }

    /// <summary>Every measurement. Written only while a listener asks for this level.</summary>
    [Event(1, Level = EventLevel.Verbose)]
    public void Operation(string source, string area, string name, double milliseconds)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.All)) WriteEvent(1, source, area, name, milliseconds);
    }

    /// <summary>A measurement over its area's limit.</summary>
    [Event(2, Level = EventLevel.Warning)]
    public void SlowOperation(string source, string area, string name, double milliseconds, string detail)
    {
        if (IsEnabled()) WriteEvent(2, source, area, name, milliseconds, detail);
    }
}
