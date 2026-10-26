using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WpfStudio.App.Features.ColtonGpt;
using WpfStudio.App.Features.Git;
using WpfStudio.App.Features.Packages;
using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

/// <summary>Composition and workspace coordination for optional tools.</summary>
public sealed class StudioFeatures : IDisposable
{
    private bool _disposed;
    public StudioFeatures(INuGetPackageService packages, GitService git, AssistantViewModel assistant, IUserDialogService dialogs)
    {
        Assistant = assistant;
        GitService = git;
        Packages = new(packages, dialogs, () => Git?.IsBusy == true ? Task.FromException<bool>(new InvalidOperationException("Wait for the Git operation to finish.")) : SaveBeforeMutation(), () => WorkspaceChanged());
        Git = new(git, _ => Packages.IsBusy ? Task.FromException<bool>(new InvalidOperationException("Wait for the package operation to finish.")) : SaveBeforeMutation()) { AutoSelect = true };
    }
    public Func<Task<bool>> SaveBeforeMutation { get; set; } = () => Task.FromResult(false);
    public Func<Task> WorkspaceChanged { get; set; } = () => Task.CompletedTask;
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

/// <summary>Shell integration kept outside presentation state.</summary>
public static class DesktopNavigation
{
    public static void Reveal(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (File.Exists(path)) { start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); }
        else start.ArgumentList.Add(path);
        Process.Start(start);
    }
    public static void Copy(string text) => Clipboard.SetText(text);
    /// <summary>Opens an http(s) address in the default browser; other schemes are refused.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new InvalidOperationException("Only web addresses can be opened.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
