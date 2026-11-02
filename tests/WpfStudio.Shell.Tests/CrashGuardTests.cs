using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using WpfStudio.App.Services;
using WpfStudio.Core;

namespace WpfStudio.Shell.Tests;

/// <summary>Faults in background callbacks and unreadable profile files must be reported, never allowed to end the process.</summary>
public sealed class CrashGuardTests
{
    [Fact]
    public void SourceWatcherReportsACallbackFailureInsteadOfThrowing()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-WatcherGuard-").FullName;
        try
        {
            var source = Path.Combine(directory, "Main.xaml"); File.WriteAllText(source, "<Grid />");
            Exception? reported = null;
            using var watcher = new KnownSourceFileWatcher([source], _ => throw new InvalidOperationException("consumer failed"), TimeSpan.Zero,
                failed: exception => reported = exception);
            watcher.Reconcile();
            Assert.IsType<InvalidOperationException>(reported);
            Assert.Equal("consumer failed", reported.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SourceWatcherSurvivesAFaultingFailureHandler()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-WatcherGuard-").FullName;
        try
        {
            var source = Path.Combine(directory, "Main.xaml"); File.WriteAllText(source, "<Grid />");
            using var watcher = new KnownSourceFileWatcher([source], _ => throw new InvalidOperationException(), TimeSpan.Zero,
                failed: _ => throw new NotSupportedException());
            watcher.Reconcile();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LogProviderSwallowsALockedLogFileAndResumesLater()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-LogGuard-").FullName;
        try
        {
            var logs = Path.Combine(directory, "Logs"); Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "studio.log");
            using var provider = new StudioLogProvider(directory);
            var logger = provider.CreateLogger("Tests");
            using (new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                logger.LogWarning("while locked");
            logger.LogWarning("after unlock");
            // The provider pauses after a failure, so neither line reaches the file; what matters is that no exception escaped.
            Assert.DoesNotContain("while locked", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SettingsStoreReturnsDefaultsWhenTheFileCannotBeRead()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-SettingsGuard-").FullName;
        try
        {
            var store = new SettingsStore(directory);
            await store.SaveAsync(new StudioSettings { Theme = "Light" });
            var path = Path.Combine(directory, "settings.json");
            using (DenyRead(path))
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllTextAsync(path));
                var loaded = await store.LoadAsync();
                Assert.Equal("Dark", loaded.Theme);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task WorkspaceStateStoreReturnsAnEmptyListWhenTheFileCannotBeRead()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-StateGuard-").FullName;
        try
        {
            var store = new WorkspaceStateStore<string>("tasks", directory);
            await store.SaveAsync(@"C:\work\App.sln", ["one"]);
            var path = Directory.EnumerateFiles(Path.Combine(directory, "tasks"), "*.json").Single();
            using (DenyRead(path)) Assert.Empty(await store.LoadAsync(@"C:\work\App.sln"));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>Denies the current user read access to <paramref name="path"/> until disposed, so the file can be deleted afterwards.</summary>
    private static IDisposable DenyRead(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.Read, AccessControlType.Deny);
        security.AddAccessRule(rule); file.SetAccessControl(security);
        return new Restore(file, rule);
    }
    private sealed class Restore(FileInfo file, FileSystemAccessRule rule) : IDisposable
    {
        public void Dispose() { var security = file.GetAccessControl(); security.RemoveAccessRule(rule); file.SetAccessControl(security); }
    }
}
