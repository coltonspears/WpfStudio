using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Reflection;
using AvalonDock;
using AvalonDock.Layout;
using Microsoft.Extensions.DependencyInjection;
using WpfStudio.App;
using WpfStudio.App.Behaviors;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Database;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;
using WpfStudio.Database.Views;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;
using Xunit.Abstractions;

namespace WpfStudio.App.Tests;

public sealed class ShellSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LoadedShellBindsRealWorkspaceAndPreservesDockingDocumentsAndThemes()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WpfStudio;component/Resources/StudioTheme.xaml", UriKind.Relative) });
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await VerifyAsync(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { application.Shutdown(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "WpfStudio UI smoke" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(5));
    }

    private async Task VerifyAsync()
    {
        var root = FindRepository();
        var data = Path.Combine(Path.GetTempPath(), "WpfStudio-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(Dispatcher.CurrentDispatcher));
        var dialogs = new TestDialogs();
        services.AddSingleton<IFileDialogService>(dialogs); services.AddSingleton<IUserDialogService>(dialogs);
        services.AddSingleton(new DocumentStore(data)); services.AddSingleton(new SettingsStore(data));
        var configuration = typeof(ShellSmokeTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        services.AddSingleton(new WorkspaceClient(Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_WORKSPACE_HOST") ?? Path.Combine(root, $"src/WpfStudio.WorkspaceHost/bin/{configuration}/net10.0/WpfStudio.WorkspaceHost.dll")));
        services.AddSingleton<WorkspaceEditTransaction>(); services.AddSingleton<BuildService>();
        services.AddSingleton<WpfIndexService>(); services.AddSingleton<ScaffoldingService>(); services.AddSingleton<XamlCompletionService>();
        services.AddSingleton<DebugSession>(); services.AddSingleton<DebuggerViewModel>(); services.AddSingleton<TerminalViewModel>();
        services.AddSingleton<IConnectionProfileStore>(new ConnectionProfileStore(Path.Combine(data, "connections.json")));
        services.AddSingleton<IQueryRecoveryStore>(new QueryRecoveryStore(Path.Combine(data, "query-recovery.json")));
        services.AddDatabaseFeature(); services.AddSingleton<ShellViewModel>();
        await using var provider = services.BuildServiceProvider();
        var shell = provider.GetRequiredService<ShellViewModel>();
        var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        MainWindow? window = null;
        try
        {
            var clock = Stopwatch.StartNew();
            window = new MainWindow { DataContext = shell, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
            var manager = (DockingManager)window.FindName("DockManager");
            DockingBehavior.SetLayoutPath(manager, Path.Combine(data, "layout.xml"));
            window.Show(); await Idle();
            var startup = clock.Elapsed;
            output.WriteLine($"Shell construction to rendered idle: {startup.TotalMilliseconds:N0} ms");
            Assert.True(startup < TimeSpan.FromSeconds(5), "Shell render exceeded 5-second regression budget");
            Assert.Equal(8, manager.Layout.Descendents().OfType<LayoutAnchorable>().Count());
            Assert.Contains(manager.Layout.Descendents().OfType<LayoutDocument>(), d => d.ContentId == "Welcome");
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/welcome.png"));
            Application.Current.MainWindow = window;
            var answerPrompt = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().Single(candidate => candidate.Title == "Smoke prompt");
                dialog.Opacity = 0;
                dialog.DialogResult = true;
            }, DispatcherPriority.Loaded);
            Assert.Equal("CustomerView", await new DesktopDialogs().PromptAsync("Smoke prompt", "New item name", "CustomerView"));
            await answerPrompt.Task;
            var initialTree = new TaskCompletionSource<TimeSpan>();
            clock.Restart();
            shell.Explorer.CollectionChanged += (_, _) => { if (shell.Explorer.Count > 0) initialTree.TrySetResult(clock.Elapsed); };
            await shell.LoadWorkspaceAsync(Path.Combine(root, "samples/CounterApp/CounterApp.csproj"));
            output.WriteLine($"Initial tree: {(await initialTree.Task).TotalMilliseconds:N0} ms; semantic workspace ready: {clock.Elapsed.TotalMilliseconds:N0} ms");
            Assert.True(initialTree.Task.Result < TimeSpan.FromSeconds(5));
            Assert.NotNull(shell.Workspace);
            Assert.NotEmpty(shell.Projects);
            Assert.NotEmpty(shell.WpfItems);
            await shell.OpenDocumentAsync(Path.Combine(root, "samples/CounterApp/MainWindow.xaml"));
            await shell.OpenDocumentAsync(Path.Combine(root, "samples/CounterApp/CounterViewModel.cs"));
            await Idle();
            Assert.Equal(2, manager.Layout.Descendents().OfType<LayoutDocument>().Count());
            Assert.DoesNotContain(manager.Layout.Descendents().OfType<LayoutDocument>(), d => d.ContentId == "Welcome");
            var editor = Descendants<EditorSurface>(window).Single(e => ReferenceEquals(e.ViewModel, shell.ActiveDocument));
            Assert.True(editor.ActualWidth > 300);
            Assert.Contains("CounterViewModel", editor.Text);
            Assert.Same(shell.Explorer, ((TreeView)window.FindName("ExplorerTree")).ItemsSource);
            Assert.Same(shell.ActiveDocument, manager.ActiveContent);
            var selected = shell.ActiveDocument!;
            var originalText = selected.State.Content;
            selected.State.Content += "\n// Unsaved smoke-test change";
            await Idle();
            var document = manager.Layout.Descendents().OfType<LayoutDocument>().Single(d => ReferenceEquals(d.Content, selected));
            Assert.Contains("•", document.Title);
            manager.GetLayoutItemFromModel(document).CloseCommand.Execute(null);
            await Idle();
            Assert.Contains(selected, shell.Documents); // User cancelled save prompt.
            selected.State.Content = originalText;
            await Idle();
            var tool = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "WpfTools");
            tool.Hide(); shell.ShowToolCommand.Execute("WpfTools"); await Idle();
            Assert.False(tool.IsHidden);
            tool.ToggleAutoHide(); Assert.True(tool.IsAutoHidden);
            shell.ShowToolCommand.Execute("WpfTools"); await Idle(); Assert.False(tool.IsAutoHidden);
            shell.SaveLayoutCommand.Execute(null);
            var savedLayout = XDocument.Load(Path.Combine(data, "layout.xml"));
            var savedPane = savedLayout.Descendants("LayoutDocumentPane").First();
            savedPane.Add(new XElement("LayoutDocument", new XAttribute("ContentId", "removed-file.cs"), new XAttribute("Title", "Stale document")));
            savedLayout.Save(Path.Combine(data, "layout.xml"));
            var terminalContent = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "Terminal").Content;
            DockingBehavior.RestoreLayout(manager); await Idle();
            Assert.DoesNotContain(manager.Layout.Descendents().OfType<LayoutDocument>(), d => d.ContentId == "removed-file.cs");
            Assert.Equal(2, manager.Layout.Descendents().OfType<LayoutDocument>().Count());
            Assert.Same(terminalContent, manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "Terminal").Content);
            shell.ResetLayoutCommand.Execute(null); await Idle();
            Assert.Equal(2, manager.Layout.Descendents().OfType<LayoutDocument>().Count());
            foreach (var id in new[] { "Output", "Problems", "Search", "Debugger", "Database", "Terminal" })
            {
                shell.ShowToolCommand.Execute(id); await Idle();
                var pane = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == id);
                Assert.True(pane.IsSelected);
                Assert.False(string.IsNullOrWhiteSpace(pane.Title));
                Assert.True(((FrameworkElement)pane.Content).ActualHeight > 80);
                Assert.True(((FrameworkElement)pane.Content).IsVisible);
            }
            shell.ShowToolCommand.Execute("Database"); await Idle();
            var database = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "Database");
            var databaseView = (DatabasePane)database.Content;
            Assert.Same(shell.Database, databaseView.DataContext);
            Assert.Same(shell.Database.ExecuteCommand, databaseView.InputBindings.OfType<KeyBinding>().Single(k => k.Key == Key.F5).Command);
            shell.Database.SelectedDocument!.SqlText = "";
            databaseView.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(databaseView), Environment.TickCount, Key.F5) { RoutedEvent = Keyboard.KeyDownEvent });
            await Idle();
            Assert.Equal("Enter SQL to execute.", shell.Database.SelectedDocument.Status);
            Assert.False(shell.Debugger.IsActive);
            shell.ShowToolCommand.Execute("Output"); await Idle();
            shell.ActiveDocument = selected;
            foreach (var projectNode in shell.Explorer) projectNode.IsExpanded = true;
            await shell.BuildCommand.ExecuteAsync(null);
            await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/studio.png"));
            shell.ToggleThemeCommand.Execute(null); await Idle();
            Assert.Equal(Color.FromRgb(255, 255, 255), ((SolidColorBrush)Application.Current.FindResource("EditorBrush")).Color);
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/studio-light.png"));
            shell.ToggleThemeCommand.Execute(null); await Idle();
            shell.CommandPaletteCommand.Execute(null); await Idle();
            Assert.True(shell.IsPaletteOpen); Assert.NotEmpty(shell.PaletteResults);
            shell.ClosePaletteCommand.Execute(null);
            shell.NewWpfItemCommand.Execute(null); await Idle(); Assert.True(shell.IsScaffoldOpen); shell.CancelScaffoldCommand.Execute(null);
            shell.PreviewChanges.Add(new FileChange("CustomerView.xaml", "<Grid />", "<Grid><TextBlock Text=\"Hello\" /></Grid>", "Add customer view"));
            shell.SelectedPreviewChange = shell.PreviewChanges[0]; shell.IsPreviewOpen = true;
            await Idle();
            Assert.Contains(Descendants<TextBox>(window), box => box.Text == shell.SelectedPreviewChange.After && box.IsVisible);
            shell.CancelPreviewCommand.Execute(null);
            var performanceFixture = Path.Combine(root, "artifacts/performance/fixture/Performance.slnx");
            if (File.Exists(performanceFixture))
            {
                var firstTree = new TaskCompletionSource<TimeSpan>();
                var loadClock = Stopwatch.StartNew();
                shell.Explorer.CollectionChanged += (_, _) => { if (shell.Explorer.Count > 0) firstTree.TrySetResult(loadClock.Elapsed); };
                double maximumDispatcherGapMs = 0;
                var lastTick = Stopwatch.GetTimestamp();
                var heartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    maximumDispatcherGapMs = Math.Max(maximumDispatcherGapMs, Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds);
                    lastTick = now;
                }, Dispatcher.CurrentDispatcher);
                try { await shell.LoadWorkspaceAsync(performanceFixture); await Idle(); }
                finally { heartbeat.Stop(); }
                Assert.NotNull(shell.Workspace);
                Assert.Equal(20, shell.Projects.Count);
                Assert.True((await firstTree.Task) < TimeSpan.FromSeconds(5));
                var client = provider.GetRequiredService<WorkspaceClient>();
                using var worker = client.WorkerProcessId is int workerId ? Process.GetProcessById(workerId) : null;
                worker?.Refresh();
                var metrics = new
                {
                    Label = "WPF loaded-window integration test; shell timing excludes test-runner/process startup; UI working set includes test runner and prior smoke checks",
                    ShellRenderedIdleMs = Math.Round(startup.TotalMilliseconds),
                    ProjectCount = shell.Projects.Count,
                    SourceFileCount = shell.Workspace.Projects.Sum(p => p.Files.Count(f => !f.IsGenerated)),
                    InitialTreeMs = Math.Round(firstTree.Task.Result.TotalMilliseconds),
                    SemanticWorkspaceReadyMs = Math.Round(loadClock.Elapsed.TotalMilliseconds),
                    UiTestProcessWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                    WorkspaceWorkerWorkingSetBytes = worker?.WorkingSet64,
                    MaximumDispatcherHeartbeatGapMs = Math.Round(maximumDispatcherGapMs),
                    CapturedUtc = DateTime.UtcNow
                };
                var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(root, "artifacts/performance/ui-measurements.json"), json);
                output.WriteLine(json);
            }
            Assert.Empty(bindingErrors.Messages);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
            if (window is not null) { window.ClearValue(Interaction.CloseGuardProperty); window.Close(); }
            foreach (var message in bindingErrors.Messages) output.WriteLine(message);
        }
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static IEnumerable<T> Descendants<T>(DependencyObject owner) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(owner); i++)
        {
            var child = VisualTreeHelper.GetChild(owner, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "samples/CounterApp/CounterApp.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found");
    }
    private static void Screenshot(FrameworkElement content, string path)
    {
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class TestDialogs : IFileDialogService, IUserDialogService
    {
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(SaveDecision.Cancel);
        public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(null);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }
}
