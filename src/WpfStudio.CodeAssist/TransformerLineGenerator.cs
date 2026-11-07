using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WpfStudio.CodeAssist;

/// <summary>
/// Writes out the whole line a transformer expects, then decides how much of it to show. A small gate scores the finished
/// line, with separate weights for long and short lines, because one gate across all lengths learns that only short lines
/// are safe. When it declines, the part the network was sure of may still be worth showing.
/// </summary>
internal sealed partial class TransformerLineGenerator : ILineGenerator
{
    /// <summary>The <see cref="LineModelSpec.FeatureVersion"/> this class reads.</summary>
    public const int SupportedFeatureVersion = 2;

    /// <summary>What the gate's weights mean, in their stored order. A manifest listing anything else needs different code.</summary>
    public static readonly IReadOnlyList<string> GateFeatures =
    [
        "log_probability", "per_token", "weakest", "tokens", "characters", "names_in_file", "unknown_names", "line_start",
        "first_token", "closes_brackets", "smallest_gap", "line_in_file", "balanced", "ends_cleanly",
    ];

    // Only this much of the file is searched for names and copies, which is what the gate was fitted on.
    private const int GateContextChars = 5000;
    private const double SmallestLogInput = 1e-6;
    private static readonly string[] Inputs = ["input_ids", "past", "copy_keys", "followers"];
    private static readonly string[] Outputs = ["probabilities", "next_past", "next_copy_keys"];
    private static readonly LineCompletion Nothing = new("", false);

    [GeneratedRegex(@"^\{?\s*(get|set|init)\b|\b(get|set|init)\s*(;|=>)", RegexOptions.None, 1000)]
    private static partial Regex Accessor();

    // Not \w, which in .NET also takes combining marks. Python's does not, and the gate was fitted on Python's names.
    [GeneratedRegex(@"[A-Za-z_][\p{L}\p{N}_]*", RegexOptions.None, 1000)]
    private static partial Regex Name();

    private readonly InferenceSession _session;
    private readonly ByteLevelBpeTokenizer _tokenizer;
    private readonly LineModelSpec _spec;
    private readonly LineGateSpec _gate;
    private readonly long[] _emptyPast;
    private readonly long[] _emptyKeys;

    private TransformerLineGenerator(InferenceSession session, ByteLevelBpeTokenizer tokenizer, LineModelSpec spec, LineGateSpec gate)
    {
        _session = session; _tokenizer = tokenizer; _spec = spec; _gate = gate;
        _emptyPast = [spec.Layers * 2, spec.Heads, 0, spec.HiddenSize / spec.Heads];
        _emptyKeys = [0, spec.CopyKeys];
    }

    public bool OffersPartialLines => true;

    public static TransformerLineGenerator Load(AssistModel model)
    {
        var spec = model.Manifest.Lines!;
        if (spec.FeatureVersion != SupportedFeatureVersion) throw new InvalidDataException($"{model.Name} needs line features version {spec.FeatureVersion}, which this WpfStudio does not have.");
        var gate = spec.Gate ?? throw new InvalidDataException($"{model.Name} has no line gate.");
        if (!gate.Features.SequenceEqual(GateFeatures, StringComparer.Ordinal)) throw new InvalidDataException($"{model.Name} has a line gate over features this WpfStudio does not compute.");
        foreach (var part in (LogisticGate[])[gate.Long, gate.Short])
            if (part.Weights.Count != GateFeatures.Count || part.Mean.Count != GateFeatures.Count || part.Scale.Count != GateFeatures.Count)
                throw new InvalidDataException($"{model.Name} has a line gate of the wrong size.");
        if (spec.Heads <= 0 || spec.HiddenSize % spec.Heads != 0 || spec.CopyKeys <= 0) throw new InvalidDataException($"{model.Name} has an impossible network shape.");
        model.VerifyFiles();
        var tokenizer = ByteLevelBpeTokenizer.Load(model.PathOf("tokenizer.json"));
        if (tokenizer.VocabularySize != spec.VocabSize) throw new InvalidDataException($"{model.Name}: the tokenizer has {tokenizer.VocabularySize} tokens; the manifest says {spec.VocabSize}.");
        var session = AssistSession.Open(model);
        try
        {
            AssistSession.RequireInput(session, model, "input_ids", 1, -1);
            AssistSession.RequireInput(session, model, "past", spec.Layers * 2, spec.Heads, -1, spec.HiddenSize / spec.Heads);
            AssistSession.RequireInput(session, model, "copy_keys", -1, spec.CopyKeys);
            AssistSession.RequireInput(session, model, "followers", -1);
        }
        catch { session.Dispose(); throw; }
        return new TransformerLineGenerator(session, tokenizer, spec, gate);
    }

