using System.Text;
using System.Text.Json;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

public sealed class DapTransportTests
{
    [Fact]
    public async Task UnicodePayloadUsesByteLengthAndSurvivesFragmentedReads()
    {
        using var encoded = new MemoryStream();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type = "event", @event = "output", body = new { output = "你好 λ 😀" } });
        await DapTransport.WriteFrameAsync(encoded, body);
        using var fragmented = new FragmentedStream(encoded.ToArray());
        var actual = await DapTransport.ReadFrameAsync(fragmented);
        Assert.Equal("你好 λ 😀", actual!.Value.GetProperty("body").GetProperty("output").GetString());
        Assert.Null(await DapTransport.ReadFrameAsync(fragmented));
    }
    [Theory]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: 999999999\r\n\r\n")]
    [InlineData("Other: 5\r\n\r\n")]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}")]
    public async Task InvalidFrameCannotAllocateUnboundedMemory(string input)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        await Assert.ThrowsAsync<InvalidDataException>(() => DapTransport.ReadFrameAsync(stream));
    }
    [Fact]
    public async Task TruncatedPayloadFailsExplicitly()
    {
        using var stream = new MemoryStream("Content-Length: 5\r\n\r\n{}"u8.ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() => DapTransport.ReadFrameAsync(stream));
    }
    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
