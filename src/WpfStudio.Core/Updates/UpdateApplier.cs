using System.Diagnostics;

namespace WpfStudio.Core.Updates;

/// <summary>
/// Replaces an installed package with a staged one, file by file, once WpfStudio has closed. It runs in the staged
/// package's own WpfStudio.exe, so nothing it copies is in use by itself.
/// </summary>
/// <remarks>
/// Each file it replaces is first moved into <c>.update-backup</c> inside the package. Windows lets a file be renamed even
/// while a process that has not quite exited still has it loaded, where overwriting it would fail. If any file cannot be
/// replaced, every change is undone and the previous version is left as it was. Files the new package no longer has are
/// left in place: the folder may hold things that are not WpfStudio's, such as a data folder set with
/// WPFSTUDIO_DATA_DIRECTORY, and an unused file does no harm. The backup is removed the next time WpfStudio starts.
/// </remarks>
public sealed class UpdateApplier(TimeSpan? lockTimeout = null)
{
    public const string BackupFolderName = ".update-backup";
    /// <summary>Written into a backup once its update is complete. A backup without it holds files an undo could not put back, and is kept.</summary>
    public const string CompletedMarkerName = ".completed";
    private readonly TimeSpan _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);

    /// <summary>Waits for a process to end. True once it has, or when it had already; false after the timeout.</summary>
    public static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; } // Already gone.
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        }
    }

    public async Task ApplyAsync(string source, string target, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The update cannot be installed over the folder it was unpacked in.");
        if (!File.Exists(Path.Combine(source, InstalledPackage.ExecutableName)) || !File.Exists(Path.Combine(source, InstalledPackage.MarkerFileName)))
            throw new UpdateException($"{source} is not a complete WpfStudio package.");
        // Only ever over an existing installation, so a wrong argument cannot fill an arbitrary folder.
        if (!File.Exists(Path.Combine(target, InstalledPackage.ExecutableName)))
            throw new UpdateException($"{target} holds no {InstalledPackage.ExecutableName} to update.");

        // The executable last, so a package without its newest executable never looks complete.
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path))
            .OrderBy(relative => relative.Equals(InstalledPackage.ExecutableName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(relative => relative, StringComparer.Ordinal)
            .ToArray();
        var backup = Path.Combine(target, BackupFolderName, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        var journal = new List<Change>(files.Length);
        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = files[i];
                var destination = Path.Combine(target, relative);
                var change = new Change(relative);
                journal.Add(change);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                change.Existed = File.Exists(destination);
                if (change.Existed)
                {
                    var saved = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    await RetryAsync(() => File.Move(destination, saved), cancellationToken).ConfigureAwait(false);
                    change.Saved = true;
                }
                // Overwriting is safe: the original was moved away or there was none, so anything there is a copy of ours cut short.
                var from = Path.Combine(source, relative);
                await RetryAsync(() => File.Copy(from, destination, overwrite: true), cancellationToken).ConfigureAwait(false);
                if (i % 25 == 0 || i == files.Length - 1) progress?.Report(new UpdateProgress($"Replacing files… {i + 1} of {files.Length}", (double)(i + 1) / files.Length));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            progress?.Report(new UpdateProgress("Restoring the previous version…"));
            var restored = await UndoAsync(target, backup, journal).ConfigureAwait(false);
            throw new UpdateException(restored
                ? $"WpfStudio could not be updated, so the previous version was kept: {exception.Message}"
                : $"WpfStudio could not be updated, and some files could not be put back. The previous files are in {backup}. {exception.Message}", exception);
        }
        // The update is complete, so its backup may go. Without the mark it is only kept, which costs space and nothing else.
        try
        {
            Directory.CreateDirectory(backup);
            await File.WriteAllTextAsync(Path.Combine(backup, CompletedMarkerName), DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Puts back what <see cref="ApplyAsync"/> changed, newest first, waiting out a file held open as the changes did.
    /// Not cancellable: half an undo is worse than a slow one. False when a file could not be restored.
    /// </summary>
    private async Task<bool> UndoAsync(string target, string backup, List<Change> journal)
    {
        var complete = true;
        for (var i = journal.Count - 1; i >= 0; i--)
        {
            var change = journal[i];
            var destination = Path.Combine(target, change.Relative);
            try
            {
                // The saved original replaces whatever was copied, even a copy cut short. With nothing there before,
                // whatever is there now is the update's. A file whose move failed was never touched.
                var saved = Path.Combine(backup, change.Relative);
                if (change.Saved) await RetryAsync(() => File.Move(saved, destination, overwrite: true), CancellationToken.None).ConfigureAwait(false);
                else if (!change.Existed && File.Exists(destination)) await RetryAsync(() => File.Delete(destination), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { complete = false; }
        }
        if (complete) UpdateDownloader.DeleteDirectory(backup);
        return complete;
    }

    /// <summary>
    /// Retries while another process still has the file open: a worker or a debugger can take a few seconds to exit
    /// after WpfStudio has, and a virus scanner may be reading a file just written. Other failures, such as a full disk,
    /// will not pass by waiting, so they are not retried.
    /// </summary>
    private async Task RetryAsync(Action action, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        for (var delay = 100; ; delay = Math.Min(delay * 2, 1000))
        {
            try { action(); return; }
            catch (Exception exception) when (IsHeldOpen(exception) && waited.Elapsed < _lockTimeout)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A sharing or lock violation (Windows errors 32 and 33), or access refused, which an open file can also cause.</summary>
    private static bool IsHeldOpen(Exception exception) =>
        exception is UnauthorizedAccessException || exception is IOException { HResult: var result } and not (FileNotFoundException or DirectoryNotFoundException) && (result & 0xFFFF) is 32 or 33;

    /// <summary>
    /// How many other WpfStudio processes run from <paramref name="applicationDirectory"/>. Their files would be replaced
    /// under them, so an update waits until they are closed. Processes of other Windows accounts cannot be inspected and are not counted.
    /// </summary>
    public static int CountOtherInstances(string applicationDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var count = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledPackage.ExecutableName)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.MainModule?.FileName is { } path && string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)) count++;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { /* Another account's, or already gone. */ }
            }
        }
        return count;
    }

    /// <summary>
    /// Removes the backups of completed updates from the package: run by WpfStudio once it has started. The backup of an
    /// update that could not be undone completely is kept, since it holds the only copy of the files it names.
    /// </summary>
    public static void RemoveBackups(string target)
    {
        var root = Path.Combine(target, BackupFolderName);
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var backup in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(backup, CompletedMarkerName))) UpdateDownloader.DeleteDirectory(backup);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The next start tries again. */ }
    }

    private sealed class Change(string relative)
    {
        public string Relative { get; } = relative;
        public bool Existed { get; set; }
        public bool Saved { get; set; }
    }
}