    public LineCompletion Generate(string code, int maxTokens, CancellationToken token = default)
    {
        var (text, typed) = Prepare(code);
        return Shown(Decode(text[..^typed.Length], maxTokens, token), text, typed, code);
    }

    /// <summary>
    /// What <see cref="Generate"/> returns, and the gate's inputs in <see cref="GateFeatures"/> order from the same pass of the network,
    /// or null inputs when it did not finish the line. Internal so the tests can compare both with training without decoding twice.
    /// </summary>
    internal (LineCompletion Completion, double[]? Features) GenerateWithFeatures(string code, int maxTokens)
    {
        var (text, typed) = Prepare(code);
        var line = Decode(text[..^typed.Length], maxTokens, CancellationToken.None);
        var features = line is null || line.Text.Length < typed.Length ? null : Features(line.Text[typed.Length..], line, code);
        return (Shown(line, text, typed, code), features);
    }

    // The normalized code, and a space typed at its end, which is held back from the network so that its line has to start with one.
    private static (string Text, string Typed) Prepare(string code)
    {
        string text = LineText.Normalize(code);
        return (text, text.EndsWith(' ') ? " " : "");
    }

    private LineCompletion Shown(DecodedLine? line, string text, string typed, string code)
    {
        if (line is null) return Nothing;
        string whole = line.Text;
        // A model expecting no space where one was typed is continuing some other line.
        if (typed.Length > 0 && !whole.StartsWith(' ')) return Nothing;
        whole = whole[typed.Length..];
        if (VisibleLength(whole) < 2) return Nothing;
        if (Trusted(whole, line, code)) return new LineCompletion(whole, true);
        string part = SurePart(line, typed);
        return VisibleLength(part) >= _gate.PartialCharacters || (VisibleLength(part) >= 2 && ClosesWhatIsOpen(text[(text.LastIndexOf('\n') + 1)..], part)) ? new LineCompletion(part, false) : Nothing;
    }

    /// <summary>The text searched for names and copies. Tabs stay as they are, because the gate was fitted on a window measured before they expand.</summary>
    internal static string SearchWindow(string code)
    {
        string text = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.Length > GateContextChars ? text[^GateContextChars..] : text;
    }

    internal static List<string> Names(string text) => [.. Name().Matches(text).Select(match => match.Value)];

    /// <summary>Visible characters, not UTF-16 units, so an emoji counts once as it did when the gate was fitted.</summary>
    internal static int VisibleLength(string text) => text.EnumerateRunes().Count(rune => !Rune.IsWhiteSpace(rune));

