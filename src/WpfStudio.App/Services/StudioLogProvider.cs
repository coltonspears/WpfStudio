using Microsoft.Extensions.Logging;
using WpfStudio.Core.IO;

namespace WpfStudio.App.Services;

/// <summary>
/// Appends warnings and errors to <c>Logs/studio.log</c> under the given directory. Logging only
/// queues a line, so it never waits for the disk; a background writer does the IO, and disposing writes what is queued.
/// </summary>
public sealed class StudioLogProvider(string directory) : ILoggerProvider
{
    private readonly QueuedLogFile _file = new(directory, "studio.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(_file, categoryName);

    public void Dispose() => _file.Dispose();

    private sealed class FileLogger(QueuedLogFile file, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                file.Write($"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)} {exception}{Environment.NewLine}");
        }
    }
}
