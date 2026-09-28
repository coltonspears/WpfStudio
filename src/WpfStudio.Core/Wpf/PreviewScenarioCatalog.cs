using System.Text.Json;
using System.Security.Cryptography;
using WpfStudio.Contracts;

namespace WpfStudio.Core.Wpf;

public sealed record PreviewScenarioCatalogResult(string? Path, IReadOnlyList<PreviewScenario> Scenarios,
    IReadOnlyList<string> Warnings, string? Fingerprint = null);

/// <summary>Reads authored scenario declarations without loading project code.</summary>
public static class PreviewScenarioCatalog
{
    public const string FileName = "wpfstudio.preview.json";
    private const int MaximumBytes = 262144;

    public static async Task<PreviewScenarioCatalogResult> LoadAsync(string? projectDirectory, string sourcePath,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(projectDirectory)) return new(null, [], []);
        string? path = null;
        string? fingerprint = null;
        try
        {
            string directory = System.IO.Path.GetFullPath(projectDirectory);
            string source = System.IO.Path.GetFullPath(sourcePath);
            path = System.IO.Path.Combine(directory, FileName);
            byte[] bytes = new byte[MaximumBytes + 1];
            int length = 0;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (length < bytes.Length)
                {
                    int read = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
            }
            catch (FileNotFoundException) { return new(path, [], []); }
            catch (DirectoryNotFoundException) { return new(path, [], []); }
            fingerprint = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length)));
            if (length > MaximumBytes) throw Invalid("The file exceeds the 256 KiB scenario configuration limit.");
            int prefix = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            using var json = JsonDocument.Parse(bytes.AsMemory(prefix, length - prefix), new JsonDocumentOptions
            { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = json.RootElement;
            Object(root, "$schema", "version", "views");
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int number) || number != 1)
                throw Invalid("Specify scenario configuration version 1.");
            var views = Array(root, "views", 128);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = new List<PreviewScenario>();
            int count = 0;
            foreach (var view in views.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                Object(view, "path", "scenarios");
                string relative = Text(view, "path", 2048);
                if (System.IO.Path.IsPathRooted(relative) || !relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                    || relative.IndexOfAny(['*', '?', ':']) >= 0)
                    throw Invalid("Each view path must be a relative XAML path, including ../ paths for linked files.");
                // This resolves identity only. A linked view may live outside the
                // project; the catalog never opens or executes the named view.
                string viewPath = System.IO.Path.GetFullPath(relative, directory);
                if (!paths.Add(viewPath)) throw Invalid("A view path is declared more than once.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scenario in Array(view, "scenarios", 64).EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 512) throw Invalid("The file exceeds the 512-scenario configuration limit.");
                    Object(scenario, "name", "viewFactory", "dataContextFactory");
                    string name = Text(scenario, "name", 80);
                    if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
                        throw Invalid("'Default' is reserved for the view's own data. Choose another scenario name.");
                    if (!names.Add(name)) throw Invalid($"Scenario '{name}' is declared more than once for a view.");
                    var viewFactory = Factory(scenario, "viewFactory");
                    var dataFactory = Factory(scenario, "dataContextFactory");
                    if (viewFactory is null && dataFactory is null)
                        throw Invalid($"Scenario '{name}' must declare a viewFactory or dataContextFactory.");
                    if (string.Equals(viewPath, source, StringComparison.OrdinalIgnoreCase))
                        selected.Add(new(name, viewFactory, dataFactory));
                }
            }
            token.ThrowIfCancellationRequested();
            return new(path, selected, [], fingerprint);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            string message = exception is JsonException json
                ? $"Invalid JSON at line {(json.LineNumber ?? 0) + 1}, column {(json.BytePositionInLine ?? 0) + 1}."
                : exception.Message;
            if (message.Length > 320) message = message[..320] + "…";
            return new(path, [], [$"Preview scenarios unavailable: {message}"], fingerprint);
        }
    }

    private static PreviewFactory? Factory(JsonElement scenario, string property)
    {
        if (!scenario.TryGetProperty(property, out var factory)) return null;
        Object(factory, "typeName", "methodName");
        return new(Text(factory, "typeName", 1024), Text(factory, "methodName", 256));
    }

    private static void Object(JsonElement value, params string[] properties)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("A configuration entry must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.Contains(property.Name, StringComparer.Ordinal)) throw Invalid($"Unknown configuration property '{property.Name}'.");
            if (!seen.Add(property.Name)) throw Invalid($"Duplicate configuration property '{property.Name}'.");
        }
    }

    private static JsonElement Array(JsonElement parent, string property, int maximum)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > maximum)
            throw Invalid($"'{property}' must be an array with at most {maximum} entries.");
        return value;
    }

    private static string Text(JsonElement parent, string property, int maximum)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > maximum
            || text != text.Trim() || text.Any(char.IsControl))
            throw Invalid($"'{property}' must be nonempty text with no surrounding whitespace or control characters (maximum {maximum} characters).");
        return text;
    }

    private static InvalidOperationException Invalid(string message) => new(message);
}
