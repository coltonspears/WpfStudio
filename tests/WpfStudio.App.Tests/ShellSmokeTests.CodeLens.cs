using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using WpfStudio.App.ViewModels;
using WpfStudio.Workspace;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    /// <summary>
    /// Lenses are requested, clicked and interrupted against a real worker and real bound collections. A fault in a
    /// background lens request must not reach the finalizer thread, and nothing may touch UI state off the UI thread.
    /// </summary>
    private async Task VerifyCodeLensThreadingAsync(string root, ShellViewModel shell, WorkspaceClient workspace)
    {
        var violations = new ConcurrentQueue<string>();
        var unobserved = new ConcurrentQueue<string>();
        void FirstChance(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is NotSupportedException or InvalidOperationException
                && (args.Exception.Message.Contains("Dispatcher thread", StringComparison.Ordinal)
                    || args.Exception.Message.Contains("different thread owns", StringComparison.Ordinal)
                    || args.Exception.Message.Contains("invalid thread", StringComparison.Ordinal)))
                violations.Enqueue(args.Exception.Message + Environment.NewLine + Environment.StackTrace);
        }
        void Unobserved(object? sender, UnobservedTaskExceptionEventArgs args) { args.SetObserved(); unobserved.Enqueue(args.Exception.ToString()); }
        // Why UnhandledFaultReporter marshals: on the finalizer thread this read would throw, and that exception ends the process.
        var offThread = await Task.Run(() => Record.Exception(() => Application.Current.MainWindow));
        Assert.Contains("different thread owns", Assert.IsType<InvalidOperationException>(offThread).Message);

        AppDomain.CurrentDomain.FirstChanceException += FirstChance;
        TaskScheduler.UnobservedTaskException += Unobserved;
        try
        {
            await shell.LoadWorkspaceAsync(Path.Combine(root, "samples/CounterApp/CounterApp.csproj"));
            await shell.OpenDocumentAsync(Path.Combine(root, "samples/CounterApp/CounterViewModel.cs"));
            await Idle();
            var document = shell.ActiveDocument!;
            await UntilAsync(() => document.CodeLenses.Count > 0, "the first lenses");

            // Clicking a count publishes references into the bound search results.
            var lens = document.CodeLenses[0];
            document.RequestCodeLens(lens, lens.Position);
            await UntilAsync(() => shell.SearchResults.Count > 0 || shell.Status.Contains("symbol reference", StringComparison.Ordinal), "the clicked lens's references");

            // Switching tabs, toggling the rows and closing an editor while a request runs.
            await shell.OpenDocumentAsync(Path.Combine(root, "samples/CounterApp/MainWindow.xaml.cs"));
            await Idle();
            var other = shell.ActiveDocument!;
            await Task.Delay(400);
            shell.ActiveDocument = document;
            shell.ShowCodeLens = false; await Idle();
            shell.ShowCodeLens = true; await Idle();
            shell.ShowInlineBlame = !shell.ShowInlineBlame; await Idle();
            shell.ShowInlineBlame = !shell.ShowInlineBlame; await Idle();
            await shell.CloseDocumentAsync(other, discardChanges: true);
            await Idle();

            // Typing while a request runs, then losing the worker while another waits to start, both fault the request.
            var original = document.State.Content;
            for (var edit = 0; edit < 4; edit++)
            {
                document.State.Content += "\n// typing";
                await Task.Delay(1900);
            }
            document.State.Content = original + "\n// worker exits";
            await Task.Delay(500);
            if (workspace.WorkerProcessId is { } worker) Process.GetProcessById(worker).Kill();
            await Task.Delay(3000);

            for (var pass = 0; pass < 3; pass++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                await Idle();
            }
            output.WriteLine($"Thread violations: {violations.Count}; unobserved faults: {unobserved.Count}");
            foreach (var item in violations.Take(5)) output.WriteLine(item);
            foreach (var item in unobserved.Take(5)) output.WriteLine(item);
            Assert.Empty(violations);
            Assert.Empty(unobserved);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= FirstChance;
            TaskScheduler.UnobservedTaskException -= Unobserved;
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string description)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90), "Timed out waiting for " + description);
            await Task.Delay(50);
        }
    }
}
