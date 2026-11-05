using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

namespace WpfStudio.CodeAssist;

/// <summary>The text a line model produced. <see cref="Finished"/> is true when that text reaches the end of the line.</summary>
public sealed record LineCompletion(string Text, bool Finished);

/// <summary>A line model loaded and ready. Models with different inputs differ in how they run, not in what they are asked.</summary>
internal interface ILineGenerator : IDisposable
{
    /// <summary>Whether a suggestion may stop partway through a line, because the model chose where.</summary>
    bool OffersPartialLines { get; }

    LineCompletion Generate(string code, int maxTokens, CancellationToken token = default);
}

/// <summary>What every line model does to code before reading it.</summary>
internal static partial class LineText
{
    [GeneratedRegex(@"^[ \t]*\t[ \t]*", RegexOptions.Multiline, 1000)]
    private static partial Regex LeadingTabs();

    public static string Normalize(string code)
    {
        // Models learn from text with bare newlines, and a carriage return is a token they have rarely seen.
        string text = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        // Nearly all training code indents with spaces, so tab indentation reads as code the model has rarely seen.
        return LeadingTabs().Replace(text, static indent => indent.Value.Replace("\t", "    ", StringComparison.Ordinal));
    }
}

/// <summary>
/// Continues the current line one token at a time, always taking the likeliest token. The network's probabilities are
/// mixed with what followed the same few tokens earlier in this file, which is how local names and literals get reused.
/// </summary>
internal sealed class LineGenerator : ILineGenerator
{
    private static readonly string[] Inputs = ["input_ids", "hidden"];
    private static readonly string[] Outputs = ["logits", "next_hidden"];
    private static readonly int[] HistoryLengths = [6, 5, 4, 3, 2];
    private readonly InferenceSession _session;
    private readonly ByteLevelBpeTokenizer _tokenizer;
    private readonly LineModelSpec _spec;

    private LineGenerator(InferenceSession session, ByteLevelBpeTokenizer tokenizer, LineModelSpec spec) { _session = session; _tokenizer = tokenizer; _spec = spec; }

    /// <summary>Whether a suggestion may stop partway through a line, because a confidence gate chose where.</summary>
    public bool OffersPartialLines => _spec.TokenConfidence > 0;

    public static LineGenerator Load(AssistModel model)
    {
        var spec = model.Manifest.Lines!;
        if (spec.FeatureVersion != 1) throw new InvalidDataException($"{model.Name} needs line features version {spec.FeatureVersion}, which this WpfStudio does not have.");
        model.VerifyFiles();
        var tokenizer = ByteLevelBpeTokenizer.Load(model.PathOf("tokenizer.json"));
        if (tokenizer.VocabularySize != spec.VocabSize) throw new InvalidDataException($"{model.Name}: the tokenizer has {tokenizer.VocabularySize} tokens; the manifest says {spec.VocabSize}.");
        var session = AssistSession.Open(model);
        try
        {
            AssistSession.RequireInput(session, model, "input_ids", 1, -1);
            AssistSession.RequireInput(session, model, "hidden", spec.Layers, 1, spec.HiddenSize);
        }
        catch { session.Dispose(); throw; }
        return new LineGenerator(session, tokenizer, spec);
    }

    public LineCompletion Generate(string code, int maxTokens, CancellationToken token = default)
    {
        string text = LineText.Normalize(code);
        // A token owns the space before it, so a caret after a space leaves a lone space token the model has rarely seen.
        // Asking from before that space lets the model predict it, and the suggestion then drops it again.
        if (!text.EndsWith(' ')) return Continue(text, maxTokens, token);
        var line = Continue(text[..^1], maxTokens, token);
        // A model expecting no space where one was typed is continuing some other line.
        return line with { Text = line.Text.StartsWith(' ') ? line.Text[1..] : "" };
    }

