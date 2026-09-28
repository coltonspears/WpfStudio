using System.IO;
using System.Text;

namespace WpfStudio.App.Services;

/// <summary>Rechecks navigation text using the same decoding and bound as the project scan.</summary>
public static class ProjectXamlDiagnosticText
{
    public const int MaximumCharacters = 1_000_000;

    public static async Task<string?> ReadAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > (long)MaximumCharacters * 4 + 4) return null;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true,
                bufferSize: 8192, leaveOpen: true);
            var text = new StringBuilder(8192);
            var buffer = new char[8192];
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumCharacters - text.Length + 1)), token);
                if (count == 0) return text.ToString();
                if (text.Length + count > MaximumCharacters) return null;
                text.Append(buffer, 0, count);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return null; }
    }
}
