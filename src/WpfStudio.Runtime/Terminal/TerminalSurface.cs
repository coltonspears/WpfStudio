using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WpfStudio.Runtime.Terminal;

/// <summary>
/// Reusable presentation adapter: owns browser events, resize and render acknowledgements.
/// A renderer lives as long as its session, so WPF reparenting and tab virtualization retain scrollback.
/// </summary>
public sealed class TerminalSurface : ContentControl
{
    private static readonly Dictionary<ConPtySession, Renderer> Renderers = [];
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(nameof(Session), typeof(ConPtySession), typeof(TerminalSurface), new PropertyMetadata(null, Changed));
    public ConPtySession? Session { get => (ConPtySession?)GetValue(SessionProperty); set => SetValue(SessionProperty, value); }
    public TerminalSurface() { Loaded += (_, _) => _ = AttachAsync(); Unloaded += (_, _) => Content = null; }
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args) { var surface = (TerminalSurface)value; if (surface.IsLoaded) _ = surface.AttachAsync(); }
    private async Task AttachAsync()
    {
        if (Session is not { } session) return;
        try
        {
            if (!Renderers.TryGetValue(session, out var renderer))
            {
                renderer = new Renderer(session, Dispatcher);
                Renderers.Add(session, renderer);
                session.Closed += () => Dispatcher.BeginInvoke(() => { if (Renderers.Remove(session, out var removed)) removed.Dispose(); });
            }
            if (renderer.Owner is { } previous && previous != this) previous.Content = null;
            renderer.Owner = this;
            Content = renderer.Browser;
            await renderer.InitializeAsync();
            if (renderer.Browser.CoreWebView2 is { } core) core.PostWebMessageAsJson("{\"type\":\"fit\"}");
        }
        catch (Exception ex)
        {
            if (Renderers.Remove(session, out var failedRenderer)) failedRenderer.Dispose();
            await session.DisposeAsync();
            if (ReferenceEquals(Session, session))
                Content = new TextBlock { Text = "Terminal renderer could not start. Ensure the Microsoft Edge WebView2 Runtime is installed.\n" + ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
        }
    }

    private sealed class Renderer : IDisposable
    {
        private readonly ConPtySession session;
        private readonly System.Windows.Threading.Dispatcher dispatcher;
        private readonly CancellationTokenSource lifetime = new();
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? acknowledgement;
        private Task? initialization;
        private Task? pump;
        private int failed;
        private int disposed;
        public TerminalSurface? Owner { get; set; }
        public WebView2 Browser { get; } = new();
        public Renderer(ConPtySession session, System.Windows.Threading.Dispatcher dispatcher) { this.session = session; this.dispatcher = dispatcher; }
        public Task InitializeAsync() => initialization ??= InitializeCoreAsync();
        private async Task InitializeCoreAsync()
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsPasswordAutosaveEnabled = false;
            core.SetVirtualHostNameToFolderMapping("terminal.wpfstudio.local", Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://terminal.wpfstudio.local/", StringComparison.Ordinal)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += ProcessFailed;
            core.Navigate("https://terminal.wpfstudio.local/index.html");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            pump = PumpAsync();
        }
        private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!args.Source.StartsWith("https://terminal.wpfstudio.local/", StringComparison.Ordinal)) return;
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "ready": ready.TrySetResult(); break;
                    case "ack": acknowledgement?.TrySetResult(); break;
                    case "input": _ = SendInputAsync(root.GetProperty("data").GetString() ?? ""); break;
                    case "resize": session.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32()); break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or System.Runtime.InteropServices.COMException) { }
        }
        private async Task SendInputAsync(string value) { try { await session.WriteAsync(value, lifetime.Token); } catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException) { } }
        private async Task PumpAsync()
        {
            try
            {
                await foreach (var chunk in session.Output.ReadAllAsync(lifetime.Token))
                {
                    acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var json = JsonSerializer.Serialize(new { type = "output", data = Convert.ToBase64String(chunk) });
                    await dispatcher.InvokeAsync(() => Browser.CoreWebView2?.PostWebMessageAsJson(json));
                    await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token); // xterm acknowledges after parsing; pipe backpressure is bounded.
                }
                await dispatcher.InvokeAsync(() => Browser.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"ended\"}"));
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
            { await FailAsync("Terminal renderer stopped responding. " + ex.Message); }
        }
        private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.FrameRenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                _ = FailAsync("Terminal renderer stopped (" + args.ProcessFailedKind + ").");
        }
        private async Task FailAsync(string message)
        {
            if (Interlocked.Exchange(ref failed, 1) != 0 || Volatile.Read(ref disposed) != 0) return;
            var owner = Owner;
            lifetime.Cancel(); acknowledgement?.TrySetCanceled(); ready.TrySetCanceled();
            try { await session.DisposeAsync(); }
            finally
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (Renderers.Remove(session, out var renderer)) renderer.Dispose();
                    if (owner is not null && ReferenceEquals(owner.Session, session))
                        owner.Content = new TextBlock { Text = message + "\nClose this terminal and create a new session.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
                });
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel(); acknowledgement?.TrySetCanceled();
            ready.TrySetCanceled();
            if (Owner is not null) Owner.Content = null;
            if (Browser.CoreWebView2 is { } core) { core.WebMessageReceived -= OnMessage; core.ProcessFailed -= ProcessFailed; }
            Browser.Dispose(); lifetime.Dispose();
        }
    }
}
