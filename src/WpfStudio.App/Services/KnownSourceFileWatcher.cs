using System.IO;
using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

public sealed record KnownSourceChange(string? Path, bool RequiresReload = false);

/// <summary>A workspace's source watcher; <see cref="Status"/> describes what it could not watch.</summary>
public interface IKnownSourceWatcher : IDisposable
{
    string? Status { get; }
}

/// <summary>Creates the watcher for one workspace's source files. It holds that workspace's paths, so each load gets its own.</summary>
public delegate IKnownSourceWatcher KnownSourceWatcherFactory(IEnumerable<string> paths, Action<KnownSourceChange> changed, Action? reconcile, IEnumerable<string>? contextFiles, Action<Exception>? failed);

/// <summary>Watches evaluated source paths, including linked files outside the workspace.</summary>
public sealed class KnownSourceFileWatcher : IKnownSourceWatcher
{
    public const int MaximumFiles = 10000;
    public const int MaximumDirectories = 128;
    private readonly HashSet<string> _paths;
    private readonly string[] _roots;
    // Each root with its separator, built once: every file event under the watched folders is tested against them.
    private readonly string[] _rootPrefixes;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action<KnownSourceChange> _changed;
    private readonly Action<Exception>? _failed;
    private readonly Timer? _fallback;
    private int _disposed;
    public string? Status { get; }

