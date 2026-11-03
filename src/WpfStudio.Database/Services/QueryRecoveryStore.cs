using System.Text.Json;
using WpfStudio.Core.IO;

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
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (documents.Count == 0) { File.Delete(_path); return; }
            await AtomicFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(documents), cancellationToken, writeThrough: true).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
