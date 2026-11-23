using System.IO.Pipes;
using System.Text.Json;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Terminal;

namespace WpfStudio.Runtime.Tests;

public sealed class TerminalAndDebugHardeningTests
{
    [Fact]
    public async Task DapTransportIsolatesEventSubscriberExceptionsAndKeepsReading()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.GetClientHandleAsString());
        using var output = new MemoryStream();
        await using var transport = new DapTransport(client, output);

        var received = new List<string>();
        var secondEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.EventReceived += (name, _) =>
        {
            if (name == "first") throw new InvalidOperationException("subscriber boom");
        };
        transport.EventReceived += (name, _) =>
        {
            received.Add(name);
            if (name == "second") secondEvent.TrySetResult();
        };
        transport.Start();

        await DapTransport.WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(new { type = "event", @event = "first" }));
        await DapTransport.WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(new { type = "event", @event = "second", body = new { ok = true } }));

        await secondEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first", "second"], received);
    }

    [Fact]
    public async Task DapTransportFailsPendingAndNewRequestsWhenInputCloses()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.GetClientHandleAsString());
        using var output = new MemoryStream();
        await using var transport = new DapTransport(client, output);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ConnectionClosed += _ => closed.TrySetResult();
        transport.Start();

        var pending = transport.RequestAsync("threads");
        server.Dispose();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<EndOfStreamException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<EndOfStreamException>(() => transport.RequestAsync("threads").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DapTransportHandlesBodylessAndNonStringOutputEventsWithoutCrashing()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.GetClientHandleAsString());
        using var output = new MemoryStream();
        await using var transport = new DapTransport(client, output);

        var seen = new List<JsonValueKind>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.EventReceived += (name, body) =>
        {
            seen.Add(body.ValueKind);
            if (name == "done") done.TrySetResult();
        };
        transport.Start();

        await DapTransport.WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(new { type = "event", @event = "output" }));
        await DapTransport.WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(new { type = "event", @event = "output", body = new { output = 42 } }));
        await DapTransport.WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(new { type = "event", @event = "done", body = new { } }));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([JsonValueKind.Undefined, JsonValueKind.Object, JsonValueKind.Object], seen);
    }

    [Fact]
    public async Task TerminalViewModelDoesNotProbeCurrentDirectoryForEmptyPathSegments()
    {
        var tempDir = Directory.CreateTempSubdirectory("WpfStudio-TermPath-").FullName;
        var originalCwd = Environment.CurrentDirectory;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var fakePwsh = Path.Combine(tempDir, "pwsh.exe");
            await File.WriteAllTextAsync(fakePwsh, "");
            Environment.CurrentDirectory = tempDir;
            Environment.SetEnvironmentVariable("PATH", $"{Path.PathSeparator}{Path.PathSeparator}");

            await using var vm = new TerminalViewModel(new InlineDispatcher());
            Assert.DoesNotContain(vm.Shells, shell => string.Equals(shell.Executable, fakePwsh, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ConPtySessionConcurrentResizeAndDisposeDoesNotThrowOrCrash()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var cmd = Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        await using var session = new ConPtySession(cmd, "/c ping -n 5 127.0.0.1 > nul", Environment.SystemDirectory);
        var resizer = Task.Run(() =>
        {
            for (var i = 0; i < 100 && !session.IsClosed; i++)
                session.Resize(80 + (i % 40), 24 + (i % 10));
        });
        await Task.Delay(25);
        await session.DisposeAsync();
        await resizer;
        Assert.True(session.IsClosed);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default) => Task.FromResult(func());
    }
}
