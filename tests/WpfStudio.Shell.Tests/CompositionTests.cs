using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WpfStudio.App.Features.ColtonGpt;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Profiling;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Debugging;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Shell.Tests;

/// <summary>Builds the application's own container, so a missing registration fails here rather than at the next launch.</summary>
public sealed class CompositionTests
{
    private static readonly ServiceProviderOptions Validated = new() { ValidateOnBuild = true, ValidateScopes = true };

    private static IServiceCollection Studio(string data)
    {
        var services = new ServiceCollection().AddWpfStudio(new InlineDispatcher());
        services.Configure<StudioPaths>(paths => paths.DataDirectory = data);
        return services;
    }

    [Fact]
    public async Task EveryRegistrationValidatesAndTheShellResolves()
    {
        var data = Directory.CreateTempSubdirectory("WpfStudio-Composition-").FullName;
        try
        {
            await using var provider = Studio(data).BuildServiceProvider(Validated);
            var shell = provider.GetRequiredService<ShellViewModel>();
            Assert.Same(provider.GetRequiredService<StudioFeatures>(), shell.Features);
            Assert.Same(provider.GetRequiredService<DesignerViewModel>(), shell.Designer);
            Assert.Same(provider.GetRequiredService<MemoryProfilerViewModel>(), shell.MemoryProfiler);
            Assert.Equal(data, shell.DataFolder);
        }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public void AMissingRegistrationFailsWhenTheContainerIsBuilt()
    {
        var services = new ServiceCollection().AddWpfStudio(new InlineDispatcher());
        services.RemoveAll<IPreviewClient>();
        var failure = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(Validated));
        Assert.Contains(nameof(IPreviewClient), failure.Message);
    }

    [Fact]
    public async Task ConfiguredPathsRedirectThePerUserFiles()
    {
        var data = Directory.CreateTempSubdirectory("WpfStudio-Composition-").FullName;
        try
        {
            await using var provider = Studio(data).BuildServiceProvider(Validated);
            await provider.GetRequiredService<SettingsStore>().SaveAsync(new());
            await provider.GetRequiredService<IConnectionProfileStore>().SaveAsync([]);
            await provider.GetRequiredService<IQueryRecoveryStore>().SaveAsync([new(Guid.NewGuid(), "Query", "SELECT 1;", "", null)]);
            await provider.GetRequiredService<AssistantSettingsStore>().SaveAsync(new());
            foreach (var file in new[] { "settings.json", "sql-connections.json", "sql-recovery.json", "coltongpt.json" }) Assert.True(File.Exists(Path.Combine(data, file)), file);
            var installer = Assert.IsType<DebuggerInstaller>(provider.GetRequiredService<IDebugAdapterProvider>());
            Assert.StartsWith(data, installer.InstallDirectory);
        }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public async Task ToolsReachTheShellThroughTheirHostUntilItIsDisposed()
    {
        await using var context = new ShellTestContext();
        var shell = context.Shell;
        var assistant = shell.Features.Assistant;

        assistant.OpenSettingsCommand.Execute(null);
        if (shell.OpenSettingsCommand.ExecutionTask is { } opening) await opening;
        Assert.True(shell.IsSettingsOpen);
        Assert.Equal(ShellViewModel.AssistantPage, shell.SettingsPage);

        var notified = new List<string?>();
        shell.PropertyChanged += (_, args) => notified.Add(args.PropertyName);
        assistant.Settings.HasApiKey = true;
        Assert.Contains(nameof(ShellViewModel.IsColtonGptReady), notified);

        await shell.DisposeAsync();
        notified.Clear();
        assistant.Settings.HasApiKey = false;
        Assert.DoesNotContain(nameof(ShellViewModel.IsColtonGptReady), notified);
    }
}
