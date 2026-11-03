using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Core;

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
            builder.Services.AddSingleton<ILoggerProvider>(sp => new StudioLogProvider(sp.GetRequiredService<IOptions<StudioPaths>>().Value.DataDirectory));
            builder.Services.AddWpfStudio(new UiDispatcher(Dispatcher));
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
}
