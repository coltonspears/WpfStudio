using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfStudio.CodeAssist;

public enum AssistModelKind { Members, Lines }

/// <summary>Member features a ranker was trained with. <see cref="FeatureVersion"/> names the code that computes them, so changing it means a new major model version.</summary>
public sealed record MemberModelSpec(int FeatureVersion, int ContextSize, int CandidateSize, int AuxiliarySize, int LookbackTokens, int CommentWords);

/// <summary>
/// Tokens and network shape a line model was trained with. With a <see cref="TokenConfidence"/>, a suggestion stops before the
/// first token the model is less sure of than that, and may be part of a line; without one, only whole lines are offered.
/// </summary>
public sealed record LineModelSpec(int FeatureVersion, int VocabSize, int HiddenSize, int ContextTokens, int PadId, int BosId, int EosId, double ContextCacheWeight, int Layers = 1, double TokenConfidence = 0);

public sealed record AssistModelProvenance(string TrainedAs, DateTimeOffset CreatedUtc, string Corpus, string DatasetSha256);

/// <summary>A version folder's manifest.json: what the model is, where it came from, and the SHA-256 of each file.</summary>
public sealed record AssistModelManifest
{
    public int FormatVersion { get; init; }
    public string Id { get; init; } = "";
    public AssistModelKind Kind { get; init; }
    public string Version { get; init; } = "";
    public string Language { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Limitations { get; init; } = "";
    public string Evaluation { get; init; } = "";
    public AssistModelProvenance? Provenance { get; init; }
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
    public MemberModelSpec? Members { get; init; }
    public LineModelSpec? Lines { get; init; }
}

/// <summary>One installed version of a model.</summary>
public sealed record AssistModel(AssistModelManifest Manifest, string Directory, Version Version)
{
    public AssistModelKind Kind => Manifest.Kind;
    /// <summary>The name shown to people and written in logs, such as "code-assist-members 2.0.0".</summary>
    public string Name => $"{Manifest.Id} {Manifest.Version}";
    public string PathOf(string file) => Path.Combine(Directory, file);

    /// <summary>Refuses files the manifest does not describe: a damaged download, or weights copied into the wrong version.</summary>
    public void VerifyFiles()
    {
        foreach (var (file, expected) in Manifest.Files)
        {
            string path = PathOf(file);
            if (!File.Exists(path)) throw new InvalidDataException($"{Name} is missing {file}.");
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{file} does not match the manifest of {Name}.");
        }
    }
}

/// <summary>
/// The model versions installed under <c>Models/&lt;kind&gt;/&lt;version&gt;</c>. A folder that cannot be used is listed in
/// <see cref="Problems"/> and not offered, so one bad version never hides the others.
/// </summary>
public sealed class AssistModelCatalog(string directory)
{
    /// <summary>The manifest layout this build reads. A later format is a model for a later WpfStudio.</summary>
    public const int SupportedFormatVersion = 1;
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    // Read on first use, from the settings page or a pool thread. While the feature is off, that may never happen.
    private readonly Lazy<(List<AssistModel> Models, List<string> Problems)> _installed = new(() => Scan(directory));

    public static string DefaultDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "CodeAssist", "Models");

    public IReadOnlyList<string> Problems => _installed.Value.Problems;

    /// <summary>The installed versions of a kind of model, newest first.</summary>
    public IReadOnlyList<AssistModel> Versions(AssistModelKind kind) => [.. _installed.Value.Models.Where(model => model.Kind == kind).OrderByDescending(model => model.Version)];

    /// <summary>The requested version, or the newest when <paramref name="version"/> is null or no longer installed.</summary>
    public AssistModel? Resolve(AssistModelKind kind, string? version)
    {
        var versions = Versions(kind);
        return versions.FirstOrDefault(model => model.Manifest.Version == version) ?? versions.FirstOrDefault();
    }

    private static (List<AssistModel>, List<string>) Scan(string directory)
    {
        var models = new List<AssistModel>();
        var problems = new List<string>();
        if (!Directory.Exists(directory)) { problems.Add($"No models were found in {directory}."); return (models, problems); }
        foreach (string manifestPath in Directory.EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories))
        {
            try { models.Add(Read(manifestPath)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { problems.Add($"{Path.GetDirectoryName(manifestPath)}: {exception.Message}"); }
        }
        return (models, problems);
    }

    private static AssistModel Read(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<AssistModelManifest>(File.ReadAllText(manifestPath), ManifestJson) ?? throw new InvalidDataException("The manifest is empty.");
        if (manifest.FormatVersion != SupportedFormatVersion) throw new InvalidDataException($"Manifest format {manifest.FormatVersion} needs a different WpfStudio; this one reads format {SupportedFormatVersion}.");
        string folder = Path.GetDirectoryName(manifestPath)!;
        string kind = manifest.Kind.ToString().ToLowerInvariant();
        if (manifest.Id != "code-assist-" + kind) throw new InvalidDataException($"The id {manifest.Id} does not name a {kind} model.");
        if (!Version.TryParse(manifest.Version, out var version) || version.Build < 0 || version.Revision >= 0) throw new InvalidDataException($"The version {manifest.Version} is not major.minor.patch.");
        // The folder is the version a settings file refers to, so it has to agree with what the manifest says it holds.
        if (Path.GetFileName(folder) != manifest.Version || Path.GetFileName(Path.GetDirectoryName(folder)) != kind) throw new InvalidDataException($"{manifest.Id} {manifest.Version} must live in {kind}/{manifest.Version}.");
        if (manifest.Files.Count == 0 || !manifest.Files.ContainsKey("model.onnx")) throw new InvalidDataException("The manifest lists no model.onnx.");
        if (manifest.Kind == AssistModelKind.Members ? manifest.Members is null : manifest.Lines is null) throw new InvalidDataException($"The manifest has no {kind} settings.");
        return new AssistModel(manifest, folder, version);
    }
}
