using System.Text.RegularExpressions;

namespace WpfStudio.Core.Text;

public enum DiffRowKind { Unchanged, Removed, Added, Modified }

/// <summary>A changed character range within one side's line text.</summary>
public readonly record struct DiffSpan(int Start, int Length);

/// <summary>
/// One aligned row of a two-sided diff. A <see cref="DiffRowKind.Modified"/> row pairs an old and a new line;
/// <see cref="DiffRowKind.Removed"/> and <see cref="DiffRowKind.Added"/> rows have only one side. Line numbers are 1-based.
/// </summary>
public sealed record DiffRow(DiffRowKind Kind, int? OldLine, int? NewLine, string OldText, string NewText,
    IReadOnlyList<DiffSpan> OldSpans, IReadOnlyList<DiffSpan> NewSpans)
{
    public bool IsChange => Kind != DiffRowKind.Unchanged;
}

/// <summary>Rows <c>[Start, End)</c> shown together: one or more change blocks plus surrounding context.</summary>
public readonly record struct DiffHunk(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// Line diff with aligned rows for side-by-side and inline views. Line endings are normalized, so a file whose
/// only difference is CRLF versus LF is identical. Paired changed lines carry word-level spans when they are
/// similar enough for a word diff to be readable.
/// </summary>
public sealed partial class TextDiff
{
    private const double WordDiffSimilarity = 0.35;
    private const int WordDiffMaxLength = 2000;
    private readonly DiffRow[] _rows;
    private readonly int[] _blocks;

    private TextDiff(DiffRow[] rows, int oldLineCount, int newLineCount, bool exact)
    {
        _rows = rows;
        OldLineCount = oldLineCount; NewLineCount = newLineCount; IsExact = exact;
        var blocks = new List<int>();
        for (int i = 0; i < rows.Length; i++)
        {
            if (!rows[i].IsChange) continue;
            if (i == 0 || !rows[i - 1].IsChange) blocks.Add(i);
            if (rows[i].OldLine is not null && rows[i].Kind != DiffRowKind.Unchanged) RemovedLines++;
            if (rows[i].NewLine is not null && rows[i].Kind != DiffRowKind.Unchanged) AddedLines++;
        }
        _blocks = [.. blocks];
    }

    public IReadOnlyList<DiffRow> Rows => _rows;
    /// <summary>Row index where each contiguous change block starts.</summary>
    public IReadOnlyList<int> ChangeBlocks => _blocks;
    public int AddedLines { get; }
    public int RemovedLines { get; }
    public int OldLineCount { get; }
    public int NewLineCount { get; }
    /// <summary>False when the edit distance exceeded the search budget and the middle was diffed as one replacement.</summary>
    public bool IsExact { get; }
    public bool IsIdentical => _blocks.Length == 0;

    public static TextDiff Compute(string? before, string? after, int maxEditDistance = 3000)
    {
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string line) { if (!ids.TryGetValue(line, out var id)) ids[line] = id = ids.Count; return id; }
        var a = oldLines.Select(Id).ToArray();
        var b = newLines.Select(Id).ToArray();
        var ops = Myers.Diff(a, b, maxEditDistance, out bool exact);
        var rows = new List<DiffRow>(Math.Max(a.Length, b.Length) + 8);
        var deleted = new List<int>();
        var inserted = new List<int>();
        void Flush()
        {
            int paired = Math.Min(deleted.Count, inserted.Count);
            for (int i = 0; i < paired; i++)
            {
                var (oldText, newText) = (oldLines[deleted[i]], newLines[inserted[i]]);
                var (oldSpans, newSpans) = WordSpans(oldText, newText);
                rows.Add(new(DiffRowKind.Modified, deleted[i] + 1, inserted[i] + 1, oldText, newText, oldSpans, newSpans));
            }
            for (int i = paired; i < deleted.Count; i++)
                rows.Add(new(DiffRowKind.Removed, deleted[i] + 1, null, oldLines[deleted[i]], "", [], []));
            for (int i = paired; i < inserted.Count; i++)
                rows.Add(new(DiffRowKind.Added, null, inserted[i] + 1, "", newLines[inserted[i]], [], []));
            deleted.Clear(); inserted.Clear();
        }
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case Myers.OpKind.Equal:
                    Flush();
                    rows.Add(new(DiffRowKind.Unchanged, op.OldIndex + 1, op.NewIndex + 1, oldLines[op.OldIndex], newLines[op.NewIndex], [], []));
                    break;
                case Myers.OpKind.Delete: deleted.Add(op.OldIndex); break;
                default: inserted.Add(op.NewIndex); break;
            }
        }
        Flush();
        return new([.. rows], oldLines.Length, newLines.Length, exact);
    }

    /// <summary>Groups change blocks with <paramref name="context"/> unchanged rows around them. Small gaps are merged.</summary>
    public IReadOnlyList<DiffHunk> Hunks(int context = 3)
    {
        context = Math.Max(0, context);
        var hunks = new List<DiffHunk>();
        foreach (var start in _blocks)
        {
            int end = start;
            while (end < _rows.Length && _rows[end].IsChange) end++;
            var hunk = new DiffHunk(Math.Max(0, start - context), Math.Min(_rows.Length, end + context));
            // A collapsed separator for a couple of lines hides less than it costs to read.
            if (hunks.Count > 0 && hunk.Start - hunks[^1].End < 3) hunks[^1] = new(hunks[^1].Start, hunk.End);
            else hunks.Add(hunk);
        }
        return hunks;
    }

    /// <summary>Splits text into lines without terminators. A final line terminator does not create an empty line.</summary>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Split('\n');
        int count = lines.Length;
        if (lines[^1].Length == 0) count--;
        var result = new string[count];
        for (int i = 0; i < count; i++) result[i] = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
        return result;
    }

    /// <summary>Word-level changed ranges for a paired line; empty when the lines are too different to align usefully.</summary>
    public static (IReadOnlyList<DiffSpan> Old, IReadOnlyList<DiffSpan> New) WordSpans(string oldText, string newText)
    {
        if (oldText == newText) return ([], []);
        if (oldText.Length > WordDiffMaxLength || newText.Length > WordDiffMaxLength) return ([], []);
        var oldTokens = Tokens(oldText);
        var newTokens = Tokens(newText);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string token) { if (!ids.TryGetValue(token, out var id)) ids[token] = id = ids.Count; return id; }
        var ops = Myers.Diff(oldTokens.Select(t => Id(t.Text)).ToArray(), newTokens.Select(t => Id(t.Text)).ToArray(), 400, out _);
        var oldSpans = new List<DiffSpan>();
        var newSpans = new List<DiffSpan>();
        int common = 0;
        foreach (var op in ops)
        {
            if (op.Kind == Myers.OpKind.Equal) common += oldTokens[op.OldIndex].Text.Length;
            else if (op.Kind == Myers.OpKind.Delete) Add(oldSpans, oldTokens[op.OldIndex]);
            else Add(newSpans, newTokens[op.NewIndex]);
        }
        // Whitespace-only differences still deserve a visible mark; otherwise require real overlap.
        double similarity = 2.0 * common / Math.Max(1, oldText.Length + newText.Length);
        if (similarity < WordDiffSimilarity) return ([], []);
        return (oldSpans, newSpans);

        static void Add(List<DiffSpan> spans, (string Text, int Start) token)
        {
            if (spans.Count > 0 && spans[^1].Start + spans[^1].Length == token.Start)
                spans[^1] = new(spans[^1].Start, spans[^1].Length + token.Text.Length);
            else spans.Add(new(token.Start, token.Text.Length));
        }
    }

    private static List<(string Text, int Start)> Tokens(string text)
    {
        var tokens = new List<(string, int)>();
        foreach (Match match in TokenPattern().Matches(text)) tokens.Add((match.Value, match.Index));
        return tokens;
    }

    [GeneratedRegex(@"\w+|\s+|[^\w\s]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

/// <summary>Myers' O((N+M)D) difference algorithm with common prefix/suffix trimming and a bounded search.</summary>
internal static class Myers
{
    public enum OpKind : byte { Equal, Delete, Insert }
    public readonly record struct Op(OpKind Kind, int OldIndex, int NewIndex);

    public static List<Op> Diff(int[] a, int[] b, int maxEditDistance, out bool exact)
    {
        exact = true;
        var ops = new List<Op>(a.Length + b.Length);
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        for (int i = 0; i < prefix; i++) ops.Add(new(OpKind.Equal, i, i));
        int n = a.Length - prefix - suffix, m = b.Length - prefix - suffix;
        var middle = Middle(a, b, prefix, n, m, maxEditDistance);
        if (middle is null)
        {
            // The texts are too different for the search budget: show the middle as one replacement.
            exact = false;
            for (int i = 0; i < n; i++) ops.Add(new(OpKind.Delete, prefix + i, -1));
            for (int j = 0; j < m; j++) ops.Add(new(OpKind.Insert, -1, prefix + j));
        }
        else ops.AddRange(middle);
        for (int i = 0; i < suffix; i++) ops.Add(new(OpKind.Equal, a.Length - suffix + i, b.Length - suffix + i));
        return ops;
    }

    private static List<Op>? Middle(int[] a, int[] b, int offset, int n, int m, int maxEditDistance)
    {
        var result = new List<Op>();
        if (n == 0 && m == 0) return result;
        if (n == 0) { for (int j = 0; j < m; j++) result.Add(new(OpKind.Insert, -1, offset + j)); return result; }
        if (m == 0) { for (int i = 0; i < n; i++) result.Add(new(OpKind.Delete, offset + i, -1)); return result; }
        int max = n + m;
        int limit = Math.Min(max, Math.Max(1, maxEditDistance));
        var v = new int[2 * max + 3];
        int center = max + 1;
        var trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= limit && found < 0; d++)
        {
            // Snapshot k in [-d, d] of the previous iteration; that is all backtracking reads.
            var snapshot = new int[2 * d + 1];
            Array.Copy(v, center - d, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[center + k - 1] < v[center + k + 1]) ? v[center + k + 1] : v[center + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[offset + x] == b[offset + y]) { x++; y++; }
                v[center + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0) return null;
        int cx = n, cy = m;
        for (int d = found; d >= 0; d--)
        {
            var previous = trace[d];
            int At(int k) => previous[k + d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
            int prevX = d == 0 ? 0 : At(prevK);
            int prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { cx--; cy--; result.Add(new(OpKind.Equal, offset + cx, offset + cy)); }
            if (d > 0)
            {
                if (cx == prevX) result.Add(new(OpKind.Insert, -1, offset + prevY));
                else result.Add(new(OpKind.Delete, offset + prevX, -1));
            }
            cx = prevX; cy = prevY;
        }
        result.Reverse();
        return result;
    }
}
