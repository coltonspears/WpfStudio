using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WpfStudio.Core.Documents;

public sealed record RecoveryDocument(string Path, string Content, DateTimeOffset SavedAt);
public sealed class ExternalFileChangedException(string path) : IOException($"The file changed outside WpfStudio: {path}");

public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, DocumentState> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly string _recoveryDirectory;
    public DocumentStore(string? dataDirectory = null)
    {
        _recoveryDirectory = System.IO.Path.Combine(dataDirectory ?? AppPaths.DataDirectory, "Recovery");
    }
    public IReadOnlyList<DocumentState> Documents => _documents.Values.ToArray();
    public DocumentState? Find(string path) => _documents.GetValueOrDefault(System.IO.Path.GetFullPath(path));
    public async Task<DocumentState> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        path = System.IO.Path.GetFullPath(path);
        if (_documents.TryGetValue(path, out var existing)) return existing;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Files larger than 16 MiB should be opened in a specialized editor.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
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
            if (File.Exists(document.Path))
            {
                var current = Hash(await File.ReadAllBytesAsync(document.Path, cancellationToken));
                if (!overwriteExternal && current != document.DiskHash) throw new ExternalFileChangedException(document.Path);
            }
            else if (document.DiskHash != null && !overwriteExternal) throw new ExternalFileChangedException(document.Path);
            var content = document.Content;
            var bytes = document.Encoding.GetPreamble().Concat(document.Encoding.GetBytes(content)).ToArray();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(document.Path)!);
            await AtomicWriteAsync(document.Path, bytes, cancellationToken);
            document.MarkSavedSnapshot(content, Hash(bytes));
            await _recoveryGate.WaitAsync(cancellationToken);
            try
            {
                if (!document.IsDirty) DeleteRecoveryCore(document.Path);
                else
                {
                    var recovery = new RecoveryDocument(document.Path, document.Content, DateTimeOffset.UtcNow);
                    await AtomicWriteAsync(RecoveryPath(document.Path), JsonSerializer.SerializeToUtf8Bytes(recovery), cancellationToken).ConfigureAwait(false);
                }
            }
            finally { _recoveryGate.Release(); }
        }
        finally { _saveGate.Release(); }
    }
    public async Task<bool> IsExternallyChangedAsync(DocumentState document, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(document.Path)) return document.DiskHash != null;
        return Hash(await File.ReadAllBytesAsync(document.Path, cancellationToken)) != document.DiskHash;
    }
    public async Task ReloadAsync(DocumentState document, CancellationToken cancellationToken = default)
    {
        var version = document.Version;
        var bytes = await File.ReadAllBytesAsync(document.Path, cancellationToken);
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
        foreach (var doc in Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!doc.IsDirty) { DeleteRecoveryCore(doc.Path); continue; }
            var recovery = new RecoveryDocument(doc.Path, doc.Content, DateTimeOffset.UtcNow);
            await AtomicWriteAsync(RecoveryPath(doc.Path), JsonSerializer.SerializeToUtf8Bytes(recovery), cancellationToken).ConfigureAwait(false);
        }
        }
        finally { _recoveryGate.Release(); }
    }
    public async Task<IReadOnlyList<RecoveryDocument>> ReadRecoveryAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<RecoveryDocument>();
        if (!Directory.Exists(_recoveryDirectory)) return records;
        foreach (var file in Directory.EnumerateFiles(_recoveryDirectory, "*.json"))
        {
            try
            {
                var value = JsonSerializer.Deserialize<RecoveryDocument>(await File.ReadAllTextAsync(file, cancellationToken));
                if (value != null) records.Add(value);
            }
            catch (JsonException) { /* A damaged recovery record must not prevent other records loading. */ }
            catch (IOException) { /* A record removed concurrently must not prevent other records loading. */ }
        }
        return records;
    }
    public void DeleteRecovery(string path)
    {
        _recoveryGate.Wait();
        try { DeleteRecoveryCore(path); }
        finally { _recoveryGate.Release(); }
    }
    private void DeleteRecoveryCore(string path) { var file = RecoveryPath(path); if (File.Exists(file)) File.Delete(file); }
    private string RecoveryPath(string path) => System.IO.Path.Combine(_recoveryDirectory, Hash(Encoding.UTF8.GetBytes(path.ToUpperInvariant())) + ".json");
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
