using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace InteractivePreviewProbe;

internal sealed record PixelPoint(int X, int Y);
internal sealed record ProbeState(int Clicks, string Text, string Focus, bool PopupOpen,
    long PopupHandle, long Handle, long ParentHandle, uint Dpi, bool InputRoot,
    PixelPoint ButtonPoint, int NativeMessages, bool KeyboardFocusWithin);
internal sealed record Packet(string Kind, long Id = 0, string? Text = null, long Handle = 0,
    int Width = 0, int Height = 0, int Number = 0, ProbeState? State = null, string? Token = null);

// The probe deliberately uses no production contracts or third-party packages.
internal sealed class Wire(Stream stream) : IDisposable
{
    private readonly Channel<Packet> _outgoing = Channel.CreateBounded<Packet>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _closed = new();
    public bool Send(Packet packet) => _outgoing.Writer.TryWrite(packet);

    public async Task WriteLoopAsync()
    {
        await foreach (Packet packet in _outgoing.Reader.ReadAllAsync(_closed.Token).ConfigureAwait(false))
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
            if (bytes.Length > 262144) throw new InvalidDataException("Probe frame too large.");
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await stream.WriteAsync(header, _closed.Token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, _closed.Token).ConfigureAwait(false);
            await stream.FlushAsync(_closed.Token).ConfigureAwait(false);
        }
    }

    public async Task<Packet> ReadAsync(CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 262144) throw new InvalidDataException("Invalid probe frame.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Packet>(body) ?? throw new InvalidDataException("Empty packet.");
    }

    public void Dispose()
    {
        _outgoing.Writer.TryComplete();
        _closed.Cancel();
        stream.Dispose();
    }
}
