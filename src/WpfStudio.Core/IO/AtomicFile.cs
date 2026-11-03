namespace WpfStudio.Core.IO;

/// <summary>Replaces a file in one step, so a crash or a cancelled write leaves the old content or the new content, never part of either.</summary>
public static class AtomicFile
{
    /// <param name="writeThrough">Also push the new content to the disk before it replaces the old, for data whose purpose is surviving a crash.</param>
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken = default, bool writeThrough = false)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        // Unique per write: overlapping writers to one path must not share a temporary file. Hidden, since a script can be saved into the user's own folders.
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = writeThrough ? FileOptions.Asynchronous | FileOptions.WriteThrough : FileOptions.Asynchronous;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, options))
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // There is no asynchronous move, and the file may live on a slow or network drive.
            await Task.Run(() => File.Move(temporary, path, true), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // A leftover temporary file must not replace the result of the write.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
