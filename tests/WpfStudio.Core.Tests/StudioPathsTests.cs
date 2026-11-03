using WpfStudio.Core;

namespace WpfStudio.Core.Tests;

public sealed class StudioPathsTests
{
    [Fact]
    public void TheDataDirectoryVariableStillRedirectsTheDefault()
    {
        var previous = Environment.GetEnvironmentVariable("WPFSTUDIO_DATA_DIRECTORY");
        var redirected = Path.Combine(Path.GetTempPath(), "WpfStudio-Paths-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WPFSTUDIO_DATA_DIRECTORY", redirected);
        try
        {
            var paths = new StudioPaths();
            Assert.Equal(redirected, paths.DataDirectory);
            Assert.Equal(Path.Combine(redirected, "sql-connections.json"), paths.SqlConnectionsFile);
            Assert.Equal(Path.Combine(redirected, "sql-recovery.json"), paths.SqlRecoveryFile);
        }
        finally { Environment.SetEnvironmentVariable("WPFSTUDIO_DATA_DIRECTORY", previous); }
    }
}
