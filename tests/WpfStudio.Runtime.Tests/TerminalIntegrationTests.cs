using System.Text;
using WpfStudio.Runtime.Terminal;

namespace WpfStudio.Runtime.Tests;

public sealed class TerminalIntegrationTests
{
    [Fact]
    public async Task UnicodeAndControlCTravelThroughThePseudoconsole()
    {
        await using var session = new ConPtySession(Environment.GetEnvironmentVariable("COMSPEC")!, "/Q", Path.GetTempPath());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var text = new StringBuilder();
        var unicodeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pingSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(async () =>
        {
            var decoder = Encoding.UTF8.GetDecoder(); var characters = new char[32768];
            await foreach (var chunk in session.Output.ReadAllAsync(deadline.Token))
            {
                var count = decoder.GetChars(chunk, 0, chunk.Length, characters, 0, false); text.Append(characters, 0, count);
                if (text.ToString().Contains("你好 λ")) unicodeSeen.TrySetResult();
                if (text.ToString().Contains("Reply from 127.0.0.1")) pingSeen.TrySetResult();
                if (text.ToString().Contains("AFTER_INTERRUPT")) { interruptSeen.TrySetResult(); break; }
            }
        }, deadline.Token);
        await session.WriteAsync("chcp 65001 >nul\recho 你好 λ\r", deadline.Token);
        await unicodeSeen.Task.WaitAsync(deadline.Token);
        await session.WriteAsync("ping -n 30 127.0.0.1\r", deadline.Token);
        await pingSeen.Task.WaitAsync(deadline.Token);
        await session.WriteAsync("\u0003", deadline.Token);
        await session.WriteAsync("echo AFTER_INTERRUPT\r", deadline.Token);
        await interruptSeen.Task.WaitAsync(deadline.Token);
        await reader;
    }

    [Fact]
    public async Task RealConPtyAcceptsInputResizesAndTerminatesWithoutHanging()
    {
        await using var session = new ConPtySession(Environment.GetEnvironmentVariable("COMSPEC")!, "/Q", Path.GetTempPath());
        session.Resize(100, 25);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var text = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            await foreach (var chunk in session.Output.ReadAllAsync(deadline.Token))
            {
                text.Append(Encoding.UTF8.GetString(chunk));
                if (text.ToString().Contains("CONPTY_RUNTIME_TEST_OK")) break;
            }
        }, deadline.Token);
        await session.WriteAsync("echo CONPTY_RUNTIME_TEST_OK\r", deadline.Token);
        await reading;
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("CONPTY_RUNTIME_TEST_OK", text.ToString());
    }
}
