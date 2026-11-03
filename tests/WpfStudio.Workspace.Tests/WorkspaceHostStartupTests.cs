using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace WpfStudio.Workspace.Tests;

public sealed class WorkspaceHostStartupTests
{
    [Fact]
    public async Task WorkerAcceptsItsConnectionWhileTheSdkIsStillBeingResolved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var started = Path.Combine(directory, "cli-started");
        // Stands in for a CLI that a cold machine or a virus scan keeps busy past the client's connect budget.
        EmitStalledCli(Path.Combine(directory, "dotnet.exe"));
        var pipeName = "wpfstudio-test-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(FindHostApp())
        {
            ArgumentList = { "--pipe", pipeName, "--workspace", project },
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        // The apphost locates its runtime without PATH, so only the worker's own `dotnet` launches find the stand-in.
        start.Environment["PATH"] = directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var host = Process.Start(start)!;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(20_000);
            var waiting = Stopwatch.StartNew();
            while (!File.Exists(started) && waiting.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
            Assert.True(File.Exists(started), "The stalled CLI was not the one the worker launched, so this test proved nothing.");
            Assert.False(host.HasExited);
        }
        finally
        {
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
            try { Directory.Delete(directory, true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void EmitStalledCli(string path)
    {
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "mscorlib.dll");
        var source = "class Program { static void Main() { System.IO.File.WriteAllText(\"cli-started\", \"\"); System.Threading.Thread.Sleep(300000); } }";
        var compilation = CSharpCompilation.Create("dotnet", [CSharpSyntaxTree.ParseText(source)], [MetadataReference.CreateFromFile(framework)], new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    private static string FindHostApp()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(directory.FullName, "src", "WpfStudio.WorkspaceHost", "bin", configuration, "net10.0", "WpfStudio.WorkspaceHost.exe");
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("Build WpfStudio.WorkspaceHost before running this test.");
    }
}
