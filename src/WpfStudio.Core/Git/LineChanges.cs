using WpfStudio.Core.Text;

namespace WpfStudio.Core.Git;

public enum LineChangeKind { Added, Modified, Deleted }

/// <summary>
/// A changed block for the editor's change margin. <see cref="StartLine"/> is the first 1-based line in the
/// current text; a deletion has no lines and marks the boundary before <see cref="StartLine"/>.
/// <see cref="OriginalText"/> is what the block replaced, so it can be shown or restored.
/// </summary>
public sealed record LineChange(LineChangeKind Kind, int StartLine, int LineCount, string OriginalText);

public static class LineChanges
{
    /// <summary>Above this many lines on either side, the margin is left empty rather than slowing typing.</summary>
    public const int MaximumLines = 30_000;

    public static IReadOnlyList<LineChange> Compute(string? original, string current)
    {
        if (original is null) // Untracked: the whole file is new.
            return current.Length == 0 ? [] : [new(LineChangeKind.Added, 1, TextDiff.SplitLines(current).Length, "")];
        var originalLines = TextDiff.SplitLines(original);
        var currentLines = TextDiff.SplitLines(current);
        if (originalLines.Length > MaximumLines || currentLines.Length > MaximumLines) return [];
        // The margin marks whole lines, and this runs after every pause in typing: the word-level comparison of each modified line would be thrown away.
        var diff = TextDiff.Compute(original, current, wordSpans: false);
        var changes = new List<LineChange>();
        var rows = diff.Rows;
        var nextNewLine = 1;
        for (var index = 0; index < rows.Count;)
        {
            var row = rows[index];
            if (!row.IsChange) { nextNewLine = (row.NewLine ?? nextNewLine - 1) + 1; index++; continue; }
            var removed = new List<string>();
            int added = 0, firstNew = -1;
            while (index < rows.Count && rows[index].IsChange)
            {
                var change = rows[index++];
                if (change.OldLine is not null) removed.Add(change.OldText);
                if (change.NewLine is { } newLine) { added++; if (firstNew < 0) firstNew = newLine; nextNewLine = newLine + 1; }
            }
            var originalText = string.Join("\n", removed);
            if (added == 0) changes.Add(new(LineChangeKind.Deleted, nextNewLine, 0, originalText));
            else changes.Add(new(removed.Count == 0 ? LineChangeKind.Added : LineChangeKind.Modified, firstNew, added, originalText));
        }
        return changes;
    }

    /// <summary>
    /// The changes shown at a line of a text with <paramref name="lineCount"/> lines, in <see cref="Compute"/>'s line order.
    /// A deletion past the end of the text shows on the last line. Found by binary search, since a margin asks for every visible line.
    /// </summary>
    public static IEnumerable<LineChange> At(IReadOnlyList<LineChange> changes, int line, int lineCount)
    {
        int low = 0, high = changes.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (changes[middle].StartLine + Math.Max(changes[middle].LineCount, 1) <= line) low = middle + 1;
            else high = middle;
        }
        for (var index = low; index < changes.Count; index++)
        {
            var change = changes[index];
            var first = change.Kind == LineChangeKind.Deleted ? Math.Min(change.StartLine, lineCount) : change.StartLine;
            if (first > line) yield break;
            if (change.Kind == LineChangeKind.Deleted ? first == line : line < change.StartLine + change.LineCount) yield return change;
        }
    }
}
