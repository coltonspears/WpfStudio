using System.Text;
using System.Threading.Channels;

namespace WpfStudio.Core.IO;

/// <summary>
/// An append-only text file in <c>Logs</c> under the given directory. Writing only queues the text, so it never waits
/// for the disk and is safe on the UI thread; a background writer does the IO, and disposing writes what is queued. A
/// file over two megabytes becomes <c>name.previous</c>, replacing the one before it.
/// </summary>
public sealed class QueuedLogFile : IDisposable
{
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(30), DisposeWait = TimeSpan.FromSeconds(5);
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;
    private DateTime _retryAfterUtc;

    public QueuedLogFile(string directory, string name)
    {
        Path = System.IO.Path.Combine(directory, "Logs", name);
        _writer = Task.Run(WriteQueuedAsync);
    }

    public string Path { get; }

    /// <summary>Queues text, which should end its own lines. Under a flood the oldest queued text is dropped.</summary>
    public void Write(string text) => _lines.Writer.TryWrite(text);

    public void Dispose()
    {
        _lines.Writer.TryComplete();
#pragma warning disable VSTHRD002 // Dispose is synchronous; the writer never needs this thread, and the wait is bounded.
        _writer.Wait(DisposeWait);
#pragma warning restore VSTHRD002
    }

    private async Task WriteQueuedAsync()
    {
        var batch = new StringBuilder();
        while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (_lines.Reader.TryRead(out var line)) batch.Append(line);
            Append(batch.ToString());
        }
    }

    private void Append(string text)
    {
        if (DateTime.UtcNow < _retryAfterUtc) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > 2 * 1024 * 1024) File.Move(Path, Path + ".previous", true);
            File.AppendAllText(Path, text);
        }
        catch (Exception failure)
        {
            // Logging must never fault the code that merely logged, and this loop must outlive any one failure. A locked or unwritable log file is retried after a pause.
            _retryAfterUtc = DateTime.UtcNow + RetryPause;
            System.Diagnostics.Trace.WriteLine("WpfStudio log file unavailable: " + failure.Message);
        }
    }
}
