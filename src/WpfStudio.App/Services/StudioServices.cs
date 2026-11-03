using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        .AddStudioCore(dispatcher).AddWorkspaceServices().AddDesigner().AddDebugging().AddTerminal().AddDatabaseFeature().AddGit().AddPackages().AddAssistant().AddProfiling().AddShell();

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

    public static IServiceCollection AddPackages(this IServiceCollection services) => services.AddSingleton<INuGetPackageService, NuGetPackageService>();

    public static IServiceCollection AddAssistant(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AssistantSettingsStore(DataDirectory(sp)));
        services.AddHttpClient<OpenRouterClient>(client => client.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<AssistantSettingsViewModel>();
        services.AddSingleton<AssistantViewModel>();
        return services;
    }

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
