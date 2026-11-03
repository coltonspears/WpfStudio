using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Database;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;

namespace WpfStudio.App;

public partial class App : Application
{
    private IHost? _host;
#pragma warning disable VSTHRD100 // A WPF override; its body catches and reports startup failures.
    protected override async void OnStartup(StartupEventArgs e)
#pragma warning restore VSTHRD100
    {
        base.OnStartup(e);
        var faults = new UnhandledFaultReporter(Dispatcher, AppPaths.DataDirectory, () => _host?.Services.GetService<ILogger<App>>(), (status, details) =>
        {
            if (MainWindow?.DataContext is not ShellViewModel shell) return;
            shell.Status = status; shell.AppendOutput(details);
        });
        DispatcherUnhandledException += (_, args) => args.Handled = faults.Report(args.Exception, "UI thread");
        TaskScheduler.UnobservedTaskException += (_, args) => { args.SetObserved(); faults.Report(args.Exception, "background task"); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => faults.Report(args.ExceptionObject as Exception ?? new InvalidOperationException(args.ExceptionObject?.ToString()), "process");
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Logging.ClearProviders();
            // By factory so the container owns it: disposing the host writes the last queued lines. An instance given to AddProvider is never disposed.
            builder.Services.AddSingleton<ILoggerProvider>(_ => new StudioLogProvider());
            ConfigureServices(builder.Services);
            _host = builder.Build(); await _host.StartAsync();
            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            var window = _host.Services.GetRequiredService<MainWindow>();
            window.DataContext = shell; MainWindow = window;
#pragma warning disable VSTHRD101 // A WPF event handler; the awaits below are guarded.
            window.Closed += async (_, _) =>
            {
                try { await _host.StopAsync(TimeSpan.FromSeconds(5)); if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync(); else _host.Dispose(); }
                catch (Exception ex) { File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "shutdown-error.log"), ex.ToString()); }
                finally { Shutdown(); }
            };
#pragma warning restore VSTHRD101
            window.Show();
            try { await shell.InitializeAsync(e.Args); }
            catch (Exception ex) { shell.Status = ex.Message; shell.AppendOutput(ex.ToString()); }
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "startup-error.log"), ex.ToString() + Environment.NewLine);
            MessageBox.Show(ex.Message + "\n\nDetails: " + Path.Combine(AppPaths.DataDirectory, "startup-error.log"), "WpfStudio could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
            Shutdown(1);
        }
    }
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(Dispatcher));
        services.AddSingleton<DesktopDialogs>();
        services.AddSingleton<IFileDialogService>(sp => sp.GetRequiredService<DesktopDialogs>());
        services.AddSingleton<IUserDialogService>(sp => sp.GetRequiredService<DesktopDialogs>());
        services.AddSingleton<DocumentStore>(); services.AddSingleton<SettingsStore>();
        services.AddSingleton<WorkspaceEditTransaction>(); services.AddSingleton<WorkspaceClient>(); services.AddSingleton<BuildService>();
        services.AddSingleton<WpfIndexService>(); services.AddSingleton<ScaffoldingService>(); services.AddSingleton<XamlCompletionService>();
        services.AddSingleton<WpfStudio.Runtime.Design.IPreviewClient, WpfStudio.Runtime.Design.PreviewClient>();
        services.AddSingleton<Features.Designer.DesignerViewModel>();
        services.AddSingleton<WpfStudio.Core.Debugging.DebuggerInstaller>(_ => new());
        services.AddSingleton(sp => new DebugSession { AdapterPath = sp.GetRequiredService<WpfStudio.Core.Debugging.DebuggerInstaller>().Locate() ?? Path.Combine(AppContext.BaseDirectory, "debugger", "netcoredbg.exe") });
        services.AddSingleton(sp =>
        {
            var installer = sp.GetRequiredService<WpfStudio.Core.Debugging.DebuggerInstaller>();
            return new DebuggerViewModel(sp.GetRequiredService<DebugSession>(), sp.GetRequiredService<IUiDispatcher>())
            {
                AdapterInstaller = installer.InstallAsync,
                AdapterDescription = "netcoredbg " + WpfStudio.Core.Debugging.DebuggerInstaller.Version
            };
        }); services.AddSingleton<TerminalViewModel>();
        services.AddSingleton<IConnectionProfileStore>(new ConnectionProfileStore(Path.Combine(AppPaths.DataDirectory, "sql-connections.json")));
        services.AddSingleton<IQueryRecoveryStore>(new QueryRecoveryStore(Path.Combine(AppPaths.DataDirectory, "sql-recovery.json")));
        services.AddDatabaseFeature(); services.AddSingleton<ShellViewModel>(); services.AddSingleton<MainWindow>();
        services.AddStudioFeatures();
    }
}
