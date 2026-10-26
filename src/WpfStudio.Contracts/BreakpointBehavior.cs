using System.Text;

namespace WpfStudio.Contracts;

public enum HitConditionKind { Always, Equal, AtLeast, Multiple }

/// <summary>
/// When a breakpoint breaks, by its hit count: <c>5</c> (or <c>=5</c>) breaks on the fifth hit, <c>&gt;=5</c> on every
/// hit from the fifth, and <c>%5</c> on every fifth hit. Hits are counted after any condition is true.
/// </summary>
public readonly record struct HitCondition(HitConditionKind Kind, int Count)
{
    public static HitCondition Always => new(HitConditionKind.Always, 0);

    public static bool TryParse(string? text, out HitCondition condition, out string? error)
    {
        condition = Always; error = null;
        var value = (text ?? "").Replace(" ", "", StringComparison.Ordinal);
        if (value.Length == 0) return true;
        var (kind, digits) = value switch
        {
            _ when value.StartsWith(">=", StringComparison.Ordinal) => (HitConditionKind.AtLeast, value[2..]),
            _ when value.StartsWith("==", StringComparison.Ordinal) => (HitConditionKind.Equal, value[2..]),
            _ when value.StartsWith('=') => (HitConditionKind.Equal, value[1..]),
            _ when value.StartsWith('%') => (HitConditionKind.Multiple, value[1..]),
            _ => (HitConditionKind.Equal, value)
        };
        if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 1)
        {
            error = "Enter a hit count such as 5 (fifth hit), >=5 (from the fifth hit) or %5 (every fifth hit).";
            return false;
        }
        condition = new(kind, count);
        return true;
    }

    public bool ShouldBreak(int hits) => Kind switch
    {
        HitConditionKind.Equal => hits == Count,
        HitConditionKind.AtLeast => hits >= Count,
        HitConditionKind.Multiple => hits % Count == 0,
        _ => true
    };

    public string Describe() => Kind switch
    {
        HitConditionKind.Equal => $"when hit count is {Count}",
        HitConditionKind.AtLeast => $"when hit count is {Count} or more",
        HitConditionKind.Multiple => $"every {Count} hits",
        _ => "every hit"
    };
}

/// <summary>
/// A tracepoint message: literal text with <c>{expression}</c> holes evaluated in the stopped frame, plus keywords
/// such as <c>$FUNCTION</c> and <c>$TID</c>. <c>{{</c> and <c>}}</c> write literal braces.
/// </summary>
public static class TracepointMessage
{
    public sealed record Segment(bool IsExpression, string Text);

    public static IReadOnlyList<Segment> Parse(string message)
    {
        var segments = new List<Segment>();
        var literal = new StringBuilder();
        for (var i = 0; i < message.Length; i++)
        {
            var c = message[i];
            if (c == '{' && i + 1 < message.Length && message[i + 1] == '{') { literal.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < message.Length && message[i + 1] == '}') { literal.Append('}'); i++; continue; }
            if (c != '{') { literal.Append(c); continue; }
            var end = FindClose(message, i + 1);
            if (end < 0) { literal.Append(message, i, message.Length - i); break; } // An unclosed hole stays literal.
            if (literal.Length > 0) { segments.Add(new(false, literal.ToString())); literal.Clear(); }
            var expression = message[(i + 1)..end].Trim();
            if (expression.Length > 0) segments.Add(new(true, expression));
            i = end;
        }
        if (literal.Length > 0) segments.Add(new(false, literal.ToString()));
        return segments;
    }

    // Nested braces belong to the expression, for example {new[] { 1, 2 }.Length}.
    private static int FindClose(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && depth-- == 0) return i;
        }
        return -1;
    }

    /// <summary>Expands the message. A failed expression writes its error in angle brackets and does not stop the others.</summary>
    public static async Task<string> FormatAsync(string message, Func<string, Task<string>> evaluate, IReadOnlyDictionary<string, string>? keywords = null)
    {
        var output = new StringBuilder();
        foreach (var segment in Parse(message))
        {
            if (!segment.IsExpression) { output.Append(ReplaceKeywords(segment.Text, keywords)); continue; }
            try { output.Append(await evaluate(segment.Text).ConfigureAwait(false)); }
            catch (Exception exception) when (exception is not OperationCanceledException) { output.Append('<').Append(exception.Message).Append('>'); }
        }
        return output.ToString();
    }

    private static string ReplaceKeywords(string text, IReadOnlyDictionary<string, string>? keywords)
    {
        if (keywords is null || !text.Contains('$')) return text;
        // Longest first, so $FUNCTION is not mistaken for a shorter keyword prefix.
        foreach (var (name, value) in keywords.OrderByDescending(pair => pair.Key.Length))
            text = text.Replace("$" + name, value, StringComparison.Ordinal);
        return text;
    }
}
