using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace WpfStudio.Profiling;

/// <summary>Reads an immutable process snapshot in whole pages and keeps them. ClrMD's live reader issues one
/// ReadProcessMemory call per small read (an object's method table, a length, a field), which dominates heap walks;
/// the snapshot never changes, so caching is safe. Owns the data target that owns the snapshot.</summary>
internal sealed class CachingDataReader(DataTarget owner, long maxCacheBytes) : IDataReader, IDisposable
{
    private const int PageSize = 64 * 1024;
    private readonly IDataReader _inner = owner.DataReader;
    private readonly Dictionary<ulong, Page> _pages = [];
    private readonly Queue<ulong> _order = new();
    private readonly int _maxPages = (int)Math.Max(16, maxCacheBytes / PageSize);
    private bool _disposed;

    private sealed record Page(byte[] Data, int Valid);

    public string DisplayName => _inner.DisplayName;
    public bool IsThreadSafe => false;
    public OSPlatform TargetPlatform => _inner.TargetPlatform;
    public Architecture Architecture => _inner.Architecture;
    public int ProcessId => _inner.ProcessId;
    public int PointerSize => _inner.PointerSize;
    public IEnumerable<ModuleInfo> EnumerateModules() => _inner.EnumerateModules();
    public bool GetThreadContext(uint threadID, uint contextFlags, Span<byte> context) => _inner.GetThreadContext(threadID, contextFlags, context);
    public void FlushCachedData() { _pages.Clear(); _order.Clear(); _inner.FlushCachedData(); }

    public int Read(ulong address, Span<byte> buffer)
    {
        // Large reads gain nothing from the cache and would evict the pages the walk is about to reuse.
        if (buffer.Length > PageSize) return _inner.Read(address, buffer);
        var done = 0;
        while (done < buffer.Length)
        {
            var at = address + (ulong)done;
            var pageBase = at & ~(ulong)(PageSize - 1);
            var page = GetPage(pageBase);
            var offset = (int)(at - pageBase);
            var available = page.Valid - offset;
            if (available <= 0)
                // The page is only partly readable; let the underlying reader decide what the rest returns.
                return done + _inner.Read(at, buffer[done..]);
            var count = Math.Min(available, buffer.Length - done);
            page.Data.AsSpan(offset, count).CopyTo(buffer[done..]);
            done += count;
        }
        return done;
    }

    public bool Read<T>(ulong address, out T value) where T : unmanaged
    {
        value = default;
        return Read(address, MemoryMarshal.AsBytes(new Span<T>(ref value))) == Unsafe.SizeOf<T>();
    }

    public T Read<T>(ulong address) where T : unmanaged => Read(address, out T value) ? value : default;

    public bool ReadPointer(ulong address, out ulong value)
    {
        if (PointerSize == 4) { var ok = Read(address, out uint small); value = small; return ok; }
        return Read(address, out value);
    }

    public ulong ReadPointer(ulong address) => ReadPointer(address, out var value) ? value : 0;

    private Page GetPage(ulong pageBase)
    {
        if (_pages.TryGetValue(pageBase, out var page)) return page;
        if (_pages.Count >= _maxPages) _pages.Remove(_order.Dequeue());
        var data = GC.AllocateUninitializedArray<byte>(PageSize);
        var valid = Math.Max(0, _inner.Read(pageBase, data));
        page = new(data, valid);
        _pages.Add(pageBase, page); _order.Enqueue(pageBase);
        return page;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _pages.Clear(); _order.Clear();
        owner.Dispose();
    }
}
