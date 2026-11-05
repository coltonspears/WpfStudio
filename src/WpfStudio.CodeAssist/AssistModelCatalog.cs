using System.Globalization;
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
/// <para>With a <see cref="Gate"/>, the network is a transformer with a copy head: <see cref="Heads"/> attention heads and <see cref="CopyKeys"/> copy key sizes.</para>
/// </summary>
public sealed record LineModelSpec(int FeatureVersion, int VocabSize, int HiddenSize, int ContextTokens, int PadId, int BosId, int EosId, double ContextCacheWeight, int Layers = 1, double TokenConfidence = 0, int Heads = 0, int CopyKeys = 0, LineGateSpec? Gate = null);

/// <summary>Logistic regression over a line's features, so a gate is a dozen numbers rather than another model file.</summary>
public sealed record LogisticGate(double Threshold, double Bias, IReadOnlyList<double> Mean, IReadOnlyList<double> Scale, IReadOnlyList<double> Weights)
{
    /// <summary>The fitted chance, from 0 to 1, that the line is exactly right.</summary>
    public double Score(IReadOnlyList<double> features)
    {
        double sum = Bias;
        for (int i = 0; i < Weights.Count; i++) sum += Weights[i] * (features[i] - Mean[i]) / Scale[i];
        return 1 / (1 + Math.Exp(-sum));
    }
}

/// <summary>
/// When a finished line is shown. Lines of at least <see cref="LongCharacters"/> visible characters have their own gate. A line
/// declined by its gate may still show its first tokens at <see cref="PartialConfidence"/> or above, if they reach <see cref="PartialCharacters"/>.
/// </summary>
public sealed record LineGateSpec(IReadOnlyList<string> Features, int LongCharacters, double PartialConfidence, int PartialCharacters, LogisticGate Long, LogisticGate Short);

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

/// <summary>One thing the settings page says about a model version.</summary>
public sealed record AssistModelFact(string Label, string Value);

/// <summary>One installed version of a model.</summary>
public sealed record AssistModel(AssistModelManifest Manifest, string Directory, Version Version)
{
    public AssistModelKind Kind => Manifest.Kind;
    /// <summary>The name shown to people and written in logs, such as "code-assist-members 2.0.0".</summary>
    public string Name => $"{Manifest.Id} {Manifest.Version}";
    public string PathOf(string file) => Path.Combine(Directory, file);

    /// <summary>When this version was trained, or null for a manifest that does not say.</summary>
    public string? TrainedOn => Manifest.Provenance?.CreatedUtc.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>What this version's files take up on disk, as a person would read it.</summary>
    public string InstalledSize
    {
        get
        {
            long bytes = Manifest.Files.Keys.Select(PathOf).Where(File.Exists).Sum(path => new FileInfo(path).Length);
            return bytes >= 1_000_000
                ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.0} MB")
                : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, bytes / 1000)} KB");
        }
    }

    /// <summary>Everything the settings page lists about this version, most useful first. A fact the manifest lacks is left out.</summary>
    public IReadOnlyList<AssistModelFact> Facts()
    {
        var provenance = Manifest.Provenance;
        var facts = new List<AssistModelFact>
        {
            new("How it scored", Manifest.Evaluation),
            new("Learned from", provenance?.Corpus ?? ""),
            new("Network", Network()),
            new("What it shows", Behavior()),
            new("Trained", TrainedOn ?? ""),
            new("Size", InstalledSize),
            new("Identity", provenance is null ? Name : $"{Name}, trained as {provenance.TrainedAs}, dataset {provenance.DatasetSha256[..Math.Min(12, provenance.DatasetSha256.Length)]}"),
        };
        return [.. facts.Where(fact => fact.Value.Length > 0)];
    }

    private string Network() => Manifest switch
    {
        { Lines: { Gate: not null } lines } => string.Create(CultureInfo.InvariantCulture, $"{lines.Layers}-layer transformer, {lines.HiddenSize:N0} wide, that can copy from earlier in the file. It knows {lines.VocabSize:N0} tokens and reads the last {lines.ContextTokens:N0}."),
        { Lines: { } lines } => string.Create(CultureInfo.InvariantCulture, $"{lines.Layers}-layer GRU with {lines.HiddenSize:N0} units. It knows {lines.VocabSize:N0} tokens and reads the last {lines.ContextTokens:N0}."),
        { Members: { CommentWords: > 0 } members } => string.Create(CultureInfo.InvariantCulture, $"Reads the last {members.LookbackTokens:N0} tokens and {members.CommentWords:N0} comment words."),
        { Members: { } members } => string.Create(CultureInfo.InvariantCulture, $"Reads the last {members.LookbackTokens:N0} tokens."),
        _ => "",
    };

    private string Behavior() => Manifest.Lines switch
    {
        { Gate: { } gate } => string.Create(CultureInfo.InvariantCulture, $"A whole line when a small learned gate trusts it. Otherwise the part it is at least {gate.PartialConfidence * 100:0}% sure of, if that comes to {gate.PartialCharacters} characters or closes a bracket left open on the line."),
        { TokenConfidence: > 0 } lines => string.Create(CultureInfo.InvariantCulture, $"As much of the line as it is at least {lines.TokenConfidence * 100:0}% sure of, one token at a time."),
        not null => "Only suggestions that reach the end of the line.",
        null => "A star on the members it ranks highest. The list itself still comes from Roslyn.",
    };

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
