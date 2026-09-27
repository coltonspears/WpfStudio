using System.IO;
using Microsoft.Extensions.Logging;
using WpfStudio.Core;

namespace WpfStudio.App.Services;

public sealed class StudioLogProvider : ILoggerProvider
{
    private readonly object _gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private sealed class FileLogger(StudioLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            lock (owner._gate)
            {
                var directory = Path.Combine(AppPaths.DataDirectory, "Logs"); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "studio.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)} {exception}{Environment.NewLine}");
            }
        }
    }
}
