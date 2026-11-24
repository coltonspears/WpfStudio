using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WpfStudio.Api.Storage;
using WpfStudio.App.Features.Api;
using WpfStudio.App.Features.ColtonGpt;
using WpfStudio.App.Features.DevOps;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Git;
using WpfStudio.App.Features.Packages;
using WpfStudio.App.Features.Profiling;
using WpfStudio.App.ViewModels;
using WpfStudio.CodeAssist;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Diagnostics;
using WpfStudio.Contracts.Profiling;
using WpfStudio.Core;
using WpfStudio.Core.Debugging;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Net;
using WpfStudio.Core.Wpf;
using WpfStudio.Database;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Design;
using WpfStudio.Runtime.Profiling;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.App.Services;

/// <summary>The application's composition root, one method per area. It does not depend on <see cref="App"/>, so tests build the same container.</summary>
public static class StudioServices
{
    private static string DataDirectory(IServiceProvider services) => services.GetRequiredService<IOptions<StudioPaths>>().Value.DataDirectory;

    public static IServiceCollection AddWpfStudio(this IServiceCollection services, IUiDispatcher dispatcher) => services
        .AddStudioCore(dispatcher).AddNetwork().AddWorkspaceServices().AddCodeAssist().AddDesigner().AddDebugging().AddTerminal().AddDatabaseFeature().AddGit().AddDevOps().AddPackages().AddAssistant().AddApiClient().AddProfiling().AddUpdates().AddShell();

    public static IServiceCollection AddStudioCore(this IServiceCollection services, IUiDispatcher dispatcher)
    {
        services.AddLogging();
        services.AddOptions<StudioPaths>();
        services.AddSingleton(dispatcher);
        // Its own instance rather than WeakReferenceMessenger.Default: each container, and so each test, has its own recipients.
        services.AddSingleton<IMessenger>(_ => new WeakReferenceMessenger());
        services.AddSingleton<DesktopDialogs>();
        services.AddSingleton<IFileDialogService>(sp => sp.GetRequiredService<DesktopDialogs>());
        services.AddSingleton<IUserDialogService>(sp => sp.GetRequiredService<DesktopDialogs>());
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDesktopService, DesktopService>();
        services.AddSingleton<IClipboardService>(ClipboardService.Shared);
        services.AddSingleton(sp => new DocumentStore(DataDirectory(sp)));
        services.AddSingleton(sp => new SettingsStore(DataDirectory(sp)));
        services.AddSingleton<WorkspaceEditTransaction>();
        // One monitor for the process: the language, preview and Code Assist clients take it as an optional constructor argument.
        services.AddSingleton(_ => new PerfMonitor());
        services.AddSingleton(sp => new PerfLog(sp.GetRequiredService<PerfMonitor>(), DataDirectory(sp)));
        return services;
    }

    /// <summary>
    /// The proxy in Settings › Network, as one live object. Handlers built from it follow later changes, so the HTTP
    /// clients below use it without being rebuilt.
    /// </summary>
    public static IServiceCollection AddNetwork(this IServiceCollection services)
    {
        services.AddSingleton<StudioWebProxy>();
        services.AddSingleton<NetworkPreferences>();
        return services;
    }

    private static System.Net.Http.HttpMessageHandler ProxiedHandler(IServiceProvider services) =>
        new System.Net.Http.SocketsHttpHandler { Proxy = services.GetRequiredService<StudioWebProxy>(), UseProxy = true };

    public static IServiceCollection AddApiClient(this IServiceCollection services)
    {
        services.AddSingleton(sp => new ApiStore(System.IO.Path.Combine(DataDirectory(sp), "api"), new WindowsSecretProtector("WpfStudio.ApiClient.v1")));
        services.AddSingleton<ApiClientViewModel>();
        return services;
    }

    public static IServiceCollection AddWorkspaceServices(this IServiceCollection services)
    {
        services.AddSingleton<WorkspaceClient>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<WpfIndexService>();
        services.AddSingleton<ScaffoldingService>();
        services.AddSingleton<XamlCompletionService>();
        services.AddSingleton<XamlFormattingService>();
        return services;
    }

    public static IServiceCollection AddCodeAssist(this IServiceCollection services)
    {
        services.AddSingleton(_ => new AssistModelCatalog(AssistModelCatalog.DefaultDirectory));
        services.AddSingleton<CodeAssistEngine>();
        services.AddSingleton<CodeAssistPreferences>();
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
        services.AddSingleton<IDebugAdapterProvider>(sp => new DebuggerInstaller(null, DataDirectory(sp)));
        services.AddSingleton<DebugSession>();
        services.AddSingleton<DebuggerViewModel>();
        return services;
    }

    public static IServiceCollection AddTerminal(this IServiceCollection services) => services.AddSingleton<TerminalViewModel>();

    public static IServiceCollection AddGit(this IServiceCollection services) => services.AddSingleton<GitService>();

