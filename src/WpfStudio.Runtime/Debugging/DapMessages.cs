using System.Text.Json;

namespace WpfStudio.Runtime.Debugging;

public sealed record DebugThread(int Id, string Name);
public sealed record DebugFrame(int Id, string Name, string? Path, int Line);
public sealed record DapScope(string Name, int VariablesReference);
public sealed record DapVariable(string Name, string Value, string Type, int VariablesReference);
public sealed record DapEvaluation(string Result, string Type, int VariablesReference);
public sealed record DapExceptionInfo(string ExceptionId, string Description, string? Details);
/// <param name="SourcePath">Null when the adapter sent no source at all, and empty when it sent one without a path.</param>
public sealed record DapBreakpoint(int? Id, bool Verified, string? Message, int Line, string? SourcePath);
public sealed record DapStopped(string? Reason, int ThreadId, IReadOnlySet<int> HitBreakpointIds);
/// <summary>A loaded assembly. <paramref name="SymbolStatus"/> is the adapter's own wording, such as "Symbols loaded."; empty when it sent none.</summary>
public sealed record DebugModule(string Id, string Name, string Path, string SymbolStatus)
{
    public bool HasSymbols => SymbolStatus.StartsWith("Symbols loaded", StringComparison.OrdinalIgnoreCase);
    public string Symbols => SymbolStatus.Length == 0 ? "" : SymbolStatus.TrimEnd('.');
}
/// <param name="Reason"><c>new</c>, <c>changed</c> or <c>removed</c>.</param>
public sealed record DapModuleEvent(string Reason, DebugModule Module);

/// <summary>
/// Reads adapter JSON. A response may omit its body and most fields, and an answer that cannot be read is not worth ending a session over,
/// so an absent or mistyped field reads as empty and a list entry that is not an object is skipped. A <c>default</c> element is a missing body.
/// </summary>
public static class DapMessages
{
    public static IReadOnlyList<DebugThread> Threads(JsonElement body) => Objects(body, "threads", item => new DebugThread(Int(item, "id"), Text(item, "name")));
    public static IReadOnlyList<DebugFrame> StackFrames(JsonElement body) => Objects(body, "stackFrames", item => new DebugFrame(Int(item, "id"), Text(item, "name"), SourcePath(item), Int(item, "line")));
    public static IReadOnlyList<DapScope> Scopes(JsonElement body) => Objects(body, "scopes", item => new DapScope(Text(item, "name"), Int(item, "variablesReference")));
    public static IReadOnlyList<DapVariable> Variables(JsonElement body) => Objects(body, "variables", item => new DapVariable(Text(item, "name"), Text(item, "value"), Text(item, "type"), Int(item, "variablesReference")));
    public static DapEvaluation Evaluation(JsonElement body) => new(Text(body, "result"), Text(body, "type"), Int(body, "variablesReference"));
    public static DapExceptionInfo ExceptionInfo(JsonElement body) => new(Text(body, "exceptionId"), Text(body, "description"), Member(body, "details") is { } details ? details.ToString() : null);

    /// <summary>The adapter answers setBreakpoints by position, so every entry keeps its place even when it cannot be read.</summary>
    public static IReadOnlyList<DapBreakpoint> Breakpoints(JsonElement body) =>
        Member(body, "breakpoints") is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray().Select(Breakpoint).ToArray() : [];

    /// <summary>The body of a <c>breakpoint</c> event, or null when it does not carry one.</summary>
    public static DapBreakpoint? ChangedBreakpoint(JsonElement body) => Member(body, "breakpoint") is { ValueKind: JsonValueKind.Object } changed ? Breakpoint(changed) : null;

    public static DapStopped Stopped(JsonElement body) => new(OptionalText(body, "reason"), Int(body, "threadId"),
        Member(body, "hitBreakpointIds") is { ValueKind: JsonValueKind.Array } ids ? ids.EnumerateArray().Select(AsInt).OfType<int>().ToHashSet() : new HashSet<int>());

    /// <summary>The body of a <c>module</c> event, or null when it names no module. netcoredbg sends string ids; the protocol also allows numbers.</summary>
    public static DapModuleEvent? Module(JsonElement body) =>
        Member(body, "module") is { ValueKind: JsonValueKind.Object } module && Member(module, "id") is { ValueKind: JsonValueKind.String or JsonValueKind.Number } id
            ? new(Text(body, "reason"), new DebugModule(id.ToString(), Text(module, "name"), Text(module, "path"), Text(module, "symbolStatus"))) : null;

    private static DapBreakpoint Breakpoint(JsonElement item) => new(Member(item, "id") is { } id ? AsInt(id) : null, Member(item, "verified")?.ValueKind == JsonValueKind.True, OptionalText(item, "message"), Int(item, "line"), SourcePath(item));
    private static string? SourcePath(JsonElement item) => Member(item, "source") is { } source ? Text(source, "path") : null;
    private static IReadOnlyList<T> Objects<T>(JsonElement body, string property, Func<JsonElement, T> read) =>
        Member(body, property) is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(read).ToArray() : [];
    private static JsonElement? Member(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var member) ? member : null;
    private static string Text(JsonElement value, string property) => OptionalText(value, property) ?? "";
    private static string? OptionalText(JsonElement value, string property) => Member(value, property)?.ToString();
    private static int Int(JsonElement value, string property) => Member(value, property) is { } member ? AsInt(member) ?? 0 : 0;
    private static int? AsInt(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
