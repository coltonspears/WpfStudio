using System.Text;
using WpfStudio.App.Services;

namespace WpfStudio.Shell.Tests;

public sealed class ProjectDiagnosticNavigationTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf32")]
    public async Task BomDecodingPreservesExactTextAndNewlines(string name)
    {
        var path = Path.GetTempFileName();
        try
        {
            Encoding encoding = name switch { "utf16" => Encoding.Unicode, "utf32" => Encoding.UTF32, _ => new UTF8Encoding(true) };
            const string text = "<Grid>\r\n<!-- café 漢字 😀 -->\n</Grid>";
            await File.WriteAllTextAsync(path, text, encoding);
            Assert.Equal(text, await ProjectXamlDiagnosticText.ReadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CharacterBudgetRejectsFileThatGrewSinceAnalysisWithoutReturningTruncatedText()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, new string('x', ProjectXamlDiagnosticText.MaximumCharacters));
            Assert.Equal(ProjectXamlDiagnosticText.MaximumCharacters, (await ProjectXamlDiagnosticText.ReadAsync(path))!.Length);
            await File.AppendAllTextAsync(path, "x");
            Assert.Null(await ProjectXamlDiagnosticText.ReadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ByteBudgetAndUnavailableFileAreRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength((long)ProjectXamlDiagnosticText.MaximumCharacters * 4 + 5);
                Assert.Null(await ProjectXamlDiagnosticText.ReadAsync(path));
            }
            Assert.Null(await ProjectXamlDiagnosticText.ReadAsync(path));
            File.Delete(path);
            Assert.Null(await ProjectXamlDiagnosticText.ReadAsync(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task MalformedUtf8AndCancelledReadDoNotProduceNavigationText()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [0xc3, 0x28]);
            Assert.Null(await ProjectXamlDiagnosticText.ReadAsync(path));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectXamlDiagnosticText.ReadAsync(path, cancellation.Token));
        }
        finally { File.Delete(path); }
    }
}
