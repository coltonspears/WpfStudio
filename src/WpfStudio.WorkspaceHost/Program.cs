using System.IO.Pipes;
using WpfStudio.Contracts.Diagnostics;
using WpfStudio.Workspace;

var pipeIndex = Array.IndexOf(args, "--pipe");
var workspaceIndex = Array.IndexOf(args, "--workspace");
if (pipeIndex < 0 || pipeIndex + 1 >= args.Length || workspaceIndex < 0 || workspaceIndex + 1 >= args.Length)
{
    Console.Error.WriteLine("Usage: WpfStudio.WorkspaceHost --pipe <name> --workspace <sln|slnx|csproj>");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    // Listen first: resolving the SDK launches the CLI twice, which on a cold machine can outlast the client's connect budget.
    // Requests sent in the meantime wait in the pipe until the engine below reads them.
    using var pipe = new NamedPipeServerStream(args[pipeIndex + 1], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var connected = pipe.WaitForConnectionAsync(cancellation.Token);
    // Register the selected SDK before JIT compilation touches MSBuildWorkspace.
    try { await ToolchainResolver.RegisterAsync(args[workspaceIndex + 1], cancellation.Token); }
    catch (Exception exception) when (exception is not OperationCanceledException) { Console.Error.WriteLine(exception.Message); }
    // The IDE gives up on a worker after thirty seconds. One that is still unconnected long after that has lost its IDE, and
    // would otherwise wait for ever, holding the workspace folder as its working directory.
    try { await connected.WaitAsync(TimeSpan.FromMinutes(3), cancellation.Token); }
    catch (TimeoutException) { Console.Error.WriteLine("No client connected; the language worker is exiting."); return 1; }
    using var engine = new WorkspaceEngine();
    // Every request is timed, and the IDE is told of the slow ones over the same connection, beside the workspace protocol.
    var perf = new PerfMonitor("worker");
    using var diagnostics = new WorkerDiagnostics(perf);
    using var rpc = new TimedJsonRpc(pipe, perf);
    rpc.AddLocalRpcTarget(engine);
    rpc.AddLocalRpcTarget<IWorkerDiagnosticsRpc>(diagnostics, null);
    rpc.StartListening();
    await rpc.Completion;
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
