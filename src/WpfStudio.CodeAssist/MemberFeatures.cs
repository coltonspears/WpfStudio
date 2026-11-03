using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;

namespace WpfStudio.CodeAssist;

/// <summary>What a ranker reads for one request. <see cref="Candidates"/> and <see cref="Auxiliary"/> hold one row per name.</summary>
internal sealed record MemberFeatureSet(float[] Context, float[] Candidates, float[] Auxiliary);

/// <summary>
/// Computes the inputs the member rankers were trained on. This must keep producing the same numbers as the training
/// code's <c>features.py</c>: the models learned these exact hash buckets, and a difference lowers quality without any
/// error. <c>GoldenParityTests</c> compares the two.
/// </summary>
internal static partial class MemberFeatures
{
    private const int MatchTimeoutMilliseconds = 1000;
    [GeneratedRegex("""//[^\n]*|/\*[\s\S]*?\*/|@?"(?:""|\\.|[^"\\])*"|'(?:(?:\\.)|[^'\\])*'|[A-Za-z_][A-Za-z_0-9]*|\d+(?:\.\d+)?|[^\w\s]""", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex Lexer();
    [GeneratedRegex(@"[A-Z]+(?=[A-Z][a-z]|$)|[A-Z]?[a-z]+|\d+", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex WordParts();
    [GeneratedRegex(@"[^\W\d]\w*", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex CommentWords();

    public static MemberFeatureSet Build(string code, IReadOnlyList<string> names, MemberModelSpec spec)
    {
        var (allTokens, comments) = Tokenize(code, collectComments: spec.FeatureVersion >= 2);
        int lookback = Math.Min(allTokens.Count, spec.LookbackTokens);
        var tokens = allTokens.GetRange(allTokens.Count - lookback, lookback);
        var context = new float[spec.ContextSize];
        for (int i = 0; i < tokens.Count; i++)
        {
            float recency = (float)(0.25 + 0.75 * (i + 1) / Math.Max(1, tokens.Count));
            context[Bucket("token:" + tokens[i], spec.ContextSize)] += recency;
            if (i > 0) context[Bucket("pair:" + tokens[i - 1] + ":" + tokens[i], spec.ContextSize)] += recency;
        }
        for (int i = 0; i < Math.Min(8, tokens.Count); i++)
            context[Bucket($"last:{i}:{tokens[tokens.Count - 1 - i]}", spec.ContextSize)] += 2f;

        // Keeps only the latest comment words, each weighted by how many tokens ago its comment was.
        var recent = new List<(string Word, double Weight)>();
        if (spec.CommentWords > 0)
            foreach (var (offset, words) in comments)
                recent.AddRange(words.Select(word => (word, CommentWeight(allTokens.Count, offset))));
        if (recent.Count > spec.CommentWords) recent.RemoveRange(0, recent.Count - spec.CommentWords);
        foreach (var (word, weight) in recent) context[Bucket("comment:" + Fold(word), spec.ContextSize)] += (float)(0.5 * weight);
        Normalize(context);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var codeWords = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
            foreach (Match part in WordParts().Matches(token)) codeWords.Add(part.Value.ToLowerInvariant());
        }
        var commentWords = recent.Select(entry => Fold(entry.Word)).ToHashSet(StringComparer.Ordinal);
        var lastCommentWords = comments.Count > 0 ? comments[^1].Words.Select(Fold).ToHashSet(StringComparer.Ordinal) : [];
        double lastCommentWeight = comments.Count > 0 ? CommentWeight(allTokens.Count, comments[^1].Offset) : 0;

        var candidates = new float[names.Count * spec.CandidateSize];
        var auxiliary = new float[names.Count * spec.AuxiliarySize];
        for (int row = 0; row < names.Count; row++)
        {
            string name = names[row];
            WriteNameFeatures(name, candidates.AsSpan(row * spec.CandidateSize, spec.CandidateSize));
            var parts = WordParts().Matches(name).Select(part => Fold(part.Value)).ToList();
            var values = auxiliary.AsSpan(row * spec.AuxiliarySize, spec.AuxiliarySize);
            values[0] = counts.ContainsKey(name) ? 1 : 0;
            values[1] = Math.Min(3, counts.GetValueOrDefault(name)) / 3f;
            values[2] = Share(parts, codeWords);
            values[3] = Math.Min(40, name.Length) / 40f;
            if (spec.FeatureVersion < 2) continue;
            string folded = Fold(name);
            values[4] = commentWords.Contains(folded) ? 1 : 0;
            values[5] = Share(parts, commentWords);
            values[6] = (float)(lastCommentWords.Contains(folded) ? lastCommentWeight : 0);
            values[7] = (float)(Share(parts, lastCommentWords) * lastCommentWeight);
        }
        return new MemberFeatureSet(context, candidates, auxiliary);
    }

    private static (List<string> Tokens, List<(int Offset, List<string> Words)> Comments) Tokenize(string code, bool collectComments)
    {
        var tokens = new List<string>();
        var comments = new List<(int, List<string>)>();
        foreach (Match match in Lexer().Matches(code))
        {
            string token = match.Value;
            if (token.StartsWith("//", StringComparison.Ordinal) || token.StartsWith("/*", StringComparison.Ordinal))
            {
                if (collectComments) comments.Add((tokens.Count, CommentWords().Matches(token).Select(word => word.Value).ToList()));
                continue;
            }
            if (token[0] == '"' || token[0] == '\'' || token.StartsWith("@\"", StringComparison.Ordinal)) token = "<literal>";
            else if (char.IsDigit(token[0])) token = "<number>";
            tokens.Add(token);
        }
        return (tokens, comments);
    }

    // The name as a whole, its camel-case words and its letter triples, hashed into a fixed vector.
    private static void WriteNameFeatures(string name, Span<float> vector)
    {
        vector[Bucket("name:" + name, vector.Length)]++;
        foreach (Match part in WordParts().Matches(name)) vector[Bucket("part:" + part.Value.ToLowerInvariant(), vector.Length)]++;
        string padded = "^" + name.ToLowerInvariant() + "$";
        for (int i = 0; i + 3 <= padded.Length; i++) vector[Bucket(string.Concat("gram:", padded.AsSpan(i, 3)), vector.Length)]++;
        Normalize(vector);
    }

    private static double CommentWeight(int tokenCount, int offset) => 1 / (1 + (tokenCount - offset) / 10d);
    private static float Share(List<string> parts, HashSet<string> words) => parts.Count(words.Contains) / (float)Math.Max(1, parts.Count);
    // Python's casefold on the text this reads: code identifiers and mostly English comments.
    private static string Fold(string value) => value.ToLowerInvariant();

    private static void Normalize(Span<float> vector)
    {
        double sum = 0;
        foreach (float value in vector) sum += value * (double)value;
        float scale = (float)Math.Max(1, Math.Sqrt(sum));
        for (int i = 0; i < vector.Length; i++) vector[i] /= scale;
    }

    private static int Bucket(string value, int size) => (int)(Crc32.HashToUInt32(Encoding.UTF8.GetBytes(value)) % (uint)size);
}
