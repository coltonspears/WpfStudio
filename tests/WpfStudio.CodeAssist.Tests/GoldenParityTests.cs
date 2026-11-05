using System.Text.Json;

namespace WpfStudio.CodeAssist.Tests;

/// <summary>
/// The models only work on inputs computed exactly as in training, and a difference would lower quality without any error.
/// So the C# port is compared with values the Python training code wrote to Golden/golden.json; generate_golden.py beside it
/// explains how to regenerate them.
/// </summary>
public sealed class GoldenParityTests
{
    private const float FeatureTolerance = 2e-6f;
    private const double GateFeatureTolerance = 1e-3;
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "golden.json"))).RootElement;
    private static readonly AssistModelCatalog Catalog = new(AssistModelCatalog.DefaultDirectory);

    private static AssistModel Model(AssistModelKind kind, string version) => Catalog.Versions(kind).Single(model => model.Manifest.Version == version);
    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.0")]
    public void MemberFeaturesMatchTraining(string version)
    {
        var spec = Model(AssistModelKind.Members, version).Manifest.Members!;
        foreach (var expected in Golden.GetProperty("members").GetProperty(version).EnumerateArray())
        {
            string code = expected.GetProperty("code").GetString()!;
            var names = Strings(expected.GetProperty("names"));
            var actual = MemberFeatures.Build(code, names, spec);
            AssertSparse(expected.GetProperty("context"), actual.Context, code);
            int row = 0;
            foreach (var candidate in expected.GetProperty("candidates").EnumerateArray())
                AssertSparse(candidate, actual.Candidates.AsSpan(spec.CandidateSize * row++, spec.CandidateSize), $"{code} / {names[row - 1]}");
            var auxiliary = expected.GetProperty("auxiliary").EnumerateArray().SelectMany(values => values.EnumerateArray().Select(value => value.GetSingle())).ToArray();
            Assert.Equal(auxiliary.Length, actual.Auxiliary.Length);
            for (int i = 0; i < auxiliary.Length; i++) Assert.True(Math.Abs(auxiliary[i] - actual.Auxiliary[i]) <= FeatureTolerance, $"{code}: auxiliary {i}: {auxiliary[i]} and {actual.Auxiliary[i]}");
        }
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.0")]
    public void MemberRankingMatchesTraining(string version)
    {
        using var ranker = MemberRanker.Load(Model(AssistModelKind.Members, version));
        foreach (var expected in Golden.GetProperty("members").GetProperty(version).EnumerateArray())
        {
            string code = expected.GetProperty("code").GetString()!;
            var names = Strings(expected.GetProperty("names"));
            var scores = expected.GetProperty("scores").EnumerateArray().Select(score => score.GetDouble()).ToArray();
            double best = scores.Max();
            var weights = scores.Select(score => Math.Exp(score - best)).ToArray();
            var wanted = names.Zip(weights).OrderByDescending(pair => pair.Second).Select(pair => (pair.First, Share: pair.Second / weights.Sum())).ToArray();
            var actual = ranker.Rank(code, names);
            Assert.Equal(wanted.Select(member => member.First), actual.Select(member => member.Name));
            for (int i = 0; i < wanted.Length; i++) Assert.Equal(wanted[i].Share, actual[i].Share, 3);
        }
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.0")]
    [InlineData("3.0.0")]
    [InlineData("4.0.0")]
    public void TokensMatchTheTokenizerLibrary(string version)
    {
        var tokenizer = ByteLevelBpeTokenizer.Load(Model(AssistModelKind.Lines, version).PathOf("tokenizer.json"));
        foreach (var expected in Golden.GetProperty("tokens").GetProperty(version).EnumerateArray())
        {
            var ids = tokenizer.Encode(expected.GetProperty("text").GetString()!);
            Assert.Equal(expected.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()), ids);
            Assert.Equal(expected.GetProperty("decoded").GetString(), tokenizer.Decode(ids));
        }
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.0")]
    [InlineData("3.0.0")]
    public void LineGenerationMatchesTraining(string version)
    {
        using var generator = LineGenerator.Load(Model(AssistModelKind.Lines, version));
        foreach (var expected in Golden.GetProperty("lines").GetProperty(version).EnumerateArray())
        {
            var actual = generator.Generate(expected.GetProperty("code").GetString()!, CodeAssistEngine.MaxLineTokens);
            string wanted = expected.GetProperty("text").GetString()!;
            if (string.IsNullOrWhiteSpace(wanted)) { Assert.True(string.IsNullOrWhiteSpace(actual.Text), actual.Text); continue; }
            Assert.Equal(wanted, actual.Text);
            Assert.Equal(expected.GetProperty("finished").GetBoolean(), actual.Finished);
        }
    }

    [Fact]
    public void TransformerLinesAndTheirGateMatchTraining()
    {
        using var generator = TransformerLineGenerator.Load(Model(AssistModelKind.Lines, "4.0.0"));
        int whole = 0, parts = 0;
        foreach (var expected in Golden.GetProperty("lines").GetProperty("4.0.0").EnumerateArray())
        {
            string code = expected.GetProperty("code").GetString()!;
            var actual = generator.Generate(code, CodeAssistEngine.MaxLineTokens);
            Assert.Equal(expected.GetProperty("text").GetString(), actual.Text);
            if (actual.Text.Length > 0)
            {
                Assert.Equal(expected.GetProperty("finished").GetBoolean(), actual.Finished);
                if (actual.Finished) whole++; else parts++;
            }
            // A line the gate declined still has to be scored the same way, or the gate would decide differently elsewhere.
            if (expected.GetProperty("features").ValueKind == JsonValueKind.Null) continue;
            var wanted = expected.GetProperty("features").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            var features = generator.Features(code, CodeAssistEngine.MaxLineTokens);
            Assert.Equal(wanted.Length, features.Length);
            for (int i = 0; i < wanted.Length; i++)
                Assert.True(Math.Abs(wanted[i] - features[i]) <= GateFeatureTolerance, $"{TransformerLineGenerator.GateFeatures[i]}: {wanted[i]} and {features[i]}, for: {code[^Math.Min(60, code.Length)..]}");
        }
        // The replayed code has to exercise both ways of showing something, or this compares very little.
        Assert.InRange(whole, 3, int.MaxValue);
        Assert.InRange(parts, 3, int.MaxValue);
    }

    private static void AssertSparse(JsonElement expected, ReadOnlySpan<float> actual, string label)
    {
        var dense = new float[actual.Length];
        foreach (var entry in expected.EnumerateArray()) dense[entry[0].GetInt32()] = entry[1].GetSingle();
        for (int i = 0; i < dense.Length; i++) Assert.True(Math.Abs(dense[i] - actual[i]) <= FeatureTolerance, $"{label}: bucket {i}: {dense[i]} and {actual[i]}");
    }
}
