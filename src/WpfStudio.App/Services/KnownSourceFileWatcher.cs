using System.IO;

namespace WpfStudio.App.Services;

public sealed record KnownSourceChange(string? Path, bool RequiresReload = false);

/// <summary>Watches evaluated source paths, including linked files outside the workspace.</summary>
public sealed class KnownSourceFileWatcher : IDisposable
{
    public const int MaximumFiles = 10000;
    public const int MaximumDirectories = 128;
    private readonly HashSet<string> _paths;
    private readonly string[] _roots;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action<KnownSourceChange> _changed;
    private readonly Timer? _fallback;
    private int _disposed;
    public string? Status { get; }

    public KnownSourceFileWatcher(IEnumerable<string> paths, Action<KnownSourceChange> changed, TimeSpan? fallbackInterval = null,
        Action? reconcile = null, IEnumerable<string>? contextFiles = null)
    {
        _changed = changed;
        var candidates = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumFiles + 1).ToArray();
        _paths = candidates.Take(MaximumFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allDirectories = _paths.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length).ToArray();
        var directories = allDirectories.Where(directory => !allDirectories.Any(parent => directory.Length > parent.Length
            && directory.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
        _roots = directories.Take(MaximumDirectories).ToArray();
        bool partial = candidates.Length > MaximumFiles || directories.Length > MaximumDirectories;
        foreach (var directory in directories.Take(MaximumDirectories))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16384
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
        { if (Volatile.Read(ref _disposed) == 0) { if (reconcile is null) Notify(null); else reconcile(); } }, null, interval, interval);
    }

    private void Changed(object sender, FileSystemEventArgs args)
    {
        if (Ignored(args.FullPath)) return;
        if (IsProjectShape(args.FullPath) || IsSource(args.FullPath) && !_paths.Contains(args.FullPath)) Notify(args.FullPath, true);
        else if (_paths.Contains(args.FullPath)) Notify(args.FullPath);
    }
    private void MembershipChanged(object sender, FileSystemEventArgs args)
    {
        if (Ignored(args.FullPath)) return;
        if (IsKnownSourceDirectory(args.FullPath)) { Notify(args.FullPath, true); return; }
        if (!IsSource(args.FullPath) && !IsProjectShape(args.FullPath)) return;
        if (IsProjectShape(args.FullPath) || !_paths.Contains(args.FullPath)) { Notify(args.FullPath, true); return; }
        if (args.ChangeType != WatcherChangeTypes.Deleted) { Notify(args.FullPath); return; }
        // Editors may replace a file atomically. Allow the replacement to finish
        // before treating a known file's disappearance as project membership loss.
        _ = ConfirmDeletionAsync(args.FullPath);
    }
    private async Task ConfirmDeletionAsync(string path)
    {
        await Task.Delay(200).ConfigureAwait(false);
        Notify(path, !File.Exists(path));
    }
    private void Renamed(object sender, RenamedEventArgs args)
    {
        if (Ignored(args.FullPath) && Ignored(args.OldFullPath)) return;
        // Atomic saves commonly rename a temporary file over an existing source.
        // Known destination + no old evaluated source does not alter membership.
        if (_paths.Contains(args.FullPath) && !_paths.Contains(args.OldFullPath) && !IsProjectShape(args.FullPath)) Notify(args.FullPath);
        else if (IsSource(args.FullPath) || IsSource(args.OldFullPath) || IsProjectShape(args.FullPath) || IsProjectShape(args.OldFullPath)
            || IsKnownSourceDirectory(args.OldFullPath) || Directory.Exists(args.FullPath)) Notify(args.FullPath, true);
    }
    private static bool IsSource(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".xaml";
    private bool IsKnownSourceDirectory(string path) => _paths.Any(source => source.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    private static bool IsProjectShape(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".props" or ".targets" or ".sln" or ".slnx";
    private bool Ignored(string path)
    {
        if (_paths.Contains(path)) return false;
        var root = _roots.FirstOrDefault(root => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var relative = root is null ? path : Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("obj", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals("artifacts", StringComparison.OrdinalIgnoreCase));
    }
    private void Error(object sender, ErrorEventArgs args) => Notify(null, true);
    private void Notify(string? path, bool requiresReload = false) { if (Volatile.Read(ref _disposed) == 0) _changed(new(path, requiresReload)); }
    public void Reconcile() => Notify(null, true);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _fallback?.Dispose();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }
}
