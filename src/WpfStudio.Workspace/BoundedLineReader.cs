using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("WpfStudio.Workspace.Tests")]

namespace WpfStudio.Workspace;

/// <summary>Drains tool output without allocating an unbounded string for a tool that never writes a newline.</summary>
internal static class BoundedLineReader
{
    internal const int MaximumLength = 32_768;

    internal static async IAsyncEnumerable<string> ReadLinesAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var truncated = false;
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is var count && count > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    yield return line.ToString() + (truncated ? " [line truncated]" : "");
                    line.Clear(); truncated = false;
                }
                else if (line.Length < MaximumLength) line.Append(character);
                else truncated = true;
            }
        }
        if (line.Length > 0 || truncated) yield return line.ToString() + (truncated ? " [line truncated]" : "");
    }
}
