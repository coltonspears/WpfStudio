using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WpfStudio.App.Features.Updates;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Updates;

namespace WpfStudio.Shell.Tests;

/// <summary>Help > Check for updates in the shell: the page it opens, and closing WpfStudio for an update only once unsaved work is settled.</summary>
public sealed class UpdateShellTests
{
    private sealed class Launcher : IUpdateLauncher
    {
        public List<ProcessStartInfo> Started { get; } = [];
        public bool Fail { get; set; }
        public void Start(ProcessStartInfo start)
        {
            if (Fail) throw new InvalidOperationException("No such file.");
            Started.Add(start);
        }
    }

    /// <summary>GitHub with no release published, so a check ends at once without the network.</summary>
    private sealed class NoReleases : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>The package being "updated" is a folder in the context's data folder, removed with it.</summary>
    private static ShellTestContext Create(Launcher launcher) => new(configure: services =>
    {
        services.AddSingleton(sp =>
        {
            var data = sp.GetRequiredService<IOptions<StudioPaths>>().Value.DataDirectory;
            return new UpdateService(new HttpClient(new NoReleases()), Path.Combine(data, "app"), Path.Combine(data, "updates"), "0.1.0", "contoso/WpfStudio");
        });
        services.AddSingleton<IUpdateLauncher>(launcher);
    });

    /// <summary>A staged package as a download leaves it: its executable is all a restart looks for.</summary>
    private static StagedUpdate Staged(string root)
    {
        var staged = new StagedUpdate(ReleaseVersion.Parse("0.2.0"), Directory.CreateDirectory(Path.Combine(root, "updates", "0.2.0", "package", "WpfStudio")).FullName);
        File.WriteAllText(staged.Executable, "exe 0.2.0");
        return staged;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task CheckForUpdatesOpensTheUpdatesPageOverTheNativePreview()
    {
        await using var test = Create(new Launcher());
        var shell = test.Shell;

        shell.CheckForUpdatesCommand.Execute(null);

        Assert.True(shell.IsAboutOpen);
        Assert.True(shell.IsUpdatesPage);
        Assert.False(shell.IsWhatsNewPage);
        Assert.False(shell.IsAboutPage);
        Assert.True(shell.Designer.IsOverlayOpen);
        await WaitUntilAsync(() => shell.Updates.State == UpdateState.NoReleases);

        shell.ShowWhatsNewCommand.Execute(null);
        Assert.True(shell.IsWhatsNewPage);
        Assert.False(shell.IsUpdatesPage);
        shell.ShowAboutCommand.Execute(null);
        Assert.True(shell.IsAboutPage);
        // The tab shows the last answer rather than asking GitHub again.
        shell.ShowUpdatesCommand.Execute(null);
        Assert.True(shell.IsUpdatesPage);
        Assert.Equal(UpdateState.NoReleases, shell.Updates.State);

        shell.CloseAboutCommand.Execute(null);
        Assert.False(shell.Designer.IsOverlayOpen);
    }

    [Fact]
    public async Task RestartingForAnUpdateWaitsForUnsavedWorkAndThenStartsTheStagedPackage()
    {
        var launcher = new Launcher();
        await using var test = Create(launcher);
        var shell = test.Shell;
        IUpdateHost host = shell;
        var staged = Staged(test.Root);
        var shutdowns = 0;
        shell.ShutdownRequested += () => shutdowns++;

        // A download that finished while the page was closed is left for the person to install.
        Assert.False(await host.RestartToUpdateAsync(staged, automatic: true));
        Assert.Contains("is downloaded", shell.Status);

        // Unsaved work, and the person keeps WpfStudio open.
        shell.Database.Queries.SelectedDocument!.SqlText += "\nSELECT 42;";
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Cancel);
        Assert.False(await host.RestartToUpdateAsync(staged, automatic: false));
        Assert.Contains("is ready", shell.Status);
        Assert.Empty(launcher.Started);
        Assert.Equal(0, shutdowns);

        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Discard);
        Assert.True(await host.RestartToUpdateAsync(staged, automatic: false));

        var start = Assert.Single(launcher.Started);
        Assert.Equal(staged.Executable, start.FileName);
        Assert.Equal([UpdateCommandLine.ApplyOption, shell.Updates.ApplicationDirectory, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), shell.ProductVersion], start.ArgumentList);
        Assert.Equal(1, shutdowns);
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task AnUpdateThatCannotStartIsReportedAndWpfStudioStillCloses()
    {
        var launcher = new Launcher { Fail = true };
        await using var test = Create(launcher);
        var shutdowns = 0;
        test.Shell.ShutdownRequested += () => shutdowns++;

        Assert.True(await ((IUpdateHost)test.Shell).RestartToUpdateAsync(Staged(test.Root), automatic: false));

        Assert.StartsWith("WpfStudio was not updated: The update could not start: No such file.", Assert.Single(test.Dialogs.Errors));
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task AStagedUpdateThatWasRemovedDoesNotEndTheSession()
    {
        var launcher = new Launcher();
        await using var test = Create(launcher);
        var shutdowns = 0;
        test.Shell.ShutdownRequested += () => shutdowns++;
        test.Shell.Database.Queries.SelectedDocument!.SqlText += "\nSELECT 42;";
        var staged = Staged(test.Root);
        File.Delete(staged.Executable);

        Assert.False(await ((IUpdateHost)test.Shell).RestartToUpdateAsync(staged, automatic: false));

        Assert.Contains("is gone", test.Shell.Status);
        Assert.Empty(test.Dialogs.SaveQuestions); // Nothing was asked: the session was never closed.
        Assert.Empty(launcher.Started);
        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public async Task StartingAfterAnUpdateSaysSoInTheStatusBar()
    {
        await using var test = Create(new Launcher());
        await test.Shell.InitializeAsync([UpdateCommandLine.UpdatedFromOption, "0.0.9"]);
        Assert.Equal("Updated WpfStudio from 0.0.9 to 0.1.0", test.Shell.Status);
        Assert.Contains("Updated WpfStudio from 0.0.9 to 0.1.0.", test.Shell.Output);
    }
}