    // The likeliest line, or null when the network did not reach its end in time, which usually means it is repeating itself.
    private DecodedLine? Decode(string code, int maxTokens, CancellationToken token)
    {
        var encoded = _tokenizer.Encode(code);
        encoded.Insert(0, _spec.BosId);
        var context = encoded.GetRange(Math.Max(0, encoded.Count - _spec.ContextTokens), Math.Min(encoded.Count, _spec.ContextTokens));
        var followers = new List<long>(context.Count + maxTokens);
        followers.AddRange(context.Skip(1).Select(id => (long)id));
        long[] feed = [.. context.Select(id => (long)id)];
        var line = new DecodedLine();
        IDisposableReadOnlyCollection<OrtValue>? memory = null;
        using var options = new RunOptions();
        using var emptyPast = OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, _emptyPast);
        using var emptyKeys = OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, _emptyKeys);
        try
        {
            for (int step = 0; step < maxTokens; step++)
            {
                token.ThrowIfCancellationRequested();
                using var ids = OrtValue.CreateTensorValueFromMemory(feed, [1, feed.Length]);
                using var known = OrtValue.CreateTensorValueFromMemory(followers.ToArray(), [followers.Count]);
                var outputs = _session.Run(options, Inputs, [ids, memory?[1] ?? emptyPast, memory?[2] ?? emptyKeys, known], Outputs);
                memory?.Dispose();
                memory = outputs;
                var (next, confidence, gap) = Choose(outputs[0].GetTensorDataAsSpan<float>());
                line.Tokens.Add(next); line.Confidence.Add(confidence); line.Gaps.Add(gap);
                string decoded = _tokenizer.Decode(line.Tokens.Where(id => id != _spec.EosId));
                int newline = decoded.IndexOf('\n');
                if (next == _spec.EosId || newline >= 0)
                {
                    line.Text = (newline >= 0 ? decoded[..newline] : decoded).TrimEnd('\r');
                    return line;
                }
                followers.Add(next);
                feed = [next];
            }
        }
        finally { memory?.Dispose(); }
        return null;
    }

    // The likeliest token that may be generated at all, how sure the network was, and how far it stood above the next likeliest.
    private (int Token, double Confidence, double Gap) Choose(ReadOnlySpan<float> probabilities)
    {
        double total = 0, best = -1, second = -1;
        int chosen = 0;
        for (int i = 0; i < probabilities.Length; i++)
        {
            if (i == _spec.BosId || i == _spec.PadId) continue;
            double value = probabilities[i];
            total += value;
            if (value > best) { second = best; best = value; chosen = i; }
            else if (value > second) second = value;
        }
        return (chosen, best / total, (best - second) / total);
    }

    private bool Trusted(string whole, DecodedLine line, string code)
    {
        var part = VisibleLength(whole) >= _gate.LongCharacters && !IsTrivial(whole) ? _gate.Long : _gate.Short;
        return part.Score(Features(whole, line, code)) >= part.Threshold;
    }

    private double[] Features(string whole, DecodedLine line, string code)
    {
        string searched = SearchWindow(code);
        string tail = searched[(searched.LastIndexOf('\n') + 1)..];
        var names = Names(whole);
        var known = Names(searched).ToHashSet(StringComparer.Ordinal);
        int inFile = names.Count(known.Contains);
        double logProbability = line.Confidence.Sum(Math.Log);
        string stripped = whole.Trim();
        int depth = Depth(tail) + Depth(whole);
        return
        [
            logProbability,
            logProbability / line.Confidence.Count,
            Math.Log(Math.Max(line.Confidence.Min(), SmallestLogInput)),
            line.Confidence.Count,
            VisibleLength(whole),
            (double)inFile / Math.Max(1, names.Count),
            names.Count - inFile,
            string.IsNullOrWhiteSpace(tail) ? 1 : 0,
            Math.Log(Math.Max(line.Confidence[0], SmallestLogInput)),
            Count(whole, ")]}") - Count(whole, "([{"),
            line.Gaps.Min(),
            stripped.Length > 0 && searched.Contains(stripped, StringComparison.Ordinal) ? 1 : 0,
            depth == 0 ? 1 : 0,
            stripped.Length > 0 && ";{})],>:".Contains(stripped[^1], StringComparison.Ordinal) ? 1 : 0,
        ];
    }

    // Finishing a bracket opened on this line is worth showing however short it is, unlike a lone "public" or "get;".
    private static bool ClosesWhatIsOpen(string tail, string part)
    {
        int depth = Count(tail, "([{") - Count(tail, ")]}");
        return depth > 0 && depth + Count(part, "([{") - Count(part, ")]}") == 0;
    }

    private static int Depth(string text) => Count(text, "([") - Count(text, ")]");

    private static int Count(string text, string characters) => text.Count(characters.Contains);

    // The tokens before the first one the network was unsure of, never ending halfway through a word.
    private string SurePart(DecodedLine line, string typed)
    {
        int kept = 0;
        while (kept < line.Tokens.Count && line.Confidence[kept] >= _gate.PartialConfidence) kept++;
        string text = _tokenizer.Decode(line.Tokens.Take(kept).Where(id => id != _spec.EosId));
        int newline = text.IndexOf('\n');
        if (newline >= 0) text = text[..newline].TrimEnd('\r');
        else if (kept < line.Tokens.Count && text.Length > 0 && IsWordCharacter(text[^1]) && line.Tokens[kept] != _spec.EosId)
        {
            string following = _tokenizer.Decode([line.Tokens[kept]]);
            if (following.Length > 0 && IsWordCharacter(following[0]))
            {
                int end = text.Length;
                while (end > 0 && IsWordCharacter(text[end - 1])) end--;
                text = text[..end];
            }
        }
        return text.StartsWith(typed, StringComparison.Ordinal) ? text[typed.Length..] : "";
    }

    // An accessor or bare punctuation is a short line however many characters it runs to: it is not what the long gate learned.
    private static bool IsTrivial(string text)
    {
        string stripped = text.Trim();
        return Accessor().IsMatch(stripped) || !stripped.Any(IsWordCharacter);
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    public void Dispose() => _session.Dispose();

    private sealed class DecodedLine
    {
        public List<int> Tokens { get; } = [];
        public List<double> Confidence { get; } = [];
        public List<double> Gaps { get; } = [];
        public string Text { get; set; } = "";
    }
}
