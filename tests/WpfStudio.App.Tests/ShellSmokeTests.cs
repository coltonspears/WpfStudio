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
        services.AddDatabaseFeature(); services.AddStudioFeatures();
        services.AddSingleton(new WpfStudio.App.Features.ColtonGpt.AssistantSettingsStore(Path.Combine(data, "assistant")));
        services.AddSingleton<ShellViewModel>();
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
            Assert.Equal(9, manager.Layout.Descendents().OfType<LayoutAnchorable>().Count());
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
            foreach (var projectNode in shell.Explorer) projectNode.IsExpanded = true;
            await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/studio.png"));
            shell.ToggleThemeCommand.Execute(null); await Idle();
            Assert.Equal(Color.FromRgb(255, 255, 255), ((SolidColorBrush)Application.Current.FindResource("EditorBrush")).Color);
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/studio-light.png"));
            shell.ToggleThemeCommand.Execute(null); await Idle();
            await VerifyExpandedToolsAsync(root, window, manager, shell);
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
            await VerifyColtonGptViewsAsync(root, shell.ThemeName);
            Assert.Empty(bindingErrors.Messages);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
            if (window is not null) { window.ClearValue(Interaction.CloseGuardProperty); window.Close(); }
            foreach (var message in bindingErrors.Messages) output.WriteLine(message);
        }
    }

    private static async Task VerifyExpandedToolsAsync(string root, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        shell.WpfCategory = "Keyed resources";
        Assert.All(shell.WpfItems, item => Assert.NotNull(item.Key));
        shell.SelectedWpfItem = shell.WpfItems.First();
        await Idle();
        Assert.True(shell.CanRenameWpfResource);
        Assert.Contains("reference", shell.WpfUsageSummary);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/wpf-resource-details.png"));
        shell.WpfCategory = "All WPF items";
        shell.SelectedWpfItem = null;
        var editor = Descendants<EditorSurface>(window).First(e => e.IsVisible);
        editor.ContextMenu.PlacementTarget = editor;
        editor.ContextMenu.IsOpen = true; await Idle();
        Assert.Contains(editor.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Refactor"));
        Screenshot(editor.ContextMenu, Path.Combine(root, "artifacts/screenshots/editor-context-menu.png"));
        editor.ContextMenu.IsOpen = false;
        var breakpoint = new BreakpointViewModel { Path = shell.ActiveDocument!.State.Path, Line = 16 };
        shell.Debugger.Breakpoints.Add(breakpoint);
        Assert.Contains(shell.ActiveDocument.BreakpointMarkers, marker => marker.Line == 16 && !marker.Bound);
        breakpoint.Status = "Bound"; breakpoint.Condition = "Count > 3";
        Assert.Contains(shell.ActiveDocument.BreakpointMarkers, marker => marker.Bound && marker.Condition == "Count > 3");
        breakpoint.Enabled = false;
        Assert.Contains(shell.ActiveDocument.BreakpointMarkers, marker => !marker.Enabled);
        shell.ShowBreakpointsCommand.Execute(null); await Idle();
        Assert.Equal(2, shell.Debugger.SelectedTab);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/breakpoints.png"));
        shell.Debugger.Breakpoints.Remove(breakpoint);
        var sourceDocument = shell.ActiveDocument!;
        var editorCount = shell.Documents.Count;
        await shell.OpenPackagesCommand.ExecuteAsync(null); await Idle();
        var packages = manager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "Packages");
        Assert.True(((FrameworkElement)packages.Content).ActualHeight > 300);
        Assert.NotEmpty(shell.Features!.Packages.Installed);
        shell.Features.Packages.SelectedInstalled = shell.Features.Packages.Installed.First();
        await Idle();
        var packagePane = (FrameworkElement)packages.Content;
        foreach (var name in new[] { "InstallPackageButton", "RemovePackageButton" })
        {
            var action = Assert.IsAssignableFrom<FrameworkElement>(packagePane.FindName(name));
            Assert.True(action.ActualHeight > 0 && action.ActualWidth > 0, $"{name} must be arranged.");
            var bounds = action.TransformToAncestor(packagePane).TransformBounds(new Rect(action.RenderSize));
            Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= packagePane.ActualWidth + 0.5 && bounds.Bottom <= packagePane.ActualHeight + 0.5,
                $"{name} must be fully visible within the package pane: {bounds}, pane {packagePane.RenderSize}.");
        }
        Assert.Null(shell.ActiveDocument);
        Assert.Equal("Packages", shell.ActiveWorkbench);
        Assert.False(shell.SaveCommand.CanExecute(null));
        var originalSource = sourceDocument.State.Content;
        var savedSource = await File.ReadAllTextAsync(sourceDocument.State.Path);
        try
        {
            sourceDocument.State.Content = originalSource + Environment.NewLine + "// Workbench save command must not save an inactive editor.";
            await shell.SaveCommand.ExecuteAsync(null);
            Assert.True(sourceDocument.State.IsDirty);
            Assert.Equal(savedSource, await File.ReadAllTextAsync(sourceDocument.State.Path));
        }
        finally { sourceDocument.State.Content = originalSource; }
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/packages.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/packages-light.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        await shell.OpenGitCommand.ExecuteAsync(null); await Idle();
        Assert.True(shell.Features.Git.HasRepository);
        var git = manager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "Git");
        Assert.Null(shell.ActiveDocument);
        Assert.Equal("Git", shell.ActiveWorkbench);
        foreach (var tab in new[] { 1, 2, 3, 0 }) { shell.Features.Git.SelectedTab = tab; await Idle(); }
        shell.SaveLayoutCommand.Execute(null); await Idle();
        Assert.Same(git.Content, manager.ActiveContent);
        Assert.True(git.IsSelected);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/git.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/git-light.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        await shell.OpenSettingsCommand.ExecuteAsync(null); await Idle();
        Assert.True(shell.IsSettingsOpen);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/settings.png"));
        shell.CloseSettingsCommand.Execute(null);
        await shell.CloseActiveCommand.ExecuteAsync(null); await Idle();
        Assert.DoesNotContain(manager.Layout.Descendents().OfType<LayoutDocument>(), d => d.ContentId == "Git");
        Assert.Equal(editorCount, shell.Documents.Count);
        packages.IsSelected = true; packages.IsActive = true; await Idle();
        await shell.CloseActiveCommand.ExecuteAsync(null); await Idle();
        Assert.DoesNotContain(manager.Layout.Descendents().OfType<LayoutDocument>(), d => d.ContentId == "Packages");
        Assert.Equal(editorCount, shell.Documents.Count);
        Assert.Contains(sourceDocument, shell.Documents);
        shell.ShowToolCommand.Execute("Output");
        await Idle();
    }

    private static async Task VerifyColtonGptViewsAsync(string root, string restoreTheme)
    {
        using var http = new System.Net.Http.HttpClient();
        using var client = new WpfStudio.App.Features.ColtonGpt.OpenRouterClient(http);
        var settings = new WpfStudio.App.Features.ColtonGpt.AssistantSettingsViewModel(
            new WpfStudio.App.Features.ColtonGpt.AssistantSettingsStore(Path.Combine(Path.GetTempPath(), "ColtonGpt-View-" + Guid.NewGuid().ToString("N"))), client);
        using var assistant = new WpfStudio.App.Features.ColtonGpt.AssistantViewModel(client, settings);
        await assistant.InitializeAsync();
        settings.Models.Add(new("example/wpf-model", "Example coding model", 128_000));
        settings.ModelId = "example/wpf-model";
        assistant.SetEditorContext("CounterViewModel.cs", "public partial class CounterViewModel : ObservableObject { }", "[ObservableProperty] public partial int Count { get; set; }");
        assistant.ContextMode = "Selected text";
        assistant.Prompt = "Explain when I should use ObservableProperty.";
        assistant.Messages.Add(new("You", "Why is my binding not updating?") { Detail = "CounterViewModel.cs · selected text" });
        assistant.Messages.Add(new("ColtonGPT", "Check that your view's DataContext is the intended view model, and that the property raises PropertyChanged. CommunityToolkit's ObservableProperty generator handles the notification for you.") { Detail = "Example response · no network request" });
        var pane = new WpfStudio.App.Features.ColtonGpt.AssistantPane { DataContext = assistant };
        var settingsView = new WpfStudio.App.Features.ColtonGpt.AssistantSettingsView { DataContext = settings, Margin = new Thickness(24) };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(pane); Grid.SetColumn(settingsView, 1); content.Children.Add(settingsView);
        var window = new Window { Width = 1100, Height = 940, Content = content, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show(); await Idle();
            var password = Assert.Single(Descendants<PasswordBox>(settingsView));
            password.Password = "not-a-real-key";
            await Idle(); Assert.Equal("not-a-real-key", settings.PendingApiKey);
            settings.PendingApiKey = "";
            await Idle(); Assert.Equal("", password.Password);
            Assert.False(assistant.SendCommand.CanExecute(null));
            foreach (var theme in new[] { "Dark", "Light" })
            {
                ThemeService.Apply(theme); await Idle();
                Assert.True(pane.ActualWidth >= 280);
                Assert.True(settingsView.ActualWidth >= 320);
                foreach (var text in Descendants<TextBox>(content))
                {
                    if (text.Foreground is SolidColorBrush foreground && text.Background is SolidColorBrush background && background.Color.A > 0)
                        Assert.NotEqual(foreground.Color, background.Color);
                }
                Screenshot(content, Path.Combine(root, "artifacts/screenshots/coltongpt-" + theme.ToLowerInvariant() + ".png"));
            }
        }
        finally { window.Close(); ThemeService.Apply(restoreTheme); }
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
