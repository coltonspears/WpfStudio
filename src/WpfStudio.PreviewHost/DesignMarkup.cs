namespace WpfStudio.PreviewHost;

/// <summary>
/// One markup extension as it is written in an attribute: its name and its arguments, unevaluated. The preview
/// reads design-time declarations such as <c>{d:DesignInstance Type=vm:Orders, IsDesignTimeCreatable=True}</c>
/// itself, because no runtime type stands behind the design namespace for the XAML loader to construct.
/// </summary>
internal sealed class DesignMarkup
{
    private DesignMarkup(string? prefix, string name, List<string> positional, Dictionary<string, string> named)
    {
        Prefix = prefix; Name = name; Positional = positional; Named = named;
    }

    /// <summary>The XML prefix before the extension's name, or null for the default namespace.</summary>
    public string? Prefix { get; }
    /// <summary>The name as written, without its prefix and without an "Extension" suffix.</summary>
    public string Name { get; }
    public IReadOnlyList<string> Positional { get; }
    public IReadOnlyDictionary<string, string> Named { get; }

    /// <summary>The named argument, or the positional one at <paramref name="position"/> when the name was left out.</summary>
    public string? Argument(string name, int position = -1) =>
        Named.TryGetValue(name, out string? value) ? value : position >= 0 && position < Positional.Count ? Positional[position] : null;

    /// <summary>
    /// Reads <c>{prefix:Name positional, Key=Value}</c>. A nested extension or quoted text stays whole inside its
    /// argument. Returns null for a literal, an escaped literal (<c>{}text</c>) and anything unbalanced.
    /// </summary>
    public static DesignMarkup? Parse(string? text)
    {
        text = text?.Trim();
        if (text is null || text.Length < 3 || text[0] != '{' || text[text.Length - 1] != '}' || text[1] == '}') return null;
        string body = text.Substring(1, text.Length - 2);
        int nameEnd = 0;
        while (nameEnd < body.Length && !char.IsWhiteSpace(body[nameEnd]) && body[nameEnd] != ',') nameEnd++;
        string qualified = body.Substring(0, nameEnd);
        if (qualified.Length == 0 || qualified.IndexOfAny(['{', '}', '=', '\'', '"']) >= 0) return null;
        int colon = qualified.IndexOf(':');
        string? prefix = colon < 0 ? null : qualified.Substring(0, colon);
        string name = qualified.Substring(colon + 1);
        if (name.Length == 0 || prefix is { Length: 0 }) return null;
        if (name.Length > 9 && name.EndsWith("Extension", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 9);

        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Split(body.Substring(nameEnd)) is not { } arguments) return null;
        foreach (string argument in arguments)
        {
            int equals = TopLevelEquals(argument);
            string key = equals > 0 ? argument.Substring(0, equals).Trim() : "";
            if (equals > 0 && IsMemberName(key))
            {
                if (named.ContainsKey(key)) return null;
                named[key] = Unquote(argument.Substring(equals + 1));
            }
            else positional.Add(Unquote(argument));
        }
        return new DesignMarkup(prefix, name, positional, named);
    }

    /// <summary>Arguments separated by commas outside nested braces and quotes, or null when braces or quotes do not balance.</summary>
    private static List<string>? Split(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\\' && index + 1 < text.Length) { index++; continue; }
            if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
            if (character is '\'' or '"') quote = character;
            else if (character == '{') depth++;
            else if (character == '}') { if (--depth < 0) return null; }
            else if (character == ',' && depth == 0)
            {
                parts.Add(text.Substring(start, index - start));
                start = index + 1;
            }
        }
        if (depth != 0 || quote != '\0') return null;
        parts.Add(text.Substring(start));
        // "{Name}" has no arguments at all; an empty one between commas is a mistake.
        if (parts.Count == 1 && string.IsNullOrWhiteSpace(parts[0])) return [];
        return parts.Any(string.IsNullOrWhiteSpace) ? null : parts;
    }

    private static int TopLevelEquals(string argument)
    {
        for (int index = 0; index < argument.Length; index++)
        {
            char character = argument[index];
            if (character == '=') return index;
            // A value that starts quoted or nested is positional even when it contains '='.
            if (character is '{' or '\'' or '"' or '\\') return -1;
        }
        return -1;
    }

    private static bool IsMemberName(string text) =>
        text.Length > 0 && text.All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or ':');

    private static string Unquote(string value)
    {
        value = value.Trim();
        bool quoted = value.Length >= 2 && value[0] is '\'' or '"' && value[value.Length - 1] == value[0];
        if (quoted) value = value.Substring(1, value.Length - 2);
        // A nested extension is read again by whoever evaluates it, escapes included.
        if (value.IndexOf('\\') < 0 || value.StartsWith('{')) return value;
        var text = new System.Text.StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length) index++;
            text.Append(value[index]);
        }
        return text.ToString();
    }
}
