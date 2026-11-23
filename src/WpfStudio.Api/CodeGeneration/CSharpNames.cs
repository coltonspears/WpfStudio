using System.Globalization;
using System.Text;

namespace WpfStudio.Api.CodeGeneration;

/// <summary>Turns JSON names into C# identifiers.</summary>
public static class CSharpNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default",
        "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    /// <summary><c>user_id</c>, <c>userId</c> and <c>user-id</c> all become <c>UserId</c>; <c>HTTPStatus</c> becomes <c>HttpStatus</c>.</summary>
    public static string Pascal(string text, string fallback = "Value")
    {
        var builder = new StringBuilder();
        foreach (var word in Words(text))
        {
            var allUpper = word.All(c => !char.IsLetter(c) || char.IsUpper(c));
            builder.Append(char.ToUpperInvariant(word[0]));
            builder.Append(allUpper ? word[1..].ToLowerInvariant() : word[1..]);
        }
        var result = builder.ToString();
        if (result.Length == 0) return fallback;
        return char.IsDigit(result[0]) ? "The" + result : result;
    }

    /// <summary>A name kept as close to the JSON as C# allows: invalid characters become underscores, and keywords get an <c>@</c>.</summary>
    public static string Verbatim(string text, string fallback = "value")
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        var result = builder.ToString();
        if (result.Length == 0 || result.All(c => c == '_')) return fallback;
        if (char.IsDigit(result[0])) result = "_" + result;
        return Keywords.Contains(result) ? "@" + result : result;
    }

    public static bool IsKeyword(string name) => Keywords.Contains(name);

    /// <summary>A reasonable singular for naming the items of an array property: <c>categories</c> → <c>category</c>.</summary>
    public static string Singular(string word)
    {
        if (word.Length < 3) return word;
        if (word.EndsWith("ies", StringComparison.OrdinalIgnoreCase)) return word[..^3] + (char.IsUpper(word[^1]) ? "Y" : "y");
        if (word.EndsWith("sses", StringComparison.OrdinalIgnoreCase) || word.EndsWith("xes", StringComparison.OrdinalIgnoreCase) || word.EndsWith("ches", StringComparison.OrdinalIgnoreCase) || word.EndsWith("shes", StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("uses", StringComparison.OrdinalIgnoreCase)) return word[..^2];
        if (word.EndsWith("ss", StringComparison.OrdinalIgnoreCase) || word.EndsWith("us", StringComparison.OrdinalIgnoreCase) || word.EndsWith("is", StringComparison.OrdinalIgnoreCase)) return word;
        if (word.EndsWith('s') || word.EndsWith('S')) return word[..^1];
        return word;
    }

    internal static IEnumerable<string> Words(string text)
    {
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            if (current.Length > 0)
            {
                var previous = text[i - 1];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                // userName → user|Name; HTTPServer → HTTP|Server.
                var boundary = (char.IsLower(previous) || char.IsDigit(previous)) && char.IsUpper(c)
                    || char.IsUpper(previous) && char.IsUpper(c) && char.IsLower(next);
                if (boundary) { yield return current.ToString(); current.Clear(); }
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>A C# string literal.</summary>
    public static string Literal(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\0': builder.Append("\\0"); break;
                default:
                    if (char.IsControl(c) || c is '\u2028' or '\u2029') builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>Text for an XML documentation comment.</summary>
    public static string XmlText(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>Hands out identifiers that are unique within one scope, numbering repeats.</summary>
internal sealed class NameScope(IEnumerable<string>? reserved = null)
{
    private readonly HashSet<string> _taken = new(reserved ?? [], StringComparer.Ordinal);

    public string Claim(string name)
    {
        var candidate = name;
        for (var n = 2; !_taken.Add(candidate); n++) candidate = name + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    public bool IsTaken(string name) => _taken.Contains(name);
}
