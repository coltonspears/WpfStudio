using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfStudio.Core.IO;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Documents;

public sealed record RecoveryDocument(string Path, string Content, DateTimeOffset SavedAt);
public sealed class ExternalFileChangedException(string path) : IOException($"The file changed outside WpfStudio: {path}");

public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, DocumentState> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    // Guarded by _recoveryGate: the version of each document whose recovery record is on disk, so the periodic writer
    // does not serialize and replace the record of a dirty document that has not changed since the last tick.
    private readonly Dictionary<string, (DocumentState Document, long Version)> _recoveryWritten = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _recoveryDirectory;
    public DocumentStore(string dataDirectory)
    {
        _recoveryDirectory = System.IO.Path.Combine(dataDirectory, "Recovery");
    }
    public IReadOnlyList<DocumentState> Documents => _documents.Values.ToArray();
    public DocumentState? Find(string path) => _documents.GetValueOrDefault(System.IO.Path.GetFullPath(path));
    // Read without locking anyone out. A file is read here whenever it may have changed on disk, which is the very moment
    // another program is writing or replacing it; an exclusive read then makes that program's write fail, or Git's checkout.
    // What is read mid-write may be incomplete; the write's own change notification brings the next, complete, read.
    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
    public async Task<DocumentState> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        path = System.IO.Path.GetFullPath(path);
        if (_documents.TryGetValue(path, out var existing)) return existing;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Files larger than 16 MiB should be opened in a specialized editor.");
        var bytes = await ReadSharedAsync(path, cancellationToken);
        if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Files larger than 16 MiB should be opened in a specialized editor.");
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        var encoding = reader.CurrentEncoding;
        if (encoding.CodePage == Encoding.UTF8.CodePage && !bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) encoding = new UTF8Encoding(false);
        return _documents.GetOrAdd(path, new DocumentState(path, text, encoding, Hash(bytes)));
    }
    public DocumentState Create(string path, string content = "")
    {
        path = System.IO.Path.GetFullPath(path);
        if (File.Exists(path) || _documents.ContainsKey(path)) throw new IOException($"A file already exists at {path}.");
        var doc = new DocumentState(path, "");
        doc.Content = content;
        if (!_documents.TryAdd(path, doc)) throw new IOException("The document is already open.");
        return doc;
    }
    public void Close(DocumentState document) => _documents.TryRemove(document.Path, out _);
    public async Task SaveAsync(DocumentState document, bool overwriteExternal = false, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            // The lease denies replacement on purpose, and the operating system would only report "Access to the path is denied."
            if (XamlBuildSourceLease.IsHeld(document.Path))
                throw new IOException($"{document.Name} is locked while the inspection build reads it. Your edits are kept; save again when the build finishes.");
            if (File.Exists(document.Path))
            {
                var current = Hash(await ReadSharedAsync(document.Path, cancellationToken));
                if (!overwriteExternal && current != document.DiskHash) throw new ExternalFileChangedException(document.Path);
            }
            else if (document.DiskHash != null && !overwriteExternal) throw new ExternalFileChangedException(document.Path);
            var content = document.Content;
            byte[] bytes = [.. document.Encoding.GetPreamble(), .. document.Encoding.GetBytes(content)];
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(document.Path)!);
            await AtomicFile.WriteAsync(document.Path, bytes, cancellationToken);
            document.MarkSavedSnapshot(content, Hash(bytes));
            await _recoveryGate.WaitAsync(cancellationToken);
            try
            {
                if (!document.IsDirty) DeleteRecoveryCore(document.Path);
                else await WriteRecoveryCoreAsync(document, cancellationToken).ConfigureAwait(false);
            }
            finally { _recoveryGate.Release(); }
        }
        finally { _saveGate.Release(); }
    }
    public async Task<bool> IsExternallyChangedAsync(DocumentState document, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(document.Path)) return document.DiskHash != null;
        return Hash(await ReadSharedAsync(document.Path, cancellationToken)) != document.DiskHash;
    }
    /// <summary>The file's current text and hash, or null when it no longer exists.</summary>
    public async Task<(string Text, string Hash, Encoding Encoding)?> ReadDiskAsync(DocumentState document, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(document.Path)) return null;
        var bytes = await ReadSharedAsync(document.Path, cancellationToken);
        using var reader = new StreamReader(new MemoryStream(bytes), document.Encoding, true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        var encoding = reader.CurrentEncoding;
        if (encoding.CodePage == Encoding.UTF8.CodePage && !bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) encoding = new UTF8Encoding(false);
        return (text, Hash(bytes), encoding);
    }
    public async Task ReloadAsync(DocumentState document, CancellationToken cancellationToken = default)
    {
        var version = document.Version;
        var bytes = await ReadSharedAsync(document.Path, cancellationToken);
        using var reader = new StreamReader(new MemoryStream(bytes), document.Encoding, true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        if (document.Version != version) throw new InvalidOperationException("The document was edited while reloading. Retry the reload.");
        var encoding = reader.CurrentEncoding;
        if (encoding.CodePage == Encoding.UTF8.CodePage && !bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) encoding = new UTF8Encoding(false);
        document.Encoding = encoding;
        document.Content = content;
        document.MarkSaved(Hash(bytes));
    }
    public async Task WriteRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        Directory.CreateDirectory(_recoveryDirectory);
        // One record that cannot be written, for example while a scanner holds it, must not leave the other documents without theirs.
        Exception? failure = null;
        foreach (var doc in Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!doc.IsDirty) DeleteRecoveryCore(doc.Path);
                else await WriteRecoveryCoreAsync(doc, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure ??= ex; }
        }
        foreach (var closed in _recoveryWritten.Keys.Where(path => !_documents.ContainsKey(path)).ToArray()) _recoveryWritten.Remove(closed);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
        finally { _recoveryGate.Release(); }
    }
    // Called with _recoveryGate held.
    private async Task WriteRecoveryCoreAsync(DocumentState document, CancellationToken cancellationToken)
    {
        // The version is read before the text: an edit between the two is then written again on the next tick instead of being skipped.
        var version = document.Version;
        var file = RecoveryPath(document.Path);
        if (_recoveryWritten.TryGetValue(document.Path, out var written) && ReferenceEquals(written.Document, document) && written.Version == version && File.Exists(file)) return;
        var recovery = new RecoveryDocument(document.Path, document.Content, DateTimeOffset.UtcNow);
        await AtomicFile.WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(recovery), cancellationToken).ConfigureAwait(false);
        _recoveryWritten[document.Path] = (document, version);
    }
    public async Task<IReadOnlyList<RecoveryDocument>> ReadRecoveryAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<RecoveryDocument>();
        if (!Directory.Exists(_recoveryDirectory)) return records;
        string[] files;
        try { files = Directory.EnumerateFiles(_recoveryDirectory, "*.json").ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return records; }
        foreach (var file in files)
        {
            try
            {
                var value = JsonSerializer.Deserialize<RecoveryDocument>(await File.ReadAllTextAsync(file, cancellationToken));
                if (value != null) records.Add(value);
            }
            catch (JsonException) { /* A damaged recovery record must not prevent other records loading. */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A record removed or locked concurrently must not prevent other records loading. */ }
        }
        return records;
    }
    /// <summary>Removes a document's recovery record. Asynchronous because the gate is held across the recovery writer's disk writes.</summary>
    public async Task DeleteRecoveryAsync(string path, CancellationToken cancellationToken = default)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { DeleteRecoveryCore(path); }
        finally { _recoveryGate.Release(); }
    }
    private void DeleteRecoveryCore(string path) { _recoveryWritten.Remove(path); var file = RecoveryPath(path); if (File.Exists(file)) File.Delete(file); }
    private string RecoveryPath(string path) => System.IO.Path.Combine(_recoveryDirectory, Hash(Encoding.UTF8.GetBytes(path.ToUpperInvariant())) + ".json");
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
