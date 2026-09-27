using System.Text.Json;

namespace WpfStudio.Database.Services;

/// <summary>Script content only: never connection profiles, passwords, messages, or query results.</summary>
public sealed record QueryRecoveryDocument(Guid Id, string Title, string Text, string SavedText, string? FilePath);

public interface IQueryRecoveryStore
{
    Task<IReadOnlyList<QueryRecoveryDocument>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<QueryRecoveryDocument> documents, CancellationToken cancellationToken = default);
}

public sealed class QueryRecoveryStore : IQueryRecoveryStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public QueryRecoveryStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio", "sql-recovery.json")) { }
    public QueryRecoveryStore(string path) => _path = Path.GetFullPath(path);

    public async Task<IReadOnlyList<QueryRecoveryDocument>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<QueryRecoveryDocument>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(IReadOnlyList<QueryRecoveryDocument> documents, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (documents.Count == 0) { File.Delete(_path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, documents, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
}
