using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using WpfStudio.App.Features.ColtonGpt;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Git;
using WpfStudio.App.Features.Packages;
using WpfStudio.App.Features.Profiling;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Profiling;
using WpfStudio.Core;
using WpfStudio.Core.Debugging;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Database;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Design;
using WpfStudio.Runtime.Profiling;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;

namespace WpfStudio.App.Services;

/// <summary>The application's composition root, one method per area. It does not depend on <see cref="App"/>, so tests build the same container.</summary>
public static class StudioServices
{
    public static IServiceCollection AddWpfStudio(this IServiceCollection services, IUiDispatcher dispatcher) => services
        .AddStudioCore(dispatcher).AddWorkspaceServices().AddDesigner().AddDebugging().AddTerminal().AddDatabase().AddGit().AddPackages().AddAssistant().AddProfiling().AddShell();

    public static IServiceCollection AddStudioCore(this IServiceCollection services, IUiDispatcher dispatcher)
    {
        services.AddLogging();
        services.AddSingleton(dispatcher);
        services.AddSingleton<DesktopDialogs>();
        services.AddSingleton<IFileDialogService>(sp => sp.GetRequiredService<DesktopDialogs>());
        services.AddSingleton<IUserDialogService>(sp => sp.GetRequiredService<DesktopDialogs>());
        services.AddSingleton<DocumentStore>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<WorkspaceEditTransaction>();
        return services;
    }

    public static IServiceCollection AddWorkspaceServices(this IServiceCollection services)
    {
        services.AddSingleton<WorkspaceClient>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<WpfIndexService>();
        services.AddSingleton<ScaffoldingService>();
        services.AddSingleton<XamlCompletionService>();
        return services;
    }

    public static IServiceCollection AddDesigner(this IServiceCollection services)
    {
        services.AddSingleton<IPreviewClient, PreviewClient>();
        services.AddSingleton<DesignerViewModel>();
        return services;
    }

    public static IServiceCollection AddDebugging(this IServiceCollection services)
    {
        services.AddSingleton<DebuggerInstaller>(_ => new());
        services.AddSingleton(sp => new DebugSession { AdapterPath = sp.GetRequiredService<DebuggerInstaller>().Locate() ?? Path.Combine(AppContext.BaseDirectory, "debugger", "netcoredbg.exe") });
        services.AddSingleton(sp =>
        {
            var installer = sp.GetRequiredService<DebuggerInstaller>();
            return new DebuggerViewModel(sp.GetRequiredService<DebugSession>(), sp.GetRequiredService<IUiDispatcher>())
            {
                AdapterInstaller = installer.InstallAsync,
                AdapterDescription = "netcoredbg " + DebuggerInstaller.Version
            };
        });
        return services;
    }

    public static IServiceCollection AddTerminal(this IServiceCollection services) => services.AddSingleton<TerminalViewModel>();

    public static IServiceCollection AddDatabase(this IServiceCollection services)
    {
        services.AddSingleton<IConnectionProfileStore>(new ConnectionProfileStore(Path.Combine(AppPaths.DataDirectory, "sql-connections.json")));
        services.AddSingleton<IQueryRecoveryStore>(new QueryRecoveryStore(Path.Combine(AppPaths.DataDirectory, "sql-recovery.json")));
        return services.AddDatabaseFeature();
    }

    public static IServiceCollection AddGit(this IServiceCollection services) => services.AddSingleton<GitService>();

    public static IServiceCollection AddPackages(this IServiceCollection services) => services.AddSingleton<INuGetPackageService, NuGetPackageService>();

    public static IServiceCollection AddAssistant(this IServiceCollection services)
    {
        services.AddSingleton<AssistantSettingsStore>();
        services.AddSingleton(_ => new OpenRouterClient(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }));
        services.AddSingleton<AssistantSettingsViewModel>();
        services.AddSingleton<AssistantViewModel>();
        return services;
    }

    public static IServiceCollection AddProfiling(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryProfiler, MemoryProfilerClient>();
        services.AddSingleton<MemoryProfilerViewModel>();
        return services;
    }

    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        services.AddSingleton<StudioFeatures>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
