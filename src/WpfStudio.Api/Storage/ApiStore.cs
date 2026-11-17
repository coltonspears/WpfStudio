using System.Text.Json;
using WpfStudio.Api.Models;
using WpfStudio.Core.IO;

namespace WpfStudio.Api.Storage;

/// <summary>Keeps secrets unreadable on disk. The application protects them for the Windows user; tests keep them as they are.</summary>
public interface ISecretProtector
{
    string Protect(string value);
    /// <summary>Throws when the value was protected for another user or machine.</summary>
    string Unprotect(string value);
}

/// <summary>Stores secrets as they are. For tests, and for platforms without a user-bound protection.</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public static PlainSecretProtector Instance { get; } = new();
    public string Protect(string value) => value;
    public string Unprotect(string value) => value;
}

/// <summary>The environments, and which one requests use.</summary>
public sealed class EnvironmentFile
{
    public List<ApiEnvironment> Environments { get; set; } = [];
    public string? ActiveEnvironmentId { get; set; }
}

/// <summary>The request tabs that were open, so they come back with unsaved edits intact.</summary>
public sealed class SessionFile
{
    public List<SessionTab> Tabs { get; set; } = [];
    public int SelectedIndex { get; set; }
}

public sealed class SessionTab
{
    public ApiRequest Request { get; set; } = new();
    public string? CollectionId { get; set; }
    /// <summary>The saved request this tab edits, or null for a request not saved to a collection yet.</summary>
    public string? SavedRequestId { get; set; }
    public bool IsDirty { get; set; }
}

/// <summary>
/// Where the API client keeps its work: one file per collection, with the OpenAPI document it came from beside it, the
/// environments, the history of sent requests and the open tabs. Secret variables and credentials typed into requests
/// are protected before they reach disk.
/// </summary>
public sealed class ApiStore(string directory, ISecretProtector protector)
{
    public const int HistoryLimit = 100;
    private const string ProtectedPrefix = "protected:";
    private readonly string _collections = Path.Combine(directory, "collections");

    public string Directory { get; } = directory;

    /// <summary>Messages about files that could not be read, collected while loading so the workbench can show them.</summary>
    public List<string> Problems { get; } = [];

