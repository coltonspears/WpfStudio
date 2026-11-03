using System.IO;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WpfStudio.Core;

namespace WpfStudio.App.Services;

/// <summary>
/// Appends warnings and errors to <c>Logs/studio.log</c> under the given directory. Logging only
/// queues a line, so it never waits for the disk; a background writer does the IO, and disposing writes what is queued.
/// </summary>
public sealed class StudioLogProvider : ILoggerProvider
{
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(30), DisposeWait = TimeSpan.FromSeconds(5);
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly string _directory;
    private readonly Task _writer;
    private DateTime _retryAfterUtc;

    public StudioLogProvider(string directory)
    {
        _directory = directory;
        _writer = Task.Run(WriteQueuedLinesAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _lines.Writer.TryComplete();
#pragma warning disable VSTHRD002 // ILoggerProvider.Dispose is synchronous; the writer never needs this thread, and the wait is bounded.
        _writer.Wait(DisposeWait);
#pragma warning restore VSTHRD002
    }

    private async Task WriteQueuedLinesAsync()
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
            var logs = Path.Combine(_directory, "Logs"); Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "studio.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, text);
        }
        catch (Exception failure)
        {
            // Logging must never fault the code that merely logged, and this loop must outlive any one failure. A locked or unwritable log file is retried after a pause.
            _retryAfterUtc = DateTime.UtcNow + RetryPause;
            System.Diagnostics.Trace.WriteLine("WpfStudio log file unavailable: " + failure.Message);
        }
    }

    private sealed class FileLogger(StudioLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                owner._lines.Writer.TryWrite($"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)} {exception}{Environment.NewLine}");
        }
    }
}
