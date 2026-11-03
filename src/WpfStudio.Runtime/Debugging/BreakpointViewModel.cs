using CommunityToolkit.Mvvm.ComponentModel;

namespace WpfStudio.Runtime.Debugging;

public sealed partial class BreakpointViewModel : ObservableObject
{
    public required string Path { get; init; }
    public required int Line { get; init; }
    [ObservableProperty] public partial string Condition { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial string Status { get; set; } = "Pending";
    /// <summary>Breaks by hit count, for example 5, &gt;=5 or %5. Empty breaks on every hit.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] public partial string HitCondition { get; set; } = "";
    /// <summary>A tracepoint message. When set, a hit writes the message to Output and execution continues.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary), nameof(IsTracepoint))] public partial string LogMessage { get; set; } = "";
    /// <summary>Hits counted in the current session, after any condition was true.</summary>
    [ObservableProperty] public partial int HitCount { get; set; }
    /// <summary>The adapter's identifier and resolved line; a breakpoint can bind to the next statement.</summary>
    public int? AdapterId { get; set; }
    public int? BoundLine { get; set; }
    public bool IsTracepoint => LogMessage.Length > 0;
    public bool HasClientBehavior => HitCondition.Length > 0 || LogMessage.Length > 0;
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HitCondition.Length > 0) parts.Add(WpfStudio.Contracts.HitCondition.TryParse(HitCondition, out var hit, out _) ? "Breaks " + hit.Describe() : "Invalid hit count");
            if (LogMessage.Length > 0) parts.Add("Logs: " + LogMessage);
            return string.Join(" · ", parts);
        }
    }
    /// <summary>The adapter's answer to setBreakpoints.</summary>
    public void Apply(DapBreakpoint answer)
    {
        Status = answer.Verified ? "Bound" : answer.Message ?? "Unbound · symbols not loaded";
        AdapterId = answer.Id;
        BoundLine = answer.Line is > 0 and var bound ? bound : null;
    }
    /// <summary>A later <c>breakpoint</c> event, which only says what changed.</summary>
    public void ApplyChange(DapBreakpoint changed)
    {
        Status = changed.Verified ? "Bound" : changed.Message ?? "Unbound";
        if (changed.Line > 0) BoundLine = changed.Line;
    }
    public SourceBreakpoint ToModel() => new(Path, Line, Condition, Enabled, HitCondition.Length == 0 ? null : HitCondition, LogMessage.Length == 0 ? null : LogMessage);
}
