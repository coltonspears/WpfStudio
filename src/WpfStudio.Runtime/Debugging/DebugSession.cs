using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WpfStudio.Runtime.Debugging;

public sealed record DebugLaunchConfiguration(string Program, string WorkingDirectory, IReadOnlyList<string>? Arguments = null, IReadOnlyDictionary<string, string>? Environment = null);
public sealed record SourceBreakpoint(string Path, int Line, string? Condition = null, bool Enabled = true);
public sealed record DebugEvent(string Name, JsonElement Body);

/// <summary>Owns one managed debug session; the adapter never runs inside the IDE process.</summary>
public sealed class DebugSession : IAsyncDisposable
{
    private Process? process;
    private DapTransport? transport;
    private Task? stderr;
    private TaskCompletionSource initialized = NewCompletion();
    private bool disposing;
    private bool ownsDebuggee;
    public string AdapterPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "debugger", "netcoredbg.exe");
    public bool IsActive => transport is not null;
    public int? AdapterProcessId { get { try { return process is { HasExited: false } adapter ? adapter.Id : null; } catch (InvalidOperationException) { return null; } } }
    public JsonElement Capabilities { get; private set; }
    public event Action<DebugEvent>? EventReceived;
    public event Action<string>? Output;
    public event Action<string>? Error;
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (IsActive) throw new InvalidOperationException("Stop the active debugging session first.");
        if (!File.Exists(AdapterPath)) throw new FileNotFoundException("netcoredbg is missing. Run tools/Get-Debugger.ps1 and rebuild WpfStudio.", AdapterPath);
        disposing = false;
        initialized = NewCompletion();
        process = new Process { StartInfo = new ProcessStartInfo(AdapterPath, "--interpreter=vscode") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(AdapterPath)! } };
        process.Start();
        var currentProcess = process;
        stderr = Task.Run(async () => { while (await currentProcess.StandardError.ReadLineAsync() is { } line) Output?.Invoke(line + "\n"); });
        transport = new DapTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        transport.EventReceived += OnEvent;
        transport.ConnectionClosed += ex => { if (!disposing) Error?.Invoke(ex.Message); };
        transport.Start();
        Capabilities = await RequestAsync("initialize", new { clientID = "wpfstudio", clientName = "WpfStudio", adapterID = "coreclr", pathFormat = "path", linesStartAt1 = true, columnsStartAt1 = true, supportsVariableType = true, supportsVariablePaging = true, supportsRunInTerminalRequest = false }, cancellationToken).ConfigureAwait(false);
    }

    public async Task LaunchAsync(DebugLaunchConfiguration configuration, IEnumerable<SourceBreakpoint> breakpoints, bool breakOnThrown = false, CancellationToken cancellationToken = default)
    {
        if (IsActive) throw new InvalidOperationException("Stop the active debugging session first.");
        ownsDebuggee = true;
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var launch = RequestAsync("launch", new { program = Path.GetFullPath(configuration.Program), cwd = configuration.WorkingDirectory, args = configuration.Arguments ?? [], env = configuration.Environment ?? new Dictionary<string, string>(), stopAtEntry = false, justMyCode = true, enableStepFiltering = true }, cancellationToken);
            await ConfigureAsync(breakpoints, breakOnThrown, cancellationToken).ConfigureAwait(false);
            await launch.ConfigureAwait(false);
        }
        catch { await TearDownAsync(); throw; }
    }

    public async Task AttachAsync(int processId, IEnumerable<SourceBreakpoint> breakpoints, bool breakOnThrown = false, CancellationToken cancellationToken = default)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (IsActive) throw new InvalidOperationException("Stop the active debugging session first.");
        ownsDebuggee = false;
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var attach = RequestAsync("attach", new { processId }, cancellationToken);
            await ConfigureAsync(breakpoints, breakOnThrown, cancellationToken).ConfigureAwait(false);
            await attach.ConfigureAwait(false);
        }
        catch { await TearDownAsync(); throw; }
    }

    private async Task ConfigureAsync(IEnumerable<SourceBreakpoint> breakpoints, bool thrown, CancellationToken cancellationToken)
    {
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        foreach (var group in breakpoints.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)) await SetBreakpointsAsync(group.Key, group, cancellationToken).ConfigureAwait(false);
        await SetExceptionsAsync(thrown, cancellationToken).ConfigureAwait(false);
        await RequestAsync("configurationDone", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonElement> SetBreakpointsAsync(string path, IEnumerable<SourceBreakpoint> breakpoints, CancellationToken cancellationToken = default) =>
        RequestAsync("setBreakpoints", new { source = new { path = Path.GetFullPath(path) }, breakpoints = breakpoints.Where(x => x.Enabled).Select(x => new { line = x.Line, condition = x.Condition ?? "" }).ToArray(), sourceModified = false }, cancellationToken);
    public Task<JsonElement> SetExceptionsAsync(bool thrown, CancellationToken cancellationToken = default) => RequestAsync("setExceptionBreakpoints", new { filters = thrown ? new[] { "all", "user-unhandled" } : ["user-unhandled"] }, cancellationToken);
    public Task<JsonElement> RequestAsync(string command, object? arguments = null, CancellationToken cancellationToken = default) =>
        (transport ?? throw new InvalidOperationException("No active debugger session.")).RequestAsync(command, arguments, cancellationToken);
    public Task<JsonElement> ContinueAsync(int threadId, CancellationToken cancellationToken = default) => RequestAsync("continue", new { threadId }, cancellationToken);
    public Task<JsonElement> PauseAsync(int threadId, CancellationToken cancellationToken = default) => RequestAsync("pause", new { threadId }, cancellationToken);
    public Task<JsonElement> StepAsync(string command, int threadId, CancellationToken cancellationToken = default)
    {
        if (command is not ("next" or "stepIn" or "stepOut")) throw new ArgumentException("Unknown step command.", nameof(command));
        return RequestAsync(command, new { threadId }, cancellationToken);
    }
    public async Task StopAsync(bool? terminate = null, CancellationToken cancellationToken = default)
    {
        try { if (IsActive) await RequestAsync("disconnect", new { terminateDebuggee = terminate ?? ownsDebuggee }, cancellationToken).ConfigureAwait(false); }
        finally { await TearDownAsync(); }
    }

    private void OnEvent(string name, JsonElement body)
    {
        if (name == "initialized") initialized.TrySetResult();
        if (name == "output" && body.TryGetProperty("output", out var output)) Output?.Invoke(output.GetString() ?? "");
        EventReceived?.Invoke(new DebugEvent(name, body));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (transport is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await RequestAsync("disconnect", new { terminateDebuggee = ownsDebuggee }, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
        finally { await TearDownAsync(); }
    }

    private async ValueTask TearDownAsync()
    {
        disposing = true;
        var activeTransport = transport;
        transport = null;
        if (activeTransport is not null) await activeTransport.DisposeAsync();
        var adapter = process;
        process = null;
        if (adapter is not null)
        {
            try { if (!adapter.HasExited) { adapter.Kill(entireProcessTree: false); await adapter.WaitForExitAsync(); } }
            catch (InvalidOperationException) { }
            adapter.Dispose();
        }
        if (stderr is not null) { try { await stderr; } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } }
        stderr = null;
    }
}
