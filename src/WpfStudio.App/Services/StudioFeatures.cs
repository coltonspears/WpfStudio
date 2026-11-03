using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using WpfStudio.App.Features.ColtonGpt;
using WpfStudio.App.Features.Git;
using WpfStudio.App.Features.Packages;
using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

/// <summary>What the optional tools need from the shell when they change workspace files.</summary>
public interface IWorkspaceMutationHost
{
    /// <summary>
    /// Saves every open document before a tool rewrites project files; false when the user cancelled. Throws while a
    /// build, debug session or change review is in progress.
    /// </summary>
    Task<bool> SaveBeforeMutationAsync();
    /// <summary>A tool changed workspace files (a package install, a checkout): re-check open files and reload the project model.</summary>
    Task WorkspaceChangedAsync();
}

/// <summary>Composition and workspace coordination for optional tools.</summary>
public sealed class StudioFeatures : IDisposable
{
    private IWorkspaceMutationHost? _host;
    private bool _disposed;
    public StudioFeatures(INuGetPackageService packages, GitService git, AssistantViewModel assistant, IUserDialogService dialogs, IDesktopService desktop)
    {
        Assistant = assistant;
        GitService = git;
        Packages = new(packages, dialogs, () => Git?.IsBusy == true ? Task.FromException<bool>(new InvalidOperationException("Wait for the Git operation to finish.")) : Host.SaveBeforeMutationAsync(), () => Host.WorkspaceChangedAsync());
        Git = new(git, desktop, _ => Packages.IsBusy ? Task.FromException<bool>(new InvalidOperationException("Wait for the package operation to finish.")) : Host.SaveBeforeMutationAsync()) { AutoSelect = true };
    }
    private IWorkspaceMutationHost Host => _host ?? throw new InvalidOperationException("The shell has not attached to the workspace tools yet.");
    /// <summary>Connects the tools to the shell that owns the documents and the project model; the shell calls this once.</summary>
    public void Attach(IWorkspaceMutationHost host) => _host = host;
    public PackagesViewModel Packages { get; }
    public GitViewModel Git { get; }
    /// <summary>The Git CLI boundary shared by the Git pane and editors (change margin and blame).</summary>
    public GitService GitService { get; }
    public AssistantViewModel Assistant { get; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Packages.Dispose(); Git.Dispose(); Assistant.Dispose();
    }
}

public static class StudioFeatureRegistration
{
    public static IServiceCollection AddStudioFeatures(this IServiceCollection services)
    {
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDesktopService, DesktopService>();
        services.AddSingleton<IClipboardService>(ClipboardService.Shared);
        services.AddSingleton<WpfStudio.Contracts.Profiling.IMemoryProfiler, WpfStudio.Runtime.Profiling.MemoryProfilerClient>();
        services.AddSingleton<Features.Profiling.MemoryProfilerViewModel>();
        services.AddSingleton<INuGetPackageService, NuGetPackageService>();
        services.AddSingleton<GitService>();
        services.AddSingleton<AssistantSettingsStore>();
        services.AddSingleton(_ => new OpenRouterClient(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }));
        services.AddSingleton<AssistantSettingsViewModel>();
        services.AddSingleton<AssistantViewModel>();
        services.AddSingleton<StudioFeatures>();
        return services;
    }
}
