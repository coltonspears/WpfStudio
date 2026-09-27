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
    public StudioFeatures(INuGetPackageService packages, GitService git, AssistantViewModel assistant, IUserDialogService dialogs)
    {
        Assistant = assistant;
        Packages = new(packages, dialogs, () => Git?.IsBusy == true ? Task.FromException<bool>(new InvalidOperationException("Wait for the Git operation to finish.")) : SaveBeforeMutation(), () => WorkspaceChanged());
        Git = new(git, _ => Packages.IsBusy ? Task.FromException<bool>(new InvalidOperationException("Wait for the package operation to finish.")) : SaveBeforeMutation());
    }
    public Func<Task<bool>> SaveBeforeMutation { get; set; } = () => Task.FromResult(false);
    public Func<Task> WorkspaceChanged { get; set; } = () => Task.CompletedTask;
    public PackagesViewModel Packages { get; }
    public GitViewModel Git { get; }
    public AssistantViewModel Assistant { get; }
    public void Dispose() { Packages.Dispose(); Git.Dispose(); Assistant.Dispose(); }
}

public static class StudioFeatureRegistration
{
    public static IServiceCollection AddStudioFeatures(this IServiceCollection services)
    {
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
}
