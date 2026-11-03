using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace WpfStudio.App.Services;

/// <summary>
/// Last line of defence for an exception nobody caught: it is logged and shown instead of ending the process. Faults
/// that leave the runtime itself unsafe are logged and allowed to terminate. Safe on any thread: unobserved task faults
/// arrive on the finalizer thread, where touching a window would throw and end the process, so
/// <paramref name="show"/> only ever runs on the UI thread.
/// </summary>
public sealed class UnhandledFaultReporter(Dispatcher dispatcher, string directory, Func<ILogger?> logger, Action<string, string> show)
{
    public bool Report(Exception exception, string source)
    {
        try { logger()?.LogError(exception, "Unhandled exception on {Source}", source); }
        catch { /* The host may already be disposed during shutdown; the file below still records the fault. */ }
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "unhandled-error.log"), $"{DateTimeOffset.Now:O} [{source}] {exception}{Environment.NewLine}");
        }
        catch { /* Nowhere left to report to. */ }
        if (exception is OutOfMemoryException or StackOverflowException or AccessViolationException) return false;
        dispatcher.BeginInvoke(() =>
        {
            // A failure to display this fault must not be reported as another fault, which would never end.
            try { show("Unexpected error: " + exception.Message, $"[{source}] {exception}"); }
            catch (Exception ex) { try { logger()?.LogError(ex, "Could not show an unhandled exception"); } catch { } }
        });
        return true;
    }
}
