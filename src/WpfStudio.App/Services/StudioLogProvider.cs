using System.IO;
using Microsoft.Extensions.Logging;
using WpfStudio.Core;

namespace WpfStudio.App.Services;

/// <summary>Appends warnings and errors to <c>Logs/studio.log</c> under the data directory (or <paramref name="directory"/>).</summary>
public sealed class StudioLogProvider(string? directory = null) : ILoggerProvider
{
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly string? _directory = directory;
    private DateTime _retryAfterUtc;
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private sealed class FileLogger(StudioLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)} {exception}{Environment.NewLine}";
            lock (owner._gate)
            {
                if (DateTime.UtcNow < owner._retryAfterUtc) return;
                try
                {
                    var logs = Path.Combine(owner._directory ?? AppPaths.DataDirectory, "Logs"); Directory.CreateDirectory(logs);
                    var path = Path.Combine(logs, "studio.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                    File.AppendAllText(path, line);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
                {
                    // Logging must never fault the code that merely logged. A locked or unwritable log file is retried after a pause.
                    owner._retryAfterUtc = DateTime.UtcNow + RetryPause;
                    System.Diagnostics.Trace.WriteLine("WpfStudio log file unavailable: " + failure.Message);
                }
            }
        }
    }
}
