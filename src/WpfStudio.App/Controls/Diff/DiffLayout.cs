using WpfStudio.Core.Text;

namespace WpfStudio.App.Controls.Diff;

public enum DiffLineKind { Context, Removed, Added, Filler, Separator }

/// <summary>
/// One displayed editor line. Fillers keep the two sides of a side-by-side diff aligned; a separator stands for
/// the collapsed unchanged rows <c>[GapStart, GapEnd)</c> and expands when clicked.
/// </summary>
public sealed record DiffDisplayLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text, IReadOnlyList<DiffSpan> Spans,
    int GapStart = -1, int GapEnd = -1)
{
    public int HiddenRows => Math.Max(0, GapEnd - GapStart);
    public bool IsChange => Kind is DiffLineKind.Removed or DiffLineKind.Added;
}

/// <summary>Display lines for the side-by-side and inline presentations of one <see cref="TextDiff"/>.</summary>
public sealed class DiffLayout
{
    private DiffLayout(IReadOnlyList<DiffDisplayLine> left, IReadOnlyList<DiffDisplayLine> right, IReadOnlyList<DiffDisplayLine> inline,
        IReadOnlyList<int> sideBySideChanges, IReadOnlyList<int> inlineChanges)
    {
        Left = left; Right = right; Inline = inline; SideBySideChanges = sideBySideChanges; InlineChanges = inlineChanges;
    }

    public IReadOnlyList<DiffDisplayLine> Left { get; }
    public IReadOnlyList<DiffDisplayLine> Right { get; }
    public IReadOnlyList<DiffDisplayLine> Inline { get; }
    /// <summary>Display line index where each change block starts, per presentation.</summary>
    public IReadOnlyList<int> SideBySideChanges { get; }
    public IReadOnlyList<int> InlineChanges { get; }

    /// <summary>
    /// Builds both presentations. Unless <paramref name="wholeFile"/> is set, unchanged rows further than
    /// <paramref name="context"/> from a change are collapsed into separators; <paramref name="expandedGaps"/>
    /// holds the <see cref="DiffDisplayLine.GapStart"/> of separators the user opened.
    /// </summary>
    public static DiffLayout Build(TextDiff diff, bool wholeFile, IReadOnlySet<int>? expandedGaps = null, int context = 3)
    {
        var rows = diff.Rows;
        var visible = new bool[rows.Count];
        if (wholeFile || diff.IsIdentical) Array.Fill(visible, true);
        else foreach (var hunk in diff.Hunks(context)) for (int i = hunk.Start; i < hunk.End; i++) visible[i] = true;
        // Opened separators stay open: mark their rows visible before building either presentation.
        for (int i = 0; i < rows.Count;)
        {
            if (visible[i]) { i++; continue; }
            int end = i;
            while (end < rows.Count && !visible[end]) end++;
            if (expandedGaps?.Contains(i) == true) for (int j = i; j < end; j++) visible[j] = true;
            i = end;
        }

        var left = new List<DiffDisplayLine>(rows.Count);
        var right = new List<DiffDisplayLine>(rows.Count);
        var sideChanges = new List<int>();
        for (int i = 0; i < rows.Count;)
        {
            if (!visible[i])
            {
                int end = i;
                while (end < rows.Count && !visible[end]) end++;
                var separator = new DiffDisplayLine(DiffLineKind.Separator, null, null, "", [], i, end);
                left.Add(separator); right.Add(separator);
                i = end;
                continue;
            }
            var row = rows[i];
            if (row.IsChange && (i == 0 || !rows[i - 1].IsChange)) sideChanges.Add(left.Count);
            switch (row.Kind)
            {
                case DiffRowKind.Unchanged:
                    left.Add(new(DiffLineKind.Context, row.OldLine, null, row.OldText, []));
                    right.Add(new(DiffLineKind.Context, null, row.NewLine, row.NewText, []));
                    break;
                case DiffRowKind.Modified:
                    left.Add(new(DiffLineKind.Removed, row.OldLine, null, row.OldText, row.OldSpans));
                    right.Add(new(DiffLineKind.Added, null, row.NewLine, row.NewText, row.NewSpans));
                    break;
                case DiffRowKind.Removed:
                    left.Add(new(DiffLineKind.Removed, row.OldLine, null, row.OldText, []));
                    right.Add(new(DiffLineKind.Filler, null, null, "", []));
                    break;
                default:
                    left.Add(new(DiffLineKind.Filler, null, null, "", []));
                    right.Add(new(DiffLineKind.Added, null, row.NewLine, row.NewText, []));
                    break;
            }
            i++;
        }
        var inline = new List<DiffDisplayLine>(rows.Count + diff.AddedLines);
        var inlineChanges = new List<int>();
        BuildInline(rows, visible, inline, inlineChanges);
        return new(left, right, inline, sideChanges, inlineChanges);
    }

    /// <summary>Inline lists a change block's removed lines before its added lines, like a unified patch.</summary>
    private static void BuildInline(IReadOnlyList<DiffRow> rows, bool[] visible, List<DiffDisplayLine> inline, List<int> changes)
    {
        for (int i = 0; i < rows.Count;)
        {
            if (!visible[i])
            {
                int end = i;
                while (end < rows.Count && !visible[end]) end++;
                inline.Add(new(DiffLineKind.Separator, null, null, "", [], i, end));
                i = end;
                continue;
            }
            if (!rows[i].IsChange)
            {
                inline.Add(new(DiffLineKind.Context, rows[i].OldLine, rows[i].NewLine, rows[i].NewText, []));
                i++;
                continue;
            }
            changes.Add(inline.Count);
            int blockEnd = i;
            while (blockEnd < rows.Count && rows[blockEnd].IsChange && visible[blockEnd]) blockEnd++;
            for (int j = i; j < blockEnd; j++)
                if (rows[j].OldLine is not null) inline.Add(new(DiffLineKind.Removed, rows[j].OldLine, null, rows[j].OldText, rows[j].OldSpans));
            for (int j = i; j < blockEnd; j++)
                if (rows[j].NewLine is not null) inline.Add(new(DiffLineKind.Added, null, rows[j].NewLine, rows[j].NewText, rows[j].NewSpans));
            i = blockEnd;
        }
    }
}
