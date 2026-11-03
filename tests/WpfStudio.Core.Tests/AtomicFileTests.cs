using System.Text;
using WpfStudio.Core.IO;

namespace WpfStudio.Core.Tests;

public sealed class AtomicFileTests
{
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WpfStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacesTheFileAndCreatesItsFolderLeavingNoTemporaryFile(bool writeThrough)
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "nested", "state.json");
        await AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("first"), writeThrough: writeThrough);
        await AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("second"), writeThrough: writeThrough);
        Assert.Equal("second", await File.ReadAllTextAsync(path));
        Assert.Equal(["state.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ACancelledWriteKeepsTheOldContentAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(NewDirectory(), "state.json");
        await AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("kept"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("lost"), cancellation.Token));
        Assert.Equal("kept", await File.ReadAllTextAsync(path));
        Assert.Equal(["state.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }
}