    public async Task<List<ApiCollection>> LoadCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ApiCollection>();
        if (!System.IO.Directory.Exists(_collections)) return result;
        foreach (var path in System.IO.Directory.EnumerateFiles(_collections, "*.json").Where(path => !path.EndsWith(".openapi.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                if (await JsonSerializer.DeserializeAsync<ApiCollection>(stream, ApiJson.Options, cancellationToken).ConfigureAwait(false) is { } collection)
                {
                    collection.Id = Path.GetFileNameWithoutExtension(path);
                    collection.HasSpecification = File.Exists(SpecificationPath(collection.Id));
                    result.Add(Reveal(collection));
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            { Problems.Add($"{Path.GetFileName(path)} could not be read: {exception.Message}"); }
        }
        return result.OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task SaveCollectionAsync(ApiCollection collection, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAsync(CollectionPath(collection.Id), JsonSerializer.SerializeToUtf8Bytes(Conceal(collection), ApiJson.Options), cancellationToken);

    public Task DeleteCollectionAsync(string id) => Task.Run(() =>
    {
        foreach (var path in new[] { CollectionPath(id), SpecificationPath(id) }) if (File.Exists(path)) File.Delete(path);
    });

    public Task SaveSpecificationAsync(string collectionId, string specification, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAsync(SpecificationPath(collectionId), System.Text.Encoding.UTF8.GetBytes(specification), cancellationToken);

    public async Task<string?> LoadSpecificationAsync(string collectionId, CancellationToken cancellationToken = default)
    {
        var path = SpecificationPath(collectionId);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task<EnvironmentFile> LoadEnvironmentsAsync(CancellationToken cancellationToken = default)
    {
        var file = await ReadAsync<EnvironmentFile>("environments.json", cancellationToken).ConfigureAwait(false) ?? new EnvironmentFile();
        foreach (var environment in file.Environments) RevealVariables(environment.Variables);
        return file;
    }

    public Task SaveEnvironmentsAsync(EnvironmentFile file, CancellationToken cancellationToken = default)
    {
        var copy = ApiJson.Clone(file);
        foreach (var environment in copy.Environments) ConcealVariables(environment.Variables);
        return WriteAsync("environments.json", copy, cancellationToken);
    }

    public async Task<List<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        var history = await ReadAsync<List<HistoryEntry>>("history.json", cancellationToken).ConfigureAwait(false) ?? [];
        foreach (var entry in history) RevealRequest(entry.Request);
        return history;
    }

    public Task SaveHistoryAsync(IEnumerable<HistoryEntry> history, CancellationToken cancellationToken = default)
    {
        var copy = ApiJson.Clone(history.Take(HistoryLimit).ToList());
        foreach (var entry in copy) ConcealRequest(entry.Request);
        return WriteAsync("history.json", copy, cancellationToken);
    }

    public async Task<SessionFile> LoadSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = await ReadAsync<SessionFile>("session.json", cancellationToken).ConfigureAwait(false) ?? new SessionFile();
        foreach (var tab in session.Tabs) RevealRequest(tab.Request);
        return session;
    }

    public Task SaveSessionAsync(SessionFile session, CancellationToken cancellationToken = default)
    {
        var copy = ApiJson.Clone(session);
        foreach (var tab in copy.Tabs) ConcealRequest(tab.Request);
        return WriteAsync("session.json", copy, cancellationToken);
    }

    public Task<T?> ReadSettingsAsync<T>(string name, CancellationToken cancellationToken = default) where T : class => ReadAsync<T>(name, cancellationToken);
    public Task WriteSettingsAsync<T>(string name, T value, CancellationToken cancellationToken = default) => WriteAsync(name, value, cancellationToken);

    private string CollectionPath(string id) => Path.Combine(_collections, SafeId(id) + ".json");
    private string SpecificationPath(string id) => Path.Combine(_collections, SafeId(id) + ".openapi.json");

    // Ids are generated, but a collection file copied in by hand must not be able to name a path outside the folder.
    private static string SafeId(string id)
    {
        var safe = new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return safe.Length == 0 ? throw new ArgumentException("A collection id must contain letters or digits.", nameof(id)) : safe;
    }

    private async Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken) where T : class
    {
        var path = Path.Combine(Directory, name);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, ApiJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Problems.Add($"{name} could not be read: {exception.Message}");
            return null;
        }
    }

    private Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(Path.Combine(Directory, name), JsonSerializer.SerializeToUtf8Bytes(value, ApiJson.Options), cancellationToken);

    // ------------------------------------------------------------------ secrets

    private ApiCollection Conceal(ApiCollection collection)
    {
        var copy = ApiJson.Clone(collection);
        ConcealVariables(copy.Variables);
        ConcealAuth(copy.Auth); copy.Proxy.Password = Protect(copy.Proxy.Password);
        foreach (var (request, _) in copy.AllRequests()) ConcealRequest(request);
        return copy;
    }

    private ApiCollection Reveal(ApiCollection collection)
    {
        RevealVariables(collection.Variables);
        RevealAuth(collection.Auth); collection.Proxy.Password = Unprotect(collection.Proxy.Password);
        foreach (var (request, _) in collection.AllRequests()) RevealRequest(request);
        return collection;
    }

    private void ConcealRequest(ApiRequest request) { ConcealAuth(request.Auth); request.Proxy.Password = Protect(request.Proxy.Password); }
    private void RevealRequest(ApiRequest request) { RevealAuth(request.Auth); request.Proxy.Password = Unprotect(request.Proxy.Password); }

    private void ConcealAuth(ApiAuth auth) { auth.Token = Protect(auth.Token); auth.Password = Protect(auth.Password); auth.KeyValue = Protect(auth.KeyValue); }
    private void RevealAuth(ApiAuth auth) { auth.Token = Unprotect(auth.Token); auth.Password = Unprotect(auth.Password); auth.KeyValue = Unprotect(auth.KeyValue); }

    private void ConcealVariables(List<KeyValue> variables) { foreach (var variable in variables.Where(variable => variable.Secret)) variable.Value = Protect(variable.Value); }
    private void RevealVariables(List<KeyValue> variables) { foreach (var variable in variables.Where(variable => variable.Secret)) variable.Value = Unprotect(variable.Value); }

    // Empty values and references to variables ({{token}}) are not secrets, and stay readable in the file.
    private string Protect(string value) => value.Length == 0 || IsReference(value) ? value : ProtectedPrefix + protector.Protect(value);

    private string Unprotect(string value)
    {
        if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return value;
        try { return protector.Unprotect(value[ProtectedPrefix.Length..]); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            const string problem = "Some saved secrets were protected by another Windows user or computer and were cleared. Enter them again.";
            if (!Problems.Contains(problem)) Problems.Add(problem);
            return "";
        }
    }

    private static bool IsReference(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal) && trimmed.IndexOf("}}", StringComparison.Ordinal) == trimmed.Length - 2;
    }
}