    private LineCompletion Continue(string code, int maxTokens, CancellationToken token)
    {
        var encoded = _tokenizer.Encode(code);
        encoded.Insert(0, _spec.BosId);
        var context = encoded.GetRange(Math.Max(0, encoded.Count - _spec.ContextTokens), Math.Min(encoded.Count, _spec.ContextTokens));
        var seen = LearnLocalContinuations(context);
        var history = context.ToList();
        var generated = new List<int>();
        var hidden = new float[_spec.Layers * _spec.HiddenSize];
        long[] feed = [.. context.Select(id => (long)id)];
        string text = "";
        using var options = new RunOptions();
        for (int step = 0; step < maxTokens; step++)
        {
            token.ThrowIfCancellationRequested();
            var probabilities = NextProbabilities(feed, hidden, options);
            MixLocalContinuations(probabilities, history, seen);
            int next = 0;
            for (int i = 1; i < probabilities.Length; i++) if (probabilities[i] > probabilities[next]) next = i;
            if (probabilities[next] < _spec.TokenConfidence) return new LineCompletion(WithoutUnfinishedWord(text, next), false);
            if (next == _spec.EosId) return new LineCompletion(text, true);
            generated.Add(next); history.Add(next);
            string decoded = _tokenizer.Decode(generated);
            int newline = decoded.IndexOf('\n');
            if (newline >= 0) return new LineCompletion(decoded[..newline].TrimEnd('\r'), true);
            text = decoded;
            feed = [next];
        }
        return new LineCompletion(text, false);
    }

    // Stopping where the next token would have continued a word leaves half a name, so the whole word goes.
    private string WithoutUnfinishedWord(string text, int next)
    {
        if (next == _spec.EosId || text.Length == 0 || !IsWordCharacter(text[^1])) return text;
        string following = _tokenizer.Decode([next]);
        if (following.Length == 0 || !IsWordCharacter(following[0])) return text;
        int end = text.Length;
        while (end > 0 && IsWordCharacter(text[end - 1])) end--;
        return text[..end];
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private double[] NextProbabilities(long[] feed, float[] hidden, RunOptions options)
    {
        using var ids = OrtValue.CreateTensorValueFromMemory(feed, [1, feed.Length]);
        using var state = OrtValue.CreateTensorValueFromMemory(hidden, [_spec.Layers, 1, _spec.HiddenSize]);
        using var outputs = _session.Run(options, Inputs, [ids, state], Outputs);
        outputs[1].GetTensorDataAsSpan<float>().CopyTo(hidden);
        var logits = outputs[0].GetTensorDataAsSpan<float>();
        var values = new double[logits.Length];
        double best = double.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++)
        {
            values[i] = i == _spec.BosId || i == _spec.PadId ? double.NegativeInfinity : logits[i];
            best = Math.Max(best, values[i]);
        }
        double total = 0;
        for (int i = 0; i < values.Length; i++) total += values[i] = Math.Exp(values[i] - best);
        for (int i = 0; i < values.Length; i++) values[i] /= total;
        return values;
    }

    // What followed each run of 2 to 6 tokens in this context. Token ids fit in a char, so a string is the key.
    private static Dictionary<string, Dictionary<int, int>> LearnLocalContinuations(List<int> context)
    {
        var seen = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        for (int index = 0; index < context.Count; index++)
            foreach (int length in HistoryLengths)
            {
                if (index < length) continue;
                string key = Key(context, index - length, length);
                if (!seen.TryGetValue(key, out var counts)) seen[key] = counts = [];
                counts[context[index]] = counts.GetValueOrDefault(context[index]) + 1;
            }
        return seen;
    }

    private void MixLocalContinuations(double[] probabilities, List<int> history, Dictionary<string, Dictionary<int, int>> seen)
    {
        foreach (int length in HistoryLengths)
        {
            if (history.Count < length || !seen.TryGetValue(Key(history, history.Count - length, length), out var counts)) continue;
            var usable = counts.Where(entry => entry.Key != _spec.BosId && entry.Key != _spec.PadId).ToList();
            if (usable.Count == 0) continue;
            for (int i = 0; i < probabilities.Length; i++) probabilities[i] *= 1 - _spec.ContextCacheWeight;
            double total = usable.Sum(entry => entry.Value);
            foreach (var (id, count) in usable) probabilities[id] += _spec.ContextCacheWeight * count / total;
            return;
        }
    }

    private static string Key(List<int> ids, int start, int length) => string.Create(length, (ids, start), static (span, state) =>
    {
        for (int i = 0; i < span.Length; i++) span[i] = (char)state.ids[state.start + i];
    });

    public void Dispose() => _session.Dispose();
}
