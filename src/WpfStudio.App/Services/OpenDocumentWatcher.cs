using System.IO;

namespace WpfStudio.App.Services;

/// <summary>
/// Notices when open files change on disk while WpfStudio has focus, for example from Git in the built-in terminal.
/// One non-recursive watcher per folder of open files; bursts of events are combined into one notification.
/// Window activation still checks every open file, so a missed or overflowed event only delays the prompt.
/// </summary>
public sealed class OpenDocumentWatcher(Action changed) : IDisposable
{
    public const int MaximumDirectories = 64;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    public void Watch(IEnumerable<string> paths)
    {
        var files = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = files.Select(Path.GetDirectoryName).OfType<string>().Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_disposed) return;
            _paths = files;
            foreach (var stale in _watchers.Keys.Where(directory => !directories.Contains(directory)).ToArray())
            {
                _watchers[stale].Dispose();
                _watchers.Remove(stale);
            }
            foreach (var directory in directories.Where(directory => !_watchers.ContainsKey(directory)))
            {
                try
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    watcher.Changed += OnChanged; watcher.Created += OnChanged; watcher.Deleted += OnChanged;
                    watcher.Renamed += (_, args) => { if (IsOpen(args.FullPath) || IsOpen(args.OldFullPath)) Schedule(); };
                    watcher.Error += (_, _) => Schedule();
                    watcher.EnableRaisingEvents = true;
                    _watchers[directory] = watcher;
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException) { }
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args) { if (IsOpen(args.FullPath)) Schedule(); }

    private bool IsOpen(string path) { lock (_gate) return _paths.Contains(path); }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer ??= new Timer(_ => { if (!Volatile.Read(ref _disposed)) changed(); });
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
