namespace WpfStudio.Core.Text;

/// <summary>Which parts of a completion's name match what has been typed, so the list can show why an entry is there.</summary>
public static class CompletionMatch
{
    /// <summary>The typed text where it appears whole; otherwise each of its letters in order, as "OFD" matches OpenFileDialog. Empty if neither fits.</summary>
    public static IReadOnlyList<(int Start, int Length)> Ranges(string text, string query)
    {
        if (query.Length == 0) return [];
        int whole = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (whole >= 0) return [(whole, query.Length)];
        var ranges = new List<(int Start, int Length)>();
        int next = 0;
        foreach (char letter in query)
        {
            int found = next < text.Length ? text.IndexOf(letter.ToString(), next, StringComparison.OrdinalIgnoreCase) : -1;
            if (found < 0) return [];
            if (ranges.Count > 0 && ranges[^1].Start + ranges[^1].Length == found) ranges[^1] = (ranges[^1].Start, ranges[^1].Length + 1);
            else ranges.Add((found, 1));
            next = found + 1;
        }
        return ranges;
    }
}
