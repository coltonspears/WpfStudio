using System.Text;

namespace WpfStudio.Core.Git;

public enum ConflictChoice { Unresolved, Current, Incoming, CurrentThenIncoming, IncomingThenCurrent, Base }

/// <summary>
/// One conflict between Git's markers. Offsets cover the whole block, markers included. <see cref="Base"/> is
/// present when the file uses the diff3 or zdiff3 conflict style.
/// </summary>
public sealed record ConflictBlock(int Start, int End, string CurrentLabel, string Current, string IncomingLabel, string Incoming, string? Base)
{
    public string Resolve(ConflictChoice choice) => choice switch
    {
        ConflictChoice.Current => Current,
        ConflictChoice.Incoming => Incoming,
        ConflictChoice.CurrentThenIncoming => Current + Incoming,
        ConflictChoice.IncomingThenCurrent => Incoming + Current,
        ConflictChoice.Base => Base ?? throw new InvalidOperationException("This conflict has no base version. Set merge.conflictStyle to diff3 to include it."),
        _ => throw new InvalidOperationException("Choose a resolution for every conflict.")
    };
}

public static class MergeConflicts
{
    /// <summary>Finds complete conflict blocks. Marker lines must start at a line start and be exactly seven characters.</summary>
    public static IReadOnlyList<ConflictBlock> Parse(string text)
    {
        var blocks = new List<ConflictBlock>();
        var position = 0;
        while (position < text.Length)
        {
            var start = FindMarker(text, "<<<<<<<", position);
            if (start < 0) break;
            var currentLabel = MarkerLabel(text, start, out var currentStart);
            var baseMarker = FindMarker(text, "|||||||", currentStart);
            var separator = FindMarker(text, "=======", currentStart);
            var end = separator < 0 ? -1 : FindMarker(text, ">>>>>>>", separator);
            var nested = FindMarker(text, "<<<<<<<", currentStart);
            if (separator < 0 || end < 0 || nested >= 0 && nested < end) { position = currentStart; continue; } // Incomplete or nested: skip.
            string? baseText = null;
            var currentEnd = separator;
            if (baseMarker >= 0 && baseMarker < separator)
            {
                currentEnd = baseMarker;
                MarkerLabel(text, baseMarker, out var baseStart);
                baseText = text[baseStart..separator];
            }
            MarkerLabel(text, separator, out var incomingStart);
            var incomingLabel = MarkerLabel(text, end, out var blockEnd);
            blocks.Add(new(start, blockEnd, currentLabel, text[currentStart..currentEnd], incomingLabel, text[incomingStart..end], baseText));
            position = blockEnd;
        }
        return blocks;
    }

    /// <summary>Replaces every block with its chosen side. Every block must be resolved.</summary>
    public static string Apply(string text, IReadOnlyList<ConflictBlock> blocks, IReadOnlyList<ConflictChoice> choices)
    {
        if (blocks.Count != choices.Count) throw new ArgumentException("Each conflict needs one choice.", nameof(choices));
        var output = new StringBuilder(text.Length);
        var cursor = 0;
        for (var index = 0; index < blocks.Count; index++)
        {
            output.Append(text, cursor, blocks[index].Start - cursor);
            output.Append(blocks[index].Resolve(choices[index]));
            cursor = blocks[index].End;
        }
        return output.Append(text, cursor, text.Length - cursor).ToString();
    }

    private static int FindMarker(string text, string marker, int from)
    {
        for (var index = text.IndexOf(marker, from, StringComparison.Ordinal); index >= 0; index = text.IndexOf(marker, index + 1, StringComparison.Ordinal))
        {
            var atLineStart = index == 0 || text[index - 1] == '\n';
            var after = index + marker.Length;
            var exact = after >= text.Length || text[after] is ' ' or '\r' or '\n';
            if (atLineStart && exact) return index;
        }
        return -1;
    }

    // Returns the text after the marker on its line, and the offset of the next line.
    private static string MarkerLabel(string text, int marker, out int nextLine)
    {
        var lineEnd = text.IndexOf('\n', marker);
        nextLine = lineEnd < 0 ? text.Length : lineEnd + 1;
        var label = text[(marker + 7)..(lineEnd < 0 ? text.Length : lineEnd)];
        return label.Trim();
    }
}
