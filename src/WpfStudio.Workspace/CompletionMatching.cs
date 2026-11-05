using Microsoft.CodeAnalysis.Completion;

namespace WpfStudio.Workspace;

/// <summary>
/// Which completion entries answer what has been typed. An entry matches by its start, by its initials ("OFD" for
/// OpenFileDialog), by the start of one of its words ("Dialog") or, from three letters on, by containing the text. When
/// more match than one reply carries, the closest matches are the ones sent, in the list's own order, and the reply says
/// it is incomplete.
/// </summary>
internal static class CompletionMatching
{
    public static (IReadOnlyList<CompletionItem> Items, bool Incomplete) Select(IReadOnlyList<CompletionItem> items, string typed, int limit)
    {
        if (typed.Length == 0) return (items.Count <= limit ? items : items.Take(limit).ToArray(), items.Count > limit);
        var matched = new List<(CompletionItem Item, int Index, int Quality)>();
        for (int index = 0; index < items.Count; index++)
            if (Quality(items[index].FilterText, typed) is var quality and > 0) matched.Add((items[index], index, quality));
        if (matched.Count <= limit) return (matched.Select(match => match.Item).ToArray(), false);
        // An entry that still needs a using gives way to one already in scope.
        return (matched.OrderByDescending(match => match.Quality).ThenBy(match => string.IsNullOrEmpty(match.Item.InlineDescription) ? 0 : 1).ThenBy(match => match.Index)
            .Take(limit).OrderBy(match => match.Index).Select(match => match.Item).ToArray(), true);
    }

    /// <summary>
    /// 4 starts with the text, 3 starts with it in another case, 2 has it as initials or as the start of one of its words,
    /// 1 contains it elsewhere, 0 does not match. One or two letters only match where a word starts: "o" is not a
    /// reason to offer every name with an o in it.
    /// </summary>
    internal static int Quality(string text, string typed)
    {
        if (text.StartsWith(typed, StringComparison.Ordinal)) return 4;
        if (text.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 3;
        if (HasInitials(text, typed)) return 2;
        bool inside = false;
        for (int at = text.IndexOf(typed, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(typed, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (StartsWord(text, at)) return 2;
            inside = true;
        }
        return inside && typed.Length >= 3 ? 1 : 0;
    }

    private static bool StartsWord(string text, int at) =>
        at == 0 || char.IsUpper(text[at]) && !char.IsUpper(text[at - 1]) || !char.IsLetterOrDigit(text[at - 1]) || char.IsDigit(text[at - 1]) != char.IsDigit(text[at]);

    // The first letter and each later capital, in order.
    private static bool HasInitials(string text, string typed)
    {
        int matched = 0;
        for (int index = 0; index < text.Length && matched < typed.Length; index++)
        {
            if (index > 0 && !char.IsUpper(text[index])) continue;
            if (char.ToUpperInvariant(text[index]) != char.ToUpperInvariant(typed[matched])) return false;
            matched++;
        }
        return matched == typed.Length;
    }
}
