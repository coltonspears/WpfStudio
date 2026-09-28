using System.Buffers.Binary;
using System.Text.Json;

namespace WpfStudio.Inspection.Protocol;

public static class InspectionWire
{
    public static async Task WriteAsync(Stream stream, InspectionMessage message, CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, InspectionProtocol.JsonOptions);
        if (bytes.Length > InspectionProtocol.MaximumFrameBytes) throw new InvalidDataException("The inspection message exceeds the size limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<InspectionMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > InspectionProtocol.MaximumFrameBytes) throw new InvalidDataException("Invalid inspection frame length.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<InspectionMessage>(bytes, InspectionProtocol.JsonOptions)
            ?? throw new InvalidDataException("Invalid inspection message.");
    }
}
