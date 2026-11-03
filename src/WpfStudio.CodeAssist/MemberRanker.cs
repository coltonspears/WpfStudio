using Microsoft.ML.OnnxRuntime;

namespace WpfStudio.CodeAssist;

/// <summary>A candidate name and its share of the model's score. Shares compare names within one request; they are not confidence.</summary>
public sealed record RankedMember(string Name, double Share);

/// <summary>Scores candidate member names against the code before them.</summary>
internal sealed class MemberRanker : IDisposable
{
    private static readonly string[] Inputs = ["context", "candidates", "auxiliary"];
    private static readonly string[] Outputs = ["scores"];
    private readonly InferenceSession _session;
    private readonly MemberModelSpec _spec;

    private MemberRanker(InferenceSession session, MemberModelSpec spec) { _session = session; _spec = spec; }

    public static MemberRanker Load(AssistModel model)
    {
        var spec = model.Manifest.Members!;
        // A feature version this build cannot compute would still run, and rank by noise.
        int expectedAuxiliary = spec.FeatureVersion switch { 1 => 4, 2 => 8, _ => throw new InvalidDataException($"{model.Name} needs member features version {spec.FeatureVersion}, which this WpfStudio does not have.") };
        if (spec.AuxiliarySize != expectedAuxiliary) throw new InvalidDataException($"{model.Name} declares {spec.AuxiliarySize} auxiliary features; version {spec.FeatureVersion} has {expectedAuxiliary}.");
        model.VerifyFiles();
        var session = AssistSession.Open(model);
        try
        {
            AssistSession.RequireInput(session, model, "context", 1, spec.ContextSize);
            AssistSession.RequireInput(session, model, "candidates", -1, spec.CandidateSize);
            AssistSession.RequireInput(session, model, "auxiliary", -1, spec.AuxiliarySize);
        }
        catch { session.Dispose(); throw; }
        var ranker = new MemberRanker(session, spec);
        // The first run compiles the feature code and starts the session, which can outlast the popup's ranking budget. Spend that here, in the background.
        try { ranker.Rank("x.", ["A", "B"]); }
        catch { ranker.Dispose(); throw; }
        return ranker;
    }

    /// <summary>Every name, best first; equal scores keep the order they came in.</summary>
    public IReadOnlyList<RankedMember> Rank(string code, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return [];
        var features = MemberFeatures.Build(code, names, _spec);
        using var options = new RunOptions();
        using var context = OrtValue.CreateTensorValueFromMemory(features.Context, [1, _spec.ContextSize]);
        using var candidates = OrtValue.CreateTensorValueFromMemory(features.Candidates, [names.Count, _spec.CandidateSize]);
        using var auxiliary = OrtValue.CreateTensorValueFromMemory(features.Auxiliary, [names.Count, _spec.AuxiliarySize]);
        using var outputs = _session.Run(options, Inputs, [context, candidates, auxiliary], Outputs);
        var scores = outputs[0].GetTensorDataAsSpan<float>();
        if (scores.Length != names.Count) throw new InvalidDataException($"The ranker returned {scores.Length} scores for {names.Count} names.");

        double best = double.NegativeInfinity;
        foreach (float score in scores)
        {
            if (!float.IsFinite(score)) throw new InvalidDataException("The ranker returned a score that is not a number.");
            best = Math.Max(best, score);
        }
        var weights = new double[scores.Length];
        double total = 0;
        for (int i = 0; i < scores.Length; i++) total += weights[i] = Math.Exp(scores[i] - best);
        return Enumerable.Range(0, names.Count).OrderByDescending(i => weights[i]).Select(i => new RankedMember(names[i], weights[i] / total)).ToArray();
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Sessions run on the caller's thread with no pool of their own: requests come one at a time and must not compete with the editor.</summary>
internal static class AssistSession
{
    public static InferenceSession Open(AssistModel model)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        return new InferenceSession(model.PathOf("model.onnx"), options);
    }

    /// <summary>Compares the graph's input shape with the manifest; -1 is a dimension that varies.</summary>
    public static void RequireInput(InferenceSession session, AssistModel model, string name, params int[] dimensions)
    {
        if (!session.InputMetadata.TryGetValue(name, out var input) || !input.Dimensions.SequenceEqual(dimensions))
            throw new InvalidDataException($"{model.Name}: the graph's {name} input does not match the manifest.");
    }
}
