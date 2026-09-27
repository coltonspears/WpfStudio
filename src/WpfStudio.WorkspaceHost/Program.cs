using System.IO.Pipes;
using StreamJsonRpc;
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
    // Register the selected SDK before JIT compilation touches MSBuildWorkspace.
    try { await ToolchainResolver.RegisterAsync(args[workspaceIndex + 1], cancellation.Token); }
    catch (Exception exception) when (exception is not OperationCanceledException) { Console.Error.WriteLine(exception.Message); }
    using var pipe = new NamedPipeServerStream(args[pipeIndex + 1], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.WaitForConnectionAsync(cancellation.Token);
    using var engine = new WorkspaceEngine();
    using var rpc = new JsonRpc(pipe);
    rpc.AddLocalRpcTarget(engine);
    rpc.StartListening();
    await rpc.Completion;
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
