using System.IO.Pipes;
using StreamJsonRpc;
using WpfStudio.Profiling;

var index = Array.IndexOf(args, "--pipe");
if (index < 0 || index + 1 >= args.Length) { Console.Error.WriteLine("Usage: WpfStudio.ProfilingHost --pipe <name>"); return 2; }
try
{
    using var pipe = new NamedPipeServerStream(args[index + 1], PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await pipe.WaitForConnectionAsync(connectionTimeout.Token);
    using var engine = new MemoryProfilerEngine();
    // System.Text.Json serializes the large analysis results several times faster than the default Newtonsoft formatter.
    // The client must use the same formatter.
    using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter()));
    rpc.AddLocalRpcTarget(engine); rpc.StartListening();
    await rpc.Completion;
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