    /// <param name="failed">
    /// Receives an exception thrown by <paramref name="changed"/> or <paramref name="reconcile"/>. Notifications arrive on
    /// watcher and timer threads, where an unhandled exception would end the process, so they are never allowed to escape.
    /// </param>
    public KnownSourceFileWatcher(IEnumerable<string> paths, Action<KnownSourceChange> changed, TimeSpan? fallbackInterval = null,
        Action? reconcile = null, IEnumerable<string>? contextFiles = null, Action<Exception>? failed = null)
    {
        _changed = changed; _failed = failed;
        var candidates = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumFiles + 1).ToArray();
        _paths = candidates.Take(MaximumFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allDirectories = _paths.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length).ToArray();
        // A directory under another that is watched needs no watcher of its own. Found by walking up from each, not by comparing
        // every pair: this runs on the UI thread whenever the workspace is set, which is also after every build.
        var known = allDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Nested(string directory)
        {
            for (var parent = Path.GetDirectoryName(directory); parent is not null; parent = Path.GetDirectoryName(parent))
                if (!Path.EndsInDirectorySeparator(parent) && known.Contains(parent)) return true;
            return false;
        }
        var directories = allDirectories.Where(directory => !Nested(directory)).ToArray();
        _roots = directories.Take(MaximumDirectories).ToArray();
        _rootPrefixes = _roots.Select(root => root + Path.DirectorySeparatorChar).ToArray();
        bool partial = candidates.Length > MaximumFiles || directories.Length > MaximumDirectories;
        foreach (var directory in directories.Take(MaximumDirectories))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 65536 // The most the system allows: a build's burst under bin and obj is what overflows it.
                };
                watcher.Changed += Changed; watcher.Created += MembershipChanged; watcher.Deleted += MembershipChanged;
                watcher.Renamed += Renamed; watcher.Error += Error;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
            { partial = true; }
        }
        // Ancestor build configuration is watched at its exact directory only;
        // never turn an ancestor (potentially a drive root) into a recursive watch.
        var contexts = (contextFiles ?? []).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(256).GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase).ToArray();
        if (contexts.Length > 32) partial = true;
        foreach (var group in contexts.Take(32))
        {
            var exact = group.ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                var watcher = new FileSystemWatcher(group.Key) { IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler change = (_, args) => { if (exact.Contains(args.FullPath)) Notify(args.FullPath, true); };
                watcher.Changed += change; watcher.Created += change; watcher.Deleted += change;
                watcher.Renamed += (_, args) => { if (exact.Contains(args.OldFullPath) || exact.Contains(args.FullPath)) Notify(args.FullPath, true); };
                watcher.Error += Error; watcher.EnableRaisingEvents = true; _watchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException) { partial = true; }
        }
        if (partial) Status = "Some source directories could not be watched; periodic reconciliation remains active.";
        // Reconciliation also covers dropped notifications and newly recreated
        // directories. It deliberately does not trust timestamps as text identity.
        var interval = fallbackInterval ?? TimeSpan.FromSeconds(15);
        if (interval > TimeSpan.Zero) _fallback = new Timer(_ =>
        { if (Volatile.Read(ref _disposed) == 0) { if (reconcile is null) Notify(null); else Guarded(reconcile); } }, null, interval, interval);
    }

    private void Changed(object sender, FileSystemEventArgs args) => Guarded(() =>
    {
        if (Ignored(args.FullPath)) return;
        if (IsProjectShape(args.FullPath) || IsSource(args.FullPath) && !_paths.Contains(args.FullPath)) Notify(args.FullPath, true);
        else if (_paths.Contains(args.FullPath)) Notify(args.FullPath);
    });
    private void MembershipChanged(object sender, FileSystemEventArgs args) => Guarded(() =>
    {
        if (Ignored(args.FullPath)) return;
        if (IsKnownSourceDirectory(args.FullPath)) { Notify(args.FullPath, true); return; }
        if (!IsSource(args.FullPath) && !IsProjectShape(args.FullPath)) return;
        if (IsProjectShape(args.FullPath) || !_paths.Contains(args.FullPath)) { Notify(args.FullPath, true); return; }
        if (args.ChangeType != WatcherChangeTypes.Deleted) { Notify(args.FullPath); return; }
        // Editors may replace a file atomically. Allow the replacement to finish
        // before treating a known file's disappearance as project membership loss.
        ConfirmDeletionAsync(args.FullPath).Forget();
    });
    private async Task ConfirmDeletionAsync(string path)
    {
        await Task.Delay(200).ConfigureAwait(false);
        Guarded(() => Notify(path, !File.Exists(path)));
    }
    private void Renamed(object sender, RenamedEventArgs args) => Guarded(() =>
    {
        if (Ignored(args.FullPath) && Ignored(args.OldFullPath)) return;
        // Atomic saves commonly rename a temporary file over an existing source.
        // Known destination + no old evaluated source does not alter membership.
        if (_paths.Contains(args.FullPath) && !_paths.Contains(args.OldFullPath) && !IsProjectShape(args.FullPath)) Notify(args.FullPath);
        else if (IsSource(args.FullPath) || IsSource(args.OldFullPath) || IsProjectShape(args.FullPath) || IsProjectShape(args.OldFullPath)
            || IsKnownSourceDirectory(args.OldFullPath) || Directory.Exists(args.FullPath)) Notify(args.FullPath, true);
    });
    /// <summary>Runs a notification on a watcher or timer thread, where an escaping exception would end the process.</summary>
    private void Guarded(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { _failed?.Invoke(exception); }
            catch { /* The failure handler is the caller's last resort; a fault in it has nowhere further to go. */ }
        }
    }
    private static bool IsSource(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".xaml";
    private bool IsKnownSourceDirectory(string path)
    {
        // Built once, not once per known file: this runs for every file created or deleted anywhere under the watched folders.
        var prefix = path + Path.DirectorySeparatorChar;
        return _paths.Any(source => source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsProjectShape(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".props" or ".targets" or ".sln" or ".slnx";
    private bool Ignored(string path)
    {
        if (_paths.Contains(path)) return false;
        var root = Array.FindIndex(_rootPrefixes, prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var relative = root < 0 ? path : Path.GetRelativePath(_roots[root], path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("obj", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals("artifacts", StringComparison.OrdinalIgnoreCase));
    }
    // Notifications were lost, usually because a build wrote thousands of files under bin and obj at once. What was lost
    // is unknown, but whether the project still has the files it had is cheap to look up, and only a change to that
    // needs the projects loaded again. Changed text is found by the reconciliation every notification without a path asks for.
    private void Error(object sender, ErrorEventArgs args) => Guarded(() => Notify(null, MembershipChanged()));

    private const int MembershipScanEntries = 60_000;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime _shapeCheckedUtc = DateTime.UtcNow;

    /// <summary>
    /// True when a known file is gone, a source file was created since watching began, or a project file was written since
    /// the last look. A source file that was already there and belongs to no project is not news. Unsure counts as changed.
    /// </summary>
    internal bool MembershipChanged()
    {
        var since = _shapeCheckedUtc;
        _shapeCheckedUtc = DateTime.UtcNow;
        try
        {
            if (_paths.Any(path => !File.Exists(path))) return true;
            int entries = 0;
            var pending = new Stack<string>(_roots);
            while (pending.TryPop(out var directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (++entries > MembershipScanEntries) return true;
                    if (IsSource(file) ? !_paths.Contains(file) && File.GetCreationTimeUtc(file) >= _startedUtc
                        : IsProjectShape(file) && File.GetLastWriteTimeUtc(file) >= since) return true;
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (Path.GetFileName(child).ToLowerInvariant() is not ("bin" or "obj" or ".git" or ".vs" or "node_modules" or "artifacts")
                        && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }
    private void Notify(string? path, bool requiresReload = false) { if (Volatile.Read(ref _disposed) == 0) _changed(new(path, requiresReload)); }
    public void Reconcile() => Guarded(() => Notify(null, true));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _fallback?.Dispose();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }
}
