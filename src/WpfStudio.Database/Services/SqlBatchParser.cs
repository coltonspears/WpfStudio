using System.Text;
using System.Text.RegularExpressions;
using WpfStudio.Database.Models;

namespace WpfStudio.Database.Services;

public sealed class SqlScriptException(string message, int line) : FormatException($"Line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>Lexes batch separators without interpreting GO inside comments, identifiers, or string literals.</summary>
public static partial class SqlBatchParser
{
    private enum LexState { Normal, String, Identifier, QuotedIdentifier, BlockComment }

    public static IReadOnlyList<SqlBatch> Parse(string script, int firstLine = 1)
    {
        ArgumentNullException.ThrowIfNull(script);
        var batches = new List<SqlBatch>();
        var state = LexState.Normal;
        int commentDepth = 0, position = 0, line = firstLine, batchStart = 0, batchLine = firstLine;
        while (position < script.Length)
        {
            int end = script.IndexOf('\n', position);
            if (end < 0) end = script.Length;
            string code = ReadCode(script.AsSpan(position, end - position), ref state, ref commentDepth).Trim();
            if (code.StartsWith(':') || code.StartsWith("!!", StringComparison.Ordinal) || SqlCmdVariable().IsMatch(code))
                throw new SqlScriptException("SQLCMD directives and variables are not supported. Run plain Transact-SQL.", line);
            if (GoPrefix().IsMatch(code))
            {
                if (!code.Equals("GO", StringComparison.OrdinalIgnoreCase))
                    throw new SqlScriptException("Only GO on its own line is supported; GO repeat counts and arguments are not supported.", line);
                AddBatch(position);
                batchStart = Math.Min(end + 1, script.Length);
                batchLine = line + 1;
            }
            position = end + 1;
            line++;
        }
        AddBatch(script.Length);
        return batches;

        void AddBatch(int end)
        {
            string text = script[batchStart..end];
            if (!string.IsNullOrWhiteSpace(text)) batches.Add(new SqlBatch(text, batchLine, batchStart));
        }
    }

    private static string ReadCode(ReadOnlySpan<char> line, ref LexState state, ref int depth)
    {
        var code = new StringBuilder();
        if (state is LexState.String or LexState.Identifier or LexState.QuotedIdentifier) code.Append('~');
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i], next = i + 1 < line.Length ? line[i + 1] : '\0';
            if (state == LexState.BlockComment)
            {
                if (c == '/' && next == '*') { depth++; i++; }
                else if (c == '*' && next == '/') { i++; if (--depth == 0) state = LexState.Normal; }
                continue;
            }
            if (state is LexState.String or LexState.Identifier or LexState.QuotedIdentifier)
            {
                char close = state == LexState.String ? '\'' : state == LexState.Identifier ? ']' : '"';
                if (c == close)
                {
                    if (next == close) i++;
                    else state = LexState.Normal;
                }
                // Mask literal contents so SQLCMD-like text is permitted inside quoted values.
                code.Append('~');
                continue;
            }
            if (c == '-' && next == '-') break;
            if (c == '/' && next == '*') { state = LexState.BlockComment; depth = 1; i++; code.Append(' '); continue; }
            if (c == '\'') state = LexState.String;
            else if (c == '[') state = LexState.Identifier;
            else if (c == '"') state = LexState.QuotedIdentifier;
            code.Append(c);
        }
        return code.ToString();
    }

    [GeneratedRegex(@"^GO(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex GoPrefix();
    [GeneratedRegex(@"\$\([^)]+\)", RegexOptions.CultureInvariant)] private static partial Regex SqlCmdVariable();
}
