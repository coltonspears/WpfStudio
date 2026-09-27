using System.Net;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WpfStudio.App.Features.Packages;

/// <summary>NuGet V3 browsing and SDK-owned project mutations. No NuGet credentials are read or copied by the IDE.</summary>
public sealed class NuGetPackageService : INuGetPackageService, IDisposable
{
    private readonly HttpClient _http;
    private readonly IPackageProcessRunner _process;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private static readonly Regex PackageId = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant);
    private static readonly Regex ExactVersion = new(@"^\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)?(?:\+[A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant);

    public NuGetPackageService() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, new PackageProcessRunner(), true) { }
    public NuGetPackageService(HttpClient http, IPackageProcessRunner process) : this(http, process, false) { }
    private NuGetPackageService(HttpClient http, IPackageProcessRunner process, bool ownsHttp)
    { _http = http; _process = process; _ownsHttp = ownsHttp; }

    public async Task<PackageInventory> ReadProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var configs = FindConfigurationFiles(directory);
        var central = FindAncestorFile(directory, "Directory.Packages.props");
        var warnings = new List<string>();
        IReadOnlyList<PackageSource> sources;
        try
        {
            var result = await _process.RunAsync(directory, ["nuget", "list", "source", "--format", "Short"], null, cancellationToken).ConfigureAwait(false);
            sources = result.ExitCode == 0 ? ParseSources(result.Output) : [];
            if (result.ExitCode != 0) warnings.Add("Could not evaluate NuGet sources: " + ShortError(result.Output));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { sources = []; warnings.Add(ex.Message); }

        IReadOnlyList<InstalledPackage> packages;
        try
        {
            var sdk = await _process.RunAsync(directory, ["--version"], null, cancellationToken).ConfigureAwait(false);
            var args = new List<string> { "list", Path.GetFullPath(projectPath), "package", "--format", "json", "--output-version", "1" };
            if (sdk.ExitCode == 0 && int.TryParse(sdk.Output.Trim().Split('.')[0], out var major) && major >= 10) args.Add("--no-restore");
            var result = await _process.RunAsync(directory, args, null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidDataException(ShortError(result.Output));
            packages = ParseInstalledPackages(result.Output);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            packages = ReadProjectReferences(projectPath, central);
            warnings.Add("Showing declared references because SDK evaluation failed. Conditions/imports may differ; restore the project to obtain resolved versions. " + ex.Message);
        }
        return new(packages, sources, configs, central, warnings.Count == 0 ? null : string.Join("\n", warnings));
    }

    public async Task<IReadOnlyList<PackageSearchItem>> SearchAsync(PackageSource source, string query, bool prerelease, CancellationToken cancellationToken)
    {
        var endpoints = await GetEndpointsAsync(source, cancellationToken).ConfigureAwait(false);
        if (endpoints.Search is null) throw new InvalidOperationException("This feed does not expose NuGet V3 search. Choose a searchable V3 source, or install an exact package ID and version below.");
        var uri = endpoints.Search + (endpoints.Search.Contains('?') ? "&" : "?") + "q=" + Uri.EscapeDataString(query.Trim()) + "&skip=0&take=40&semVerLevel=2.0.0&prerelease=" + prerelease.ToString().ToLowerInvariant();
        using var json = await ReadJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("data", out var data)) return [];
        return data.EnumerateArray().Select(x => new PackageSearchItem(Text(x, "id"), Text(x, "version"), Text(x, "description"),
            x.TryGetProperty("authors", out var authors) ? authors.ValueKind == JsonValueKind.Array ? string.Join(", ", authors.EnumerateArray().Select(a => a.GetString())) : authors.ToString() : "",
            x.TryGetProperty("totalDownloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : 0,
            Text(x, "projectUrl"), x.TryGetProperty("versions", out var versions) ? SortVersions(versions.EnumerateArray().Select(v => Text(v, "version")), prerelease) : [Text(x, "version")])).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(PackageSource source, string packageId, bool prerelease, CancellationToken cancellationToken)
    {
        ValidatePackageId(packageId);
        var endpoints = await GetEndpointsAsync(source, cancellationToken).ConfigureAwait(false);
        if (endpoints.Content is null) throw new InvalidOperationException("This feed does not expose package versions. Enter an exact version to install.");
        using var json = await ReadJsonAsync(endpoints.Content.TrimEnd('/') + "/" + packageId.ToLowerInvariant() + "/index.json", cancellationToken).ConfigureAwait(false);
        return SortVersions(json.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()!), prerelease);
    }

    public async Task<PackageProcessResult> ChangeAsync(string projectPath, string packageId, string? version, bool remove,
        IProgress<string>? output, CancellationToken cancellationToken)
    {
        var args = CreateChangeArguments(projectPath, packageId, version, remove);
        if (!await _mutation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Another package change is already running.");
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            output?.Report("dotnet " + string.Join(" ", args.Select(x => x.Contains(' ') ? '"' + x + '"' : x)));
            var result = await _process.RunAsync(directory, args, output, cancellationToken).ConfigureAwait(false);
            // Remove does not restore. Re-evaluate assets before notifying the workspace.
            if (remove && result.ExitCode == 0)
            {
                output?.Report("Restoring project after removing the reference…");
                var restore = await _process.RunAsync(directory, ["restore", Path.GetFullPath(projectPath), "--verbosity", "minimal"], output, cancellationToken).ConfigureAwait(false);
                return new(restore.ExitCode, result.Output + restore.Output);
            }
            return result;
        }
        finally { _mutation.Release(); }
    }

    public static IReadOnlyList<string> CreateChangeArguments(string projectPath, string packageId, string? version, bool remove)
    {
        ValidatePackageId(packageId);
        if (!Path.GetExtension(projectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select a C# project.");
        if (remove) return ["remove", Path.GetFullPath(projectPath), "package", packageId];
        if (version is null || !ExactVersion.IsMatch(version)) throw new ArgumentException("Choose an exact package version, for example 8.4.2 or 9.0.0-preview.1.");
        // The legacy command order works with SDK 8, 9 and 10. The SDK owns CPM and source-mapping decisions.
        return ["add", Path.GetFullPath(projectPath), "package", packageId, "--version", version];
    }

    public static IReadOnlyList<PackageSource> ParseSources(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.TrimStart().StartsWith("E ", StringComparison.Ordinal)).Select(line => line.Trim()[2..].Trim())
        .Where(address => !string.IsNullOrWhiteSpace(address))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(address => new PackageSource(address.Contains("api.nuget.org", StringComparison.OrdinalIgnoreCase) ? "nuget.org" : address, address)).ToArray();

    public static IReadOnlyList<InstalledPackage> ParseInstalledPackages(string output)
    {
        var start = output.IndexOf('{');
        using var json = JsonDocument.Parse(start < 0 ? output : output[start..]);
        var rows = new List<InstalledPackage>();
        foreach (var project in json.RootElement.GetProperty("projects").EnumerateArray())
        {
            if (!project.TryGetProperty("frameworks", out var frameworks)) continue;
            foreach (var framework in frameworks.EnumerateArray())
            {
                if (!framework.TryGetProperty("topLevelPackages", out var packages)) continue;
                foreach (var package in packages.EnumerateArray())
                {
                    if (package.TryGetProperty("autoReferenced", out var auto) && auto.ValueKind == JsonValueKind.True) continue;
                    rows.Add(new(Text(package, "id"), Text(package, "requestedVersion"), Text(package, "resolvedVersion"), Text(framework, "framework")));
                }
            }
        }
        return rows.GroupBy(x => (x.Id, x.RequestedVersion, x.ResolvedVersion)).Select(g => g.First() with { Frameworks = string.Join(", ", g.Select(x => x.Frameworks).Distinct()) }).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<InstalledPackage> ReadProjectReferences(string projectPath, string? centralPath)
    {
        var document = XDocument.Load(projectPath);
        var central = centralPath is null ? null : XDocument.Load(centralPath);
        var properties = document.Descendants().Where(x => x.Parent?.Name.LocalName == "PropertyGroup").GroupBy(x => x.Name.LocalName).ToDictionary(x => x.Key, x => x.Last().Value);
        if (central is not null)
            foreach (var property in central.Descendants().Where(x => x.Parent?.Name.LocalName == "PropertyGroup")) properties.TryAdd(property.Name.LocalName, property.Value);
        string Resolve(string text) => Regex.Replace(text, @"\$\(([^)]+)\)", m => properties.GetValueOrDefault(m.Groups[1].Value, m.Value));
        return document.Descendants().Where(x => x.Name.LocalName == "PackageReference" && x.Attribute("Include") is not null).Select(x =>
        {
            var id = x.Attribute("Include")!.Value;
            var centralEntry = central?.Descendants().LastOrDefault(p => p.Name.LocalName == "PackageVersion" && string.Equals((p.Attribute("Include") ?? p.Attribute("Update"))?.Value, id, StringComparison.OrdinalIgnoreCase));
            var version = x.Attribute("VersionOverride")?.Value ?? x.Elements().FirstOrDefault(p => p.Name.LocalName == "VersionOverride")?.Value
                ?? x.Attribute("Version")?.Value ?? x.Elements().FirstOrDefault(p => p.Name.LocalName == "Version")?.Value
                ?? centralEntry?.Attribute("Version")?.Value ?? centralEntry?.Elements().FirstOrDefault(p => p.Name.LocalName == "Version")?.Value ?? "(evaluated by MSBuild)";
            return new InstalledPackage(id, Resolve(version), "Not evaluated", x.Attribute("Condition")?.Value ?? "Declared reference");
        }).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> SortVersions(IEnumerable<string> versions, bool prerelease) => versions
        .Where(x => ExactVersion.IsMatch(x) && (prerelease || !x.Split('+')[0].Contains('-')))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x, PackageVersionComparer.Instance).ToArray();

    private async Task<(string? Search, string? Content)> GetEndpointsAsync(PackageSource source, CancellationToken token)
    {
        if (!Uri.TryCreate(source.Address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("Local package folders can be used for install/restore. Browse requires an HTTP NuGet V3 source; enter an exact ID/version to use this source.");
        using var json = await ReadJsonAsync(source.Address, token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("resources", out var resources)) throw new InvalidOperationException("This source is not a NuGet V3 service index.");
        string? search = null, content = null;
        foreach (var resource in resources.EnumerateArray())
        {
            var type = Text(resource, "@type"); var address = Text(resource, "@id");
            if (type.StartsWith("SearchQueryService", StringComparison.Ordinal)) search ??= address;
            if (type.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal)) content ??= address;
        }
        return (search, content);
    }

    private async Task<JsonDocument> ReadJsonAsync(string address, CancellationToken token)
    {
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("This feed requires authentication for browsing. CLI install/restore still uses your NuGet.Config credentials or credential provider. Search a public source, or enter the exact ID and version.");
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (limited.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("NuGet response exceeded the 8 MiB browsing limit. Narrow the search.");
            limited.Write(buffer, 0, count);
        }
        limited.Position = 0;
        return await JsonDocument.ParseAsync(limited, cancellationToken: token).ConfigureAwait(false);
    }

    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static void ValidatePackageId(string packageId) { if (!PackageId.IsMatch(packageId)) throw new ArgumentException("Enter a valid NuGet package ID (letters, digits, dots, hyphens or underscores)."); }
    private static string ShortError(string error) => error.Length > 1200 ? error[^1200..].Trim() : error.Trim();
    private static string? FindAncestorFile(string directory, string name)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, name))) return Path.Combine(current.FullName, name);
        return null;
    }
    private static IReadOnlyList<string> FindConfigurationFiles(string directory)
    {
        var files = new List<string>();
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config");
        if (File.Exists(user)) files.Add(user);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "NuGet.Config");
            if (File.Exists(path)) files.Add(path);
        }
        return files;
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); _mutation.Dispose(); }
}

internal sealed class PackageVersionComparer : IComparer<string>
{
    public static readonly PackageVersionComparer Instance = new();
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.CompareOrdinal(x, y);
        var left = x.Split('+')[0].Split('-', 2); var right = y.Split('+')[0].Split('-', 2);
        var a = left[0].Split('.'); var b = right[0].Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var value = (i < a.Length ? System.Numerics.BigInteger.Parse(a[i]) : 0).CompareTo(i < b.Length ? System.Numerics.BigInteger.Parse(b[i]) : 0);
            if (value != 0) return value;
        }
        if (left.Length != right.Length) return left.Length == 1 ? 1 : -1;
        if (left.Length == 1) return 0;
        a = left[1].Split('.'); b = right[1].Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var numericA = System.Numerics.BigInteger.TryParse(a[i], out var numberA);
            var numericB = System.Numerics.BigInteger.TryParse(b[i], out var numberB);
            var value = numericA && numericB ? numberA.CompareTo(numberB) : numericA != numericB ? numericA ? -1 : 1 : StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
            if (value != 0) return value;
        }
        return a.Length.CompareTo(b.Length);
    }
}
