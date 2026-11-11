using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text;

namespace WpfStudio.Contracts.Diagnostics;

/// <summary>Names a piece of code the way its author would, from what the compiler made of it.</summary>
public static class PerfNames
{
    /// <summary>
    /// The method a delegate runs, as <c>Type.Method</c>. A lambda or local function is named after the method that
    /// contains it, which is the name that can be searched for; the compiler's own name for it changes with every edit.
    /// A delegate that resumes an await is named after the async method it resumes.
    /// </summary>
    public static string Of(Delegate callback) => callback.Target is { } target && AsyncMethod(target.GetType()) is { } resumed ? resumed : Of(callback.Method);

    public static string Of(MethodBase method) => Join(Owner(method.DeclaringType), Written(method.Name));

    /// <summary>
    /// The async method that resumes when <paramref name="continuation"/> runs, or null when it is not an await's
    /// continuation. The runtime keeps a suspended async method in a box that is generic over the method's state
    /// machine, and the continuation is that box's own method, so the box's type names the method.
    /// </summary>
    public static string? AsyncMethod(Type continuation)
    {
        if (!continuation.IsGenericType) return null;
        var machine = continuation.GetGenericArguments()[^1];
        if (!typeof(IAsyncStateMachine).IsAssignableFrom(machine)) return null;
        var (name, kind) = Written(machine.Name);
        return Join(Owner(machine.DeclaringType), (name, kind is null ? "resumed" : kind + ", resumed"));
    }

    private static string Join(string type, (string Name, string? Kind) method) =>
        (type.Length == 0 ? method.Name : type + "." + method.Name) + (method.Kind is null ? "" : " (" + method.Kind + ")");

    // The compiler names what it generates after the method it came from: <Method>b__0 is a lambda in Method,
    // <Method>g__Local|0_0 a local function, <Method>d__5 an async method's state machine, and these nest, so the state
    // machine of an async lambda is <<Method>b__0>d.
    private static (string Name, string? Kind) Written(string generated)
    {
        if (!generated.StartsWith('<')) return (generated, null);
        int depth = 0, close = -1;
        for (int index = 0; index < generated.Length && close < 0; index++)
            if (generated[index] == '<') depth++;
            else if (generated[index] == '>' && --depth == 0) close = index;
        if (close < 2) return (generated, null);
        var (name, kind) = Written(generated[1..close]);
        string rest = generated[(close + 1)..];
        if (rest.StartsWith("g__", StringComparison.Ordinal))
        {
            int bar = rest.IndexOf('|');
            return (name + "." + (bar > 3 ? rest[3..bar] : rest[3..]), kind);
        }
        return (name, rest.StartsWith("b__", StringComparison.Ordinal) ? "lambda" : kind);
    }

    // The nearest type a person wrote: closures and state machines are nested in it under names that start with '<'.
    private static string Owner(Type? type)
    {
        while (type is not null && type.Name.StartsWith('<')) type = type.DeclaringType;
        if (type is null) return "";
        string name = type.Name;
        int arity = name.IndexOf('`');
        return arity > 0 ? name[..arity] : name;
    }
}

/// <summary>Lays out what a <see cref="PerfMonitor"/> recorded as text that reads in a log and pastes into a message.</summary>
public static class PerfReport
{
    public static string Format(PerfMonitor perf, string title) => Format(title, perf.Elapsed, perf.Snapshot());

    public static string Format(string title, TimeSpan period, IReadOnlyList<PerfStat> stats)
    {
        var text = new StringBuilder();
        text.Append(title).Append(", last ").Append(Duration(period.TotalMilliseconds).Trim()).Append(" · ").AppendLine(Runtime());
        if (stats.Count == 0) return text.AppendLine("  nothing recorded").ToString();
        int area = Math.Max(4, stats.Max(stat => stat.Area.Length));
        text.Append("  ").Append("area".PadRight(area)).AppendLine("    count     total       p50       p95       max  name");
        foreach (var stat in stats)
            text.Append("  ").Append(stat.Area.PadRight(area)).Append(stat.Count.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append(Duration(stat.TotalMilliseconds)).Append(Duration(stat.MedianMilliseconds)).Append(Duration(stat.P95Milliseconds)).Append(Duration(stat.MaxMilliseconds))
                .Append("  ").AppendLine(stat.Name);
        return text.ToString();
    }

    /// <summary>One slow operation as a log line, without its time.</summary>
    public static string Line(SlowOperation operation) =>
        $"{operation.Source,-6} {operation.Area,-12} {Duration(operation.Milliseconds)}  {operation.Name}{(string.IsNullOrEmpty(operation.Detail) ? "" : " · " + operation.Detail)}";

    /// <summary>Ten characters wide, so columns of durations line up.</summary>
    public static string Duration(double milliseconds) => (milliseconds switch
    {
        >= 60_000 => (milliseconds / 60_000).ToString("0.0", CultureInfo.InvariantCulture) + " min",
        >= 1000 => (milliseconds / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " s",
        >= 10 => milliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms",
        _ => milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms"
    }).PadLeft(10);

    // What the collector cost the whole process, and how it is set up: a blocking collection stops every thread at once,
    // which no single operation's timing can show.
    private static string Runtime()
    {
        var memory = GC.GetGCMemoryInfo();
        return string.Create(CultureInfo.InvariantCulture, $"GC: {(GCSettings.IsServerGC ? "server" : "workstation")}, {(GCSettings.LatencyMode == GCLatencyMode.Batch ? "blocking" : "concurrent")}; paused {Duration(GC.GetTotalPauseDuration().TotalMilliseconds).Trim()} in all ({memory.PauseTimePercentage:0.#}% of the time); {GC.CollectionCount(2)} gen2 of {GC.CollectionCount(0)} collections; heap {memory.HeapSizeBytes / (1024d * 1024):0} MB; {Environment.ProcessorCount} cores");
    }
}
