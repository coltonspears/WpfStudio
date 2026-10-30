using System.Text.Json;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core;

/// <summary>
/// A list kept per workspace in the WpfStudio profile rather than the repository, such as the Task List's own items or
/// pinned file groups. Each workspace gets one JSON file under <c>&lt;data&gt;/&lt;folder&gt;</c>, named by a hash of its path.
/// </summary>
public sealed class WorkspaceStateStore<T>(string folder, string? directory = null)
{
    private readonly string _directory = Path.Combine(directory ?? AppPaths.DataDirectory, folder);
    private string PathFor(string workspacePath)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(workspacePath).ToUpperInvariant())))[..20];
        return Path.Combine(_directory, hash + ".json");
    }
    public async Task<IReadOnlyList<T>> LoadAsync(string workspacePath, CancellationToken token = default)
    {
        var path = PathFor(workspacePath);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<T>>(await File.ReadAllTextAsync(path, token)) ?? [] : []; }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }
    public Task SaveAsync(string workspacePath, IEnumerable<T> items, CancellationToken token = default)
        => DocumentStore.AtomicWriteAsync(PathFor(workspacePath), JsonSerializer.SerializeToUtf8Bytes(items.ToList(), new JsonSerializerOptions { WriteIndented = true }), token);
}

/// <summary>A to-do item the user added by hand to the Task List.</summary>
public sealed record UserTaskItem(string Text, bool IsDone = false, string? Path = null, int Line = 0);

/// <summary>A named set of files opened together, such as the files of one feature.</summary>
public sealed record FileGroup(string Name, List<string> Files);
