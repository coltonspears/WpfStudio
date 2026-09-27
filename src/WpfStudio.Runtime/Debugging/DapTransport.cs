using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WpfStudio.Runtime.Debugging;

/// <summary>Bounded DAP stream framing, independent of processes and UI.</summary>
public sealed class DapTransport(Stream input, Stream output) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? reader;
    private int sequence;
    public event Action<string, JsonElement>? EventReceived;
    public event Action<Exception>? ConnectionClosed;

    public void Start() => reader ??= ReadLoopAsync();

    public async Task<JsonElement> RequestAsync(string command, object? arguments = null, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { seq = id, type = "request", command, arguments = arguments ?? new { } });
            await writer.WaitAsync(timeout.Token).ConfigureAwait(false);
            try { await WriteFrameAsync(output, payload, timeout.Token).ConfigureAwait(false); }
            finally { writer.Release(); }
            return await completion.Task.ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<JsonElement?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var header = new MemoryStream();
        var single = new byte[1];
        var marker = 0;
        byte[] terminator = [13, 10, 13, 10];
        while (marker != 4)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (header.Length == 0) return null;
                throw new EndOfStreamException("Debugger closed in the middle of a DAP header.");
            }
            header.WriteByte(single[0]);
            marker = single[0] == terminator[marker] ? marker + 1 : single[0] == 13 ? 1 : 0;
            if (header.Length > 8192) throw new InvalidDataException("DAP header exceeds 8 KB.");
        }
        var length = -1;
        foreach (var line in Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            if (length != -1 || !int.TryParse(line.AsSpan(15).Trim(), out length) || length < 0 || length > 16 * 1024 * 1024)
                throw new InvalidDataException("Invalid DAP Content-Length.");
        }
        if (length < 0) throw new InvalidDataException("DAP Content-Length is missing.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task ReadLoopAsync()
    {
        Exception failure = new EndOfStreamException("The debugger connection closed.");
        try
        {
            while (await ReadFrameAsync(input, lifetime.Token).ConfigureAwait(false) is { } message)
            {
                var type = message.GetProperty("type").GetString();
                if (type == "response" && pending.TryRemove(message.GetProperty("request_seq").GetInt32(), out var completion))
                {
                    if (message.TryGetProperty("success", out var success) && success.GetBoolean())
                        completion.TrySetResult(message.TryGetProperty("body", out var body) ? body.Clone() : default);
                    else completion.TrySetException(new InvalidOperationException(message.TryGetProperty("message", out var error) ? error.GetString() : "Debugger request failed."));
                }
                else if (type == "event")
                {
                    EventReceived?.Invoke(message.GetProperty("event").GetString()!, message.TryGetProperty("body", out var body) ? body.Clone() : default);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or InvalidOperationException) { failure = ex; }
        finally
        {
            foreach (var item in pending.Values) item.TrySetException(failure);
            pending.Clear();
            if (!lifetime.IsCancellationRequested) ConnectionClosed?.Invoke(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await input.DisposeAsync();
        await output.DisposeAsync();
        if (reader is not null) { try { await reader; } catch (ObjectDisposedException) { } }
        lifetime.Dispose();
    }
}
