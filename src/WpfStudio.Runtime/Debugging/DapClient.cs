namespace WpfStudio.Runtime.Debugging;

/// <summary>The questions the debugger pane asks an adapter, typed. The session stays the owner of the process and of the commands that change its state.</summary>
public sealed class DapClient(DebugSession session)
{
    public async Task<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
        DapMessages.Threads(await session.RequestAsync("threads", cancellationToken: cancellationToken).ConfigureAwait(false));
    public async Task<IReadOnlyList<DebugFrame>> GetStackTraceAsync(int threadId, int levels, CancellationToken cancellationToken = default) =>
        DapMessages.StackFrames(await session.RequestAsync("stackTrace", new { threadId, startFrame = 0, levels }, cancellationToken).ConfigureAwait(false));
    public async Task<IReadOnlyList<DapScope>> GetScopesAsync(int frameId, CancellationToken cancellationToken = default) =>
        DapMessages.Scopes(await session.RequestAsync("scopes", new { frameId }, cancellationToken).ConfigureAwait(false));
    public async Task<IReadOnlyList<DapVariable>> GetVariablesAsync(int variablesReference, int start, int count, CancellationToken cancellationToken = default) =>
        DapMessages.Variables(await session.RequestAsync("variables", new { variablesReference, start, count }, cancellationToken).ConfigureAwait(false));
    public async Task<DapEvaluation> EvaluateAsync(string expression, int frameId, string context, CancellationToken cancellationToken = default) =>
        DapMessages.Evaluation(await session.RequestAsync("evaluate", new { expression, frameId, context }, cancellationToken).ConfigureAwait(false));
    public async Task<DapExceptionInfo> GetExceptionInfoAsync(int threadId, CancellationToken cancellationToken = default) =>
        DapMessages.ExceptionInfo(await session.RequestAsync("exceptionInfo", new { threadId }, cancellationToken).ConfigureAwait(false));
    public async Task<IReadOnlyList<DapBreakpoint>> SetBreakpointsAsync(string path, IEnumerable<SourceBreakpoint> breakpoints, CancellationToken cancellationToken = default) =>
        DapMessages.Breakpoints(await session.SetBreakpointsAsync(path, breakpoints, cancellationToken).ConfigureAwait(false));
}