    /// <summary>
    /// The DevOps workbench's HTTP client goes through the proxy in Settings › Network and follows no redirect by itself:
    /// <see cref="WpfStudio.Core.DevOps.DevOpsRestClient"/> follows them, and sends the token only while the address stays on the service.
    /// </summary>
    public static IServiceCollection AddDevOps(this IServiceCollection services)
    {
        services.AddHttpClient(DevOpsClientName, client => client.Timeout = TimeSpan.FromSeconds(100))
            .ConfigurePrimaryHttpMessageHandler(sp => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                Proxy = sp.GetRequiredService<StudioWebProxy>(),
                UseProxy = true,
            });
        services.AddSingleton<IDevOpsCredentials>(sp => new DevOpsCredentials(DataDirectory(sp), sp.GetRequiredService<GitService>()));
        services.AddSingleton<DevOpsProviderFactory>(sp =>
        {
            var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            return (target, token, settings) => target.Kind == WpfStudio.Core.DevOps.DevOpsProviderKind.AzureDevOps
                ? new WpfStudio.Core.DevOps.AzureDevOps.AzureDevOpsProvider(factory.CreateClient(DevOpsClientName), target, token) { HiddenStates = settings.HiddenStateList }
                : new WpfStudio.Core.DevOps.GitHub.GitHubProvider(factory.CreateClient(DevOpsClientName), target, token);
        });
        return services;
    }

    private const string DevOpsClientName = "devops";

    public static IServiceCollection AddPackages(this IServiceCollection services) => services.AddSingleton<INuGetPackageService, NuGetPackageService>();

    public static IServiceCollection AddAssistant(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AssistantSettingsStore(DataDirectory(sp)));
        services.AddHttpClient<OpenRouterClient>(client => client.Timeout = TimeSpan.FromMinutes(5)).ConfigurePrimaryHttpMessageHandler(ProxiedHandler);
        services.AddSingleton<AssistantSettingsViewModel>();
        services.AddSingleton<AssistantViewModel>();
        return services;
    }

    /// <summary>
    /// Help > Check for updates: GitHub's release feed through the proxy in Settings › Network, and the downloads staged
    /// under the data folder. The running package's own folder is the one an update replaces.
    /// </summary>
    public static IServiceCollection AddUpdates(this IServiceCollection services)
    {
        services.AddHttpClient(UpdatesClientName, client => client.Timeout = TimeSpan.FromMinutes(30)).ConfigurePrimaryHttpMessageHandler(ProxiedHandler);
        services.AddSingleton(sp => new WpfStudio.Core.Updates.UpdateService(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(UpdatesClientName),
            AppContext.BaseDirectory, System.IO.Path.Combine(DataDirectory(sp), "updates"), ProductVersion));
        services.AddSingleton<IUpdateLauncher, ProcessUpdateLauncher>();
        return services;
    }

    private const string UpdatesClientName = "updates";
    private static string? ProductVersion => typeof(StudioServices).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public static IServiceCollection AddProfiling(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryProfiler, MemoryProfilerClient>();
        services.AddSingleton<IProcessMemorySampler, ProcessMemorySampler>();
        services.AddSingleton<MemoryProfilerViewModel>();
        return services;
    }

    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        services.AddSingleton<FontCatalog>();
        services.AddSingleton<EditorPreferences>();
        services.AddSingleton(sp => new WorkspaceStateStore<UserTaskItem>("tasks", DataDirectory(sp)));
        services.AddSingleton(sp => new WorkspaceStateStore<FileGroup>("file-groups", DataDirectory(sp)));
        // Factories, not instances: a watcher holds the paths of one workspace or one set of open files.
        services.AddSingleton<OpenDocumentWatcherFactory>(changed => new OpenDocumentWatcher(changed));
        services.AddSingleton<KnownSourceWatcherFactory>((paths, changed, reconcile, contextFiles, failed) => new KnownSourceFileWatcher(paths, changed, null, reconcile, contextFiles, failed));
        // The runtime check: what is installed, Microsoft's release metadata, and the installers it lists.
        services.AddSingleton<WpfStudio.Workspace.Runtimes.IDotNetInventorySource, WpfStudio.Workspace.Runtimes.DotNetInventorySource>();
        services.AddSingleton<WpfStudio.Workspace.Runtimes.IInstallerLauncher, ShellInstallerLauncher>();
        services.AddHttpClient("dotnet-releases", client => client.Timeout = TimeSpan.FromMinutes(30)).ConfigurePrimaryHttpMessageHandler(ProxiedHandler);
        services.AddSingleton(sp => new WpfStudio.Workspace.Runtimes.DotNetReleases(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("dotnet-releases")));
        services.AddSingleton(sp => new WpfStudio.Workspace.Runtimes.RuntimeInstaller(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("dotnet-releases"),
            sp.GetRequiredService<WpfStudio.Workspace.Runtimes.IInstallerLauncher>(), System.IO.Path.Combine(DataDirectory(sp), "downloads")));
        services.AddSingleton<WpfStudio.App.Features.Runtimes.RuntimeCheckServices>();
        services.AddSingleton<ShellToolServices>();
        services.AddSingleton<ShellPanes>();
        services.AddSingleton<ShellLanguageServices>();
        services.AddSingleton<ShellDesktopServices>();
        services.AddSingleton<StudioFeatures>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
