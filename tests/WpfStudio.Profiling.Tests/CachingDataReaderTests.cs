using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using WpfStudio.Profiling;

namespace WpfStudio.Profiling.Tests;

public sealed class CachingDataReaderTests
{
    // Readable regions deliberately start and end inside 64 KiB pages.
    private static readonly (ulong Start, int Length)[] Regions = [(0x10000, 100_000), (0x31000, 5_000)];

    [Fact]
    public void ReadsMatchTheUnderlyingReaderAcrossPagesAndRegionEdges()
    {
        var inner = new FakeReader(Regions);
        using var reader = new CachingDataReader(new DataTarget(inner, new DataTargetOptions()), 1 << 20);
        var expected = new FakeReader(Regions);
        foreach (var (address, length) in new (ulong, int)[]
        {
            (0x10000, 16), (0x1FFF8, 16), (0x10000, 100_000), (0x10000 + 99_990, 16), (0x286A0, 8),
            (0x30F00, 512), (0x31000, 4), (0x31000 + 4_990, 32), (0x50000, 8), (0x2FFFF, 2)
        })
        {
            var actual = new byte[length]; var wanted = new byte[length];
            var read = reader.Read(address, actual);
            Assert.Equal(expected.Read(address, wanted), read);
            Assert.Equal(wanted[..read], actual[..read]);
        }
    }

    [Fact]
    public void RepeatedSmallReadsHitTheCache()
    {
        var inner = new FakeReader(Regions);
        using var reader = new CachingDataReader(new DataTarget(inner, new DataTargetOptions()), 1 << 20);
        for (ulong address = 0x10000; address < 0x10000 + 60_000; address += 8) reader.Read<ulong>(address);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(FakeReader.Value(0x10008) | (ulong)FakeReader.Value(0x10009) << 8, reader.Read<ulong>(0x10008) & 0xFFFF);
        Assert.True(reader.ReadPointer(0x10010, out var pointer));
        Assert.Equal(reader.Read<ulong>(0x10010), pointer);
    }

    private sealed class FakeReader((ulong Start, int Length)[] regions) : IDataReader
    {
        public int Calls { get; private set; }
        public static byte Value(ulong address) => (byte)(address * 31 + (address >> 8));
        public string DisplayName => "fake";
        public bool IsThreadSafe => false;
        public OSPlatform TargetPlatform => OSPlatform.Windows;
        public Architecture Architecture => Architecture.X64;
        public int ProcessId => 1;
        public int PointerSize => 8;
        public IEnumerable<ModuleInfo> EnumerateModules() => [];
        public bool GetThreadContext(uint threadID, uint contextFlags, Span<byte> context) => false;
        public void FlushCachedData() { }

        /// <summary>Like ReadProcessMemory: returns the readable prefix of the request.</summary>
        public int Read(ulong address, Span<byte> buffer)
        {
            Calls++;
            var region = regions.FirstOrDefault(r => address >= r.Start && address < r.Start + (ulong)r.Length);
            if (region.Length == 0) return 0;
            var count = (int)Math.Min((ulong)buffer.Length, region.Start + (ulong)region.Length - address);
            for (var i = 0; i < count; i++) buffer[i] = Value(address + (ulong)i);
            return count;
        }

        public bool Read<T>(ulong address, out T value) where T : unmanaged
        {
            value = default;
            return Read(address, MemoryMarshal.AsBytes(new Span<T>(ref value))) == System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        }

        public T Read<T>(ulong address) where T : unmanaged => Read(address, out T value) ? value : default;
        public bool ReadPointer(ulong address, out ulong value) => Read(address, out value);
        public ulong ReadPointer(ulong address) => Read<ulong>(address);
    }
}
