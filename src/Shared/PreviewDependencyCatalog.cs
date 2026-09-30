using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewDependencies;

// Compiled into both sides of the preview transport: discovery and restart
// invalidation must use the same built runtime graph, not compiler references.
internal sealed record PreviewDependencyAsset(string Source, string RelativePath, bool Native);
internal sealed record PreviewDependencyCatalog(IReadOnlyList<PreviewDependencyAsset> Assets, IReadOnlyList<string> Inputs)
{
    public static PreviewDependencyCatalog Read(PreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AssemblyPath)) return new([], []);
        string assembly = Path.GetFullPath(request.AssemblyPath);
        string output = Path.GetDirectoryName(assembly)!;
        var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { assembly };
        if (Directory.Exists(output))
        {
            var pending = new Stack<string>(); pending.Push(output);
            while (pending.TryPop(out var directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A preview dependency directory is a filesystem link. Copy it into the build output first.");
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (inputs.Count >= 20_000) throw new IOException("The preview output exceeds 20,000 files. Use a project-specific output folder.");
                    inputs.Add(file);
                }
                foreach (string child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
        }
        string depsPath = Path.ChangeExtension(assembly, ".deps.json");
        inputs.Add(depsPath);
        using var deps = ReadJson(depsPath);
        if (deps is null) return new([], inputs.ToArray());
        string? assetsPath = request.ProjectAssetsPath ?? (request.ProjectDirectory is null ? null : Path.Combine(request.ProjectDirectory, "obj", "project.assets.json"));
        using var restore = assetsPath is null ? null : ReadJson(assetsPath);
        if (assetsPath is not null) inputs.Add(Path.GetFullPath(assetsPath));
        var packageFolders = restore?.RootElement.TryGetProperty("packageFolders", out var folders) == true
            ? folders.EnumerateObject().Select(folder => Path.GetFullPath(folder.Name)).ToArray() : [];
        if (packageFolders.Length == 0)
            packageFolders = [Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
                ? Path.GetFullPath(custom) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")];
        var root = deps.RootElement;
        if (!root.TryGetProperty("runtimeTarget", out var runtime) || !runtime.TryGetProperty("name", out var targetName)
            || !root.TryGetProperty("targets", out var targets) || !targets.TryGetProperty(targetName.GetString()!, out var target)
            || !root.TryGetProperty("libraries", out var libraries))
            throw new InvalidDataException($"The preview dependency manifest '{depsPath}' has no runtime target. Rebuild the selected project.");
        var rids = new List<string> { RuntimeInformation.RuntimeIdentifier };
        if (root.TryGetProperty("runtimes", out var graph))
            for (int i = 0; i < rids.Count && i < 64; i++)
                if (graph.TryGetProperty(rids[i], out var imports))
                    foreach (var rid in imports.EnumerateArray().Select(item => item.GetString()).OfType<string>())
                        if (!rids.Contains(rid, StringComparer.Ordinal)) rids.Add(rid);
        foreach (string rid in new[] { "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "win", "any" })
            if (!rids.Contains(rid, StringComparer.Ordinal)) rids.Add(rid);
        var selected = new Dictionary<string, (PreviewDependencyAsset Asset, int Rank)>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in target.EnumerateObject())
        {
            if (!libraries.TryGetProperty(library.Name, out var description)
                || !description.TryGetProperty("type", out var type) || type.GetString() != "package"
                || !description.TryGetProperty("path", out var packagePath)) continue;
            foreach (string group in new[] { "runtime", "resources", "native", "runtimeTargets" })
            {
                if (!library.Value.TryGetProperty(group, out var entries)) continue;
                foreach (var entry in entries.EnumerateObject())
                {
                    if (Path.GetFileName(entry.Name) == "_._") continue;
                    bool native = group == "native";
                    int rank = 1000;
                    if (group == "runtimeTargets")
                    {
                        if (!entry.Value.TryGetProperty("rid", out var rid) || (rank = rids.IndexOf(rid.GetString()!)) < 0) continue;
                        native = entry.Value.TryGetProperty("assetType", out var kind) && kind.GetString() == "native";
                    }
                    string relative = native ? entry.Name : group == "resources" && entry.Value.TryGetProperty("locale", out var locale)
                        ? locale.GetString() + "/" + Path.GetFileName(entry.Name) : Path.GetFileName(entry.Name);
                    string key = (native ? "native/" : "managed/") + (native ? Path.GetFileName(entry.Name) : relative);
                    if (selected.TryGetValue(key, out var previous) && previous.Rank < rank) continue;
                    string local = Within(output, relative);
                    string[] candidates = packageFolders.Select(folder => Within(Within(folder, packagePath.GetString()!), entry.Name)).ToArray();
                    // Built copy-local artifacts win over the package cache. Never
                    // substitute a different package version or a ref/ assembly.
                    string source = File.Exists(local) ? local : candidates.FirstOrDefault(File.Exists) ?? candidates[0];
                    if (selected.TryGetValue(key, out previous) && previous.Rank == rank && previous.Asset.Source != source)
                        throw new InvalidDataException($"Conflicting runtime assets for '{key}' in '{depsPath}'. Rebuild the selected project.");
                    selected[key] = (new(source, relative, native), rank);
                    if (selected.Count > 8192) throw new InvalidDataException("The preview runtime dependency graph exceeds 8,192 assets.");
                }
            }
        }
        foreach (var item in selected.Values) inputs.Add(item.Asset.Source);
        return new(selected.Values.Select(item => item.Asset).ToArray(), inputs.ToArray());
    }

    public string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Inputs.Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0" + (file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing") + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string Within(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is "." or ".." || part.Contains(':')))
            throw new InvalidDataException("The preview dependency manifest contains an invalid relative asset path.");
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A preview dependency escapes its package directory.");
        return path;
    }
    private static JsonDocument? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException($"Preview dependency metadata exceeds 16 MiB: {path}");
        using var stream = File.OpenRead(path);
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
    }
}
