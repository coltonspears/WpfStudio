using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WpfStudio.Api.Tests;

/// <summary>A request as it arrived: the request line, headers and body.</summary>
internal sealed record ReceivedRequest(string RequestLine, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string Method => RequestLine.Split(' ')[0];
    public string Target => RequestLine.Split(' ')[1];
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A one-connection-per-request HTTP/1.1 server on the loopback interface. It also serves as a proxy: a proxied
/// request arrives with an absolute address in its request line, which is what the proxy tests look for.
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<ReceivedRequest, int, string> _respond;
    private readonly Task _loop;
    private int _count;

    public TestServer(Func<ReceivedRequest, int, string> respond)
    {
        _respond = respond;
        _listener.Start();
        _loop = AcceptAsync();
    }

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Address => "http://127.0.0.1:" + Port;

    public static string Response(int status, string reason, string body, params string[] headers)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);
        return $"HTTP/1.1 {status} {reason}\r\n{string.Concat(headers.Select(header => header + "\r\n"))}Content-Length: {bytes}\r\nConnection: close\r\n\r\n{body}";
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            int headerEnd;
            while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) return;
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }
            var head = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray()).Split("\r\n");
            var headers = head.Skip(1).Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0].Trim(), parts => parts.Length > 1 ? parts[1].Trim() : "", StringComparer.OrdinalIgnoreCase);
            var length = headers.TryGetValue("Content-Length", out var text) ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;
            while (buffer.Count - headerEnd - 4 < length)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) break;
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }
            var body = Encoding.UTF8.GetString(buffer.Skip(headerEnd + 4).Take(length).ToArray());
            var request = new ReceivedRequest(head[0], headers, body);
            Requests.Enqueue(request);
            var reply = Encoding.UTF8.GetBytes(_respond(request, Interlocked.Increment(ref _count)));
            await stream.WriteAsync(reply);
        }
    }

    private static int IndexOf(List<byte> buffer, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= buffer.Count; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = buffer[i + j] == pattern[j];
            if (match) return i;
        }
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}
