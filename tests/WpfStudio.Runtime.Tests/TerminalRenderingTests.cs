using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;
using WpfStudio.Runtime.Terminal;

namespace WpfStudio.Runtime.Tests;

public sealed class TerminalRenderingTests
{
    [Fact]
    public async Task BundledXtermRendersRealConPtyOutputAndSurvivesReparenting()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var window = new Window { Width = 700, Height = 300, Opacity = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Loaded += async (_, _) =>
            {
                ConPtySession? session = null;
                try
                {
                    long hostBefore = Process.GetCurrentProcess().WorkingSet64;
                    var startup = Stopwatch.StartNew();
                    session = new ConPtySession(Environment.GetEnvironmentVariable("COMSPEC")!, "/Q", Path.GetTempPath());
                    var first = new TerminalSurface { Session = session }; window.Content = first;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    WebView2? browser = null;
                    while (!timeout.IsCancellationRequested)
                    {
                        await Task.Delay(100, timeout.Token);
                        browser = first.Content as WebView2;
                        if (browser?.CoreWebView2 is not null && await browser.ExecuteScriptAsync("typeof terminal") == "\"object\"") break;
                    }
                    Assert.NotNull(browser?.CoreWebView2);
                    var processes = browser!.CoreWebView2.Environment.GetProcessInfos();
                    long browserWorkingSet = 0;
                    foreach (var information in processes)
                    {
                        try { using var process = Process.GetProcessById(information.ProcessId); browserWorkingSet += process.WorkingSet64; }
                        catch (ArgumentException) { }
                    }
                    using var shellProcess = Process.GetProcessById(session.ProcessId);
                    var metrics = new
                    {
                        Label = "First live terminal in hidden WPF integration test; host delta includes JIT/runtime/test overhead; WebView2 environment memory may be shared between terminal sessions",
                        FirstTerminalReadyMs = Math.Round(startup.Elapsed.TotalMilliseconds),
                        TestHostBeforeBytes = hostBefore,
                        TestHostAfterBytes = Process.GetCurrentProcess().WorkingSet64,
                        TestHostDeltaBytes = Process.GetCurrentProcess().WorkingSet64 - hostBefore,
                        WebView2EnvironmentWorkingSetBytes = browserWorkingSet,
                        WebView2ProcessCount = processes.Count,
                        ShellWorkingSetBytes = shellProcess.WorkingSet64,
                        CapturedUtc = DateTime.UtcNow
                    };
                    for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
                    {
                        if (!Directory.Exists(Path.Combine(root.FullName, "src/WpfStudio.Runtime"))) continue;
                        var report = Path.Combine(root.FullName, "artifacts/performance/terminal-feature.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
                        break;
                    }
                    await session.WriteAsync("echo RENDER_READY\r", timeout.Token);
                    const string transcript = "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString()).join('\\n')";
                    string rendered = "";
                    while (!rendered.Contains("RENDER_READY")) { await Task.Delay(100, timeout.Token); rendered = await browser!.ExecuteScriptAsync(transcript); }
                    var second = new TerminalSurface { Session = session }; window.Content = second;
                    await Task.Delay(150, timeout.Token);
                    Assert.Same(browser, second.Content);
                    Assert.Contains("RENDER_READY", await browser!.ExecuteScriptAsync(transcript));
                    var beforeColumns = int.Parse(await browser.ExecuteScriptAsync("terminal.cols"));
                    window.Width = 1000; window.Height = 450;
                    await Task.Delay(250, timeout.Token);
                    Assert.True(int.Parse(await browser.ExecuteScriptAsync("terminal.cols")) > beforeColumns);
                    await session.WriteAsync("for /L %i in (1,1,1000) do @echo FLOW_%i\r", timeout.Token);
                    do { await Task.Delay(100, timeout.Token); rendered = await browser.ExecuteScriptAsync(transcript); } while (!rendered.Contains("FLOW_1000"));
                    Assert.Equal("true", await browser.ExecuteScriptAsync("search.findPrevious('RENDER_READY')"));
                    // Crash only this terminal's renderer, then verify controlled teardown rather than an ACK deadlock.
                    _ = browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}").ContinueWith(_ => { }, TaskScheduler.Default);
                    while (second.Content is not TextBlock) await Task.Delay(100, timeout.Token);
                    Assert.Contains("renderer stopped", ((TextBlock)second.Content).Text);
                    await shellProcess.WaitForExitAsync(timeout.Token);
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { if (session is not null) await session.DisposeAsync(); window.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            };
            window.Show(); Dispatcher.Run();
        }) { IsBackground = true, Name = "Terminal renderer integration test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(50));
    }
}
