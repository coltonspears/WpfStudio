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
using WpfStudio.Runtime.Inspection;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;
using Xunit.Abstractions;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests(ITestOutputHelper output)
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
        services.AddSingleton<WpfStudio.Contracts.Profiling.IMemoryProfiler>(new WpfStudio.Runtime.Profiling.MemoryProfilerClient(
            Path.Combine(root, $"src/WpfStudio.App/bin/{configuration}/net10.0-windows/ProfilingHost/WpfStudio.ProfilingHost.dll")));
        services.AddSingleton<WpfStudio.Runtime.Design.IPreviewClient>(_ => new WpfStudio.Runtime.Design.PreviewClient(
            Environment.GetEnvironmentVariable("WPFSTUDIO_PREVIEW_HOST_UNDER_TEST")));
        services.AddSingleton<WpfStudio.App.Features.Designer.DesignerViewModel>();
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
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_MEMORY_ONLY") == "1")
            {
                await VerifyMemoryProfilerAsync(root, window, manager, shell);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_PREVIEW_SOLUTION") is { Length: > 0 } previewSolution)
            {
                await VerifyExternalPreviewSolutionAsync(root, window, shell, previewSolution);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_LAYOUT_EDITING_ONLY") == "1")
            {
                await VerifyXamlLayoutEditingAsync(root, data, window, manager, shell);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_APPEARANCE_ONLY") == "1")
            {
                await VerifyPreviewAppearanceAsync(root, data, window, manager, shell);
                await VerifyRuntimeInspectionAsync(root, data, window, manager, shell);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_SCENARIOS_ONLY") == "1")
            {
                await VerifyPreviewScenariosAsync(root, data, window, manager, shell);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_EDITING_ONLY") == "1")
            {
                await VerifyXamlEditingAsync(root, data, window, shell, dialogs, provider.GetRequiredService<WorkspaceClient>());
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_SYMBOLS_ONLY") == "1")
            {
                await VerifyXamlSymbolsAsync(root, data, window, shell, dialogs);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_EVENTS_ONLY") == "1")
            {
                await VerifyXamlEventsAsync(root, data, window, shell);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_NAMES_ONLY") == "1")
            {
                await VerifyXamlNamesAsync(root, data, window, shell);
                await VerifyXamlNameRefactoringAsync(root, data, window, shell, dialogs);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_ANALYSIS_ONLY") == "1")
            {
                await VerifyProjectXamlAnalysisAsync(root, data, window, shell);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_INSPECTION_ONLY") == "1")
            {
                await VerifyRuntimeInspectionAsync(root, data, window, manager, shell);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_DESIGNER_ONLY") == "1")
            {
                await VerifyDesignerAsync(root, data, window, manager, shell);
                Assert.Empty(bindingErrors.Messages);
                return;
            }
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
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/sql.png"));
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
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/command-palette.png"));
            shell.PaletteQuery = ">build"; await Idle();
            Assert.Equal("Build", shell.PaletteResults.First().Label);
            shell.QuickOpenCommand.Execute(null); shell.PaletteQuery = "cvm"; await Idle();
            Assert.False(shell.IsCommandPalette);
            Assert.Equal("CounterViewModel.cs", shell.PaletteResults.First().Label);
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/quick-open.png"));
            shell.ClosePaletteCommand.Execute(null);
            shell.NewWpfItemCommand.Execute(null); await Idle(); Assert.True(shell.IsScaffoldOpen);
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/new-item.png"));
            shell.CancelScaffoldCommand.Execute(null);
            shell.PreviewChanges.Add(new FileChange("CustomerView.xaml", "<Grid />", "<Grid><TextBlock Text=\"Hello\" /></Grid>", "Add customer view"));
            shell.SelectedPreviewChange = shell.PreviewChanges[0]; shell.IsPreviewOpen = true;
            await Idle();
            var reviewDiff = Descendants<WpfStudio.App.Controls.Diff.DiffView>(window).Single(view => view.Name == "ReviewDiff");
            Assert.True(reviewDiff.IsVisible);
            Assert.Equal(shell.SelectedPreviewChange.After, reviewDiff.After);
            Assert.True(reviewDiff.ShowsSplit);
            Assert.Contains(reviewDiff.Layout!.Right, line => line.Kind == WpfStudio.App.Controls.Diff.DiffLineKind.Added && line.Text.Contains("Hello"));
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/change-preview.png"));
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
            await VerifyMemoryProfilerAsync(root, window, manager, shell);
            await VerifyProjectXamlAnalysisAsync(root, data, window, shell);
            await VerifyXamlEventsAsync(root, data, window, shell);
            await VerifyXamlNamesAsync(root, data, window, shell);
            await VerifyXamlNameRefactoringAsync(root, data, window, shell, dialogs);
            await VerifyXamlSymbolsAsync(root, data, window, shell, dialogs);
            await VerifyXamlEditingAsync(root, data, window, shell, dialogs, provider.GetRequiredService<WorkspaceClient>());
            await VerifyDesignerAsync(root, data, window, manager, shell);
            await VerifyXamlLayoutEditingAsync(root, data, window, manager, shell);
            await VerifyPreviewScenariosAsync(root, data, window, manager, shell);
            await VerifyPreviewAppearanceAsync(root, data, window, manager, shell);
            await VerifyRuntimeInspectionAsync(root, data, window, manager, shell);
            Assert.Empty(bindingErrors.Messages);
        }
        catch (Exception exception)
        {
            output.WriteLine("UI verification failed: " + exception);
            throw;
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
        foreach (var tab in new[] { 2, 1 }) { shell.Features.Git.SelectedTab = tab; await Idle(); }
        await shell.Features.Git.WhenLoadedAsync(); await Idle();
        Assert.NotEmpty(shell.Features.Git.VisibleHistory);
        Assert.Equal(shell.Features.Git.VisibleHistory[0], shell.Features.Git.SelectedCommit);
        Assert.Equal(shell.Features.Git.SelectedCommit!.Id, shell.Features.Git.CommitDetails?.Id);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/git-history.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/git-history-light.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        shell.Features.Git.SelectedTab = 0; await Idle();
        await shell.Features.Git.WhenLoadedAsync(); await Idle();
        if (shell.Features.Git.ChangeCount > 0) Assert.NotNull(shell.Features.Git.CurrentDiff);
        shell.SaveLayoutCommand.Execute(null); await Idle();
        Assert.Same(git.Content, manager.ActiveContent);
        Assert.True(git.IsSelected);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/git.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/git-light.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        // The same workbench at document size, as it appears with the tool panes closed.
        var gitWorkbench = new WpfStudio.App.Features.Git.GitPane { DataContext = shell.Features.Git };
        var gitWindow = new Window { Width = 1480, Height = 900, Content = gitWorkbench, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        try
        {
            gitWindow.Show(); await Idle();
            foreach (var (tab, name) in new[] { (1, "git-workbench-history"), (0, "git-workbench") })
            {
                shell.Features.Git.SelectedTab = tab; await Idle();
                await shell.Features.Git.WhenLoadedAsync(); await Idle();
                Screenshot(gitWorkbench, Path.Combine(root, $"artifacts/screenshots/{name}.png"));
            }
        }
        finally { gitWindow.Close(); }
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

    private static async Task VerifyXamlEditingAsync(string root, string data, MainWindow window, ShellViewModel shell, TestDialogs dialogs, WorkspaceClient client)
    {
        string directory = Path.Combine(data, "xaml-editing");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Formatting.xaml"), typingPath = Path.Combine(directory, "Typing.xaml");
        const string markup = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid.RowDefinitions><RowDefinition Height = 'Auto'/></Grid.RowDefinitions><StackPanel><TextBlock><Run Text='A'/><Run Text='B'/></TextBlock><TextBlock xml:space='preserve'> A <Run Text='B'/> C </TextBlock><Button  Content = 'Save &amp; Close' /><!-- keep  exact --></StackPanel></Grid>";
        string unsaved = markup.Replace("<Button  ", "<Button  Tag = 'draft' ", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, markup);
        await shell.OpenDocumentAsync(path);
        var editor = shell.ActiveDocument!;
        editor.State.Content = unsaved;
        await Idle();
        var surface = Descendants<EditorSurface>(window).Single(item => ReferenceEquals(item.ViewModel, editor));
        surface.Select(unsaved.IndexOf("Save", StringComparison.Ordinal), 4);
        var before = System.Windows.Markup.XamlReader.Parse(unsaved);
        var format = window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.F && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Same(shell.FormatCommand, format.Command);
        var timer = Stopwatch.StartNew();
        await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(format.Command).ExecuteAsync(null);
        long formatMs = timer.ElapsedMilliseconds;
        await Idle();
        string formatted = editor.State.Content;
        Assert.NotEqual(unsaved, formatted);
        Assert.Contains("\n", formatted);
        Assert.Equal("Save", surface.SelectedText);
        Assert.Equal("Save", editor.SelectedText);
        Assert.Contains("<!-- keep  exact -->", formatted);
        Assert.Contains("xml:space='preserve'> A ", formatted);
        Assert.Contains(" C </TextBlock>", formatted);
        // Compare actual WPF object serialization, including inline/text content,
        // rather than inferring semantic equivalence from normalized XML alone.
        var after = System.Windows.Markup.XamlReader.Parse(formatted);
        Assert.Equal(System.Windows.Markup.XamlWriter.Save(before), System.Windows.Markup.XamlWriter.Save(after));
        Assert.Equal(markup, await File.ReadAllTextAsync(path));
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-formatting.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-formatting-light.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(unsaved, editor.State.Content);
        editor.State.Content = markup;

        // Custom content requires project metadata, so this also verifies the
        // selected out-of-process worker instead of only the local fallback.
        string project = Path.Combine(directory, "Formatting.csproj");
        string customPath = Path.Combine(directory, "Custom.xaml");
        const string customMarkup = "<local:CustomBox xmlns:local='clr-namespace:FormattingFixture' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Button Content='A'/><Button Content='B'/></local:CustomBox>";
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(directory, "CustomBox.cs"), """
            using System.Collections.Generic;
            using System.Windows;
            using System.Windows.Markup;
            namespace FormattingFixture;
            [ContentProperty(nameof(Children))]
            public class CustomBox : FrameworkElement
            {
                public List<object> Children { get; } = new();
            }
            """);
        await File.WriteAllTextAsync(customPath, customMarkup);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
        await shell.LoadWorkspaceAsync(project);
        Assert.True(client.IsConnected);
        Assert.NotNull(client.WorkerProcessId);
        await shell.OpenDocumentAsync(customPath);
        var customEditor = shell.ActiveDocument!;
        await Idle();
        timer.Restart();
        await shell.FormatCommand.ExecuteAsync(null);
        long projectFormatMs = timer.ElapsedMilliseconds;
        Assert.Contains(">\n    <Button", customEditor.State.Content.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("/>\n    <Button", customEditor.State.Content.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(customMarkup, await File.ReadAllTextAsync(customPath));
        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(customMarkup, customEditor.State.Content);

        await File.WriteAllTextAsync(typingPath, "<Grid");
        await shell.OpenDocumentAsync(typingPath); await Idle();
        var typing = shell.ActiveDocument!;
        var input = Descendants<EditorSurface>(window).Single(item => ReferenceEquals(item.ViewModel, typing));
        input.CaretOffset = input.Document.TextLength;
        input.Document.UndoStack.ClearAll();
        input.TextArea.PerformTextInput(">");
        Assert.Equal("<Grid></Grid>", input.Text);
        Assert.Equal(6, input.CaretOffset);
        input.Document.UndoStack.Undo();
        Assert.Equal("<Grid", typing.State.Content);
        input.Document.UndoStack.Redo();
        Assert.Equal(6, input.CaretOffset);
        input.CaretOffset = 6;
        input.TextArea.PerformTextInput("\n");
        Assert.Contains("\n    \n", input.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(input.Text.IndexOf("    ", StringComparison.Ordinal) + 4, input.CaretOffset);
        input.Document.UndoStack.Undo();
        Assert.Equal("<Grid></Grid>", input.Text);

        typing.State.Content = "<Button Content=";
        input.CaretOffset = input.Document.TextLength;
        input.TextArea.PerformTextInput("\"");
        Assert.Equal("<Button Content=\"\"", input.Text);
        input.TextArea.PerformTextInput("Hello");
        input.TextArea.PerformTextInput("\"");
        Assert.Equal("<Button Content=\"Hello\"", input.Text);
        input.TextArea.PerformTextInput(">");
        Assert.Equal("<Button Content=\"Hello\"></Button>", input.Text);
        Assert.Equal(input.Text, typing.State.Content);
        Assert.Equal("<Grid", await File.ReadAllTextAsync(typingPath));
        typing.State.Content = "<Grid";
        Assert.Empty(dialogs.Errors);
        Directory.CreateDirectory(Path.Combine(root, "artifacts/performance"));
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/performance/xaml-formatting.json"), JsonSerializer.Serialize(new
        {
            Scope = "Loaded-window workstation sample: format a small unowned XAML buffer and a compiler-resolved custom content collection, preserve selection and compare loaded WPF content; caches may be warm.",
            FormatMs = formatMs,
            ProjectFormatMs = projectFormatMs,
            WorkerProcessId = client.WorkerProcessId,
            WorkerHostOverride = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_WORKSPACE_HOST"),
            CapturedUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task VerifyXamlSymbolsAsync(string root, string data, MainWindow window, ShellViewModel shell, TestDialogs dialogs)
    {
        string directory = Path.Combine(data, "xaml-symbols");
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Symbols.csproj");
        string view = Path.Combine(directory, "RefactorView.xaml"), closedView = Path.Combine(directory, "ClosedView.xaml");
        string modelPath = Path.Combine(directory, "Models.cs"), codePath = view + ".cs";
        const string modelSource = """
            namespace RefactorFixture;
            public class Customer
            {
                public string Name { get; set; } = "Customer";
                public string Label => Name;
            }
            public class Order { public string Name { get; set; } = "Order"; }
            """;
        const string codeSource = """
            using System.Windows;
            using System.Windows.Controls;
            namespace RefactorFixture;
            public partial class RefactorView : UserControl
            {
                private void OnSave(object sender, RoutedEventArgs args) { }
            }
            """;
        const string markup = """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                         xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                         xmlns:vm="clr-namespace:RefactorFixture" mc:Ignorable="d"
                         x:Class="RefactorFixture.RefactorView" d:DataContext="{d:DesignInstance vm:Customer}">
                <UserControl.Resources>
                    <DataTemplate DataType="{x:Type vm:Order}"><TextBlock Text="{Binding Name}" /></DataTemplate>
                </UserControl.Resources>
                <StackPanel>
                    <TextBlock Text="{Binding Name}" />
                    <Button Content="Customer" Click="OnSave" />
                </StackPanel>
            </UserControl>
            """;
        const string closedMarkup = """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                         xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                         xmlns:vm="clr-namespace:RefactorFixture" mc:Ignorable="d"
                         d:DataContext="{d:DesignInstance vm:Customer}">
                <TextBlock Text="{Binding N&#97;me}" />
            </UserControl>
            """;
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(view, markup);
        await File.WriteAllTextAsync(closedView, closedMarkup);
        await File.WriteAllTextAsync(modelPath, modelSource);
        await File.WriteAllTextAsync(codePath, codeSource);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
        await shell.LoadWorkspaceAsync(project);
        await shell.OpenDocumentAsync(modelPath);
        var model = shell.ActiveDocument!;
        string unsavedModel = modelSource.Replace("public string Label", "public int Unsaved => 42;\n    public string Label", StringComparison.Ordinal);
        model.State.Content = unsavedModel;
        await model.SyncAsync();
        await shell.OpenDocumentAsync(view);
        var editor = shell.ActiveDocument!;
        string unsavedView = markup.Replace("Content=\"Customer\"", "Content=\"Unsaved view\"", StringComparison.Ordinal);
        editor.State.Content = unsavedView;
        int propertyPosition = unsavedView.LastIndexOf("Binding Name", StringComparison.Ordinal) + "Binding ".Length;
        editor.Navigate(propertyPosition + 2);
        await Idle();
        Assert.DoesNotContain(shell.Documents, item => item.State.Path == closedView);
        var referenceClock = Stopwatch.StartNew();
        await shell.FindReferencesCommand.ExecuteAsync(null);
        long referenceMs = referenceClock.ElapsedMilliseconds;
        Assert.Contains(shell.SearchResults, hit => hit.Path == view);
        Assert.Contains(shell.SearchResults, hit => hit.Path == closedView);
        Assert.Contains(shell.SearchResults, hit => hit.Path == modelPath);
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-symbol-references.png"));

        // Review/cancel then accept the same cross-language rename through the real commands.
        var cancelled = await StartRenameAsync("FullName");
        Assert.Equal(3, shell.PreviewChanges.Count);
        Assert.All(shell.PreviewChanges, change => Assert.NotEqual(change.Before, change.After));
        Assert.Contains("Binding Name", shell.PreviewChanges.Single(change => change.Path == view).After); // Order.Name is unrelated.
        Assert.Contains("Binding FullName", shell.PreviewChanges.Single(change => change.Path == view).After);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-symbol-rename-review.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-symbol-rename-review-light.png"));
        shell.ToggleThemeCommand.Execute(null);
        shell.CancelPreviewCommand.Execute(null);
        await cancelled;
        Assert.Equal(unsavedView, editor.State.Content);
        Assert.Equal(unsavedModel, model.State.Content);

        var accepted = await StartRenameAsync("FullName");
        shell.AcceptPreviewCommand.Execute(null);
        await accepted;
        Assert.Contains("public string FullName", model.State.Content);
        Assert.Contains("Label => FullName", model.State.Content);
        Assert.Contains("public class Order { public string Name", model.State.Content);
        Assert.Contains("Binding FullName", editor.State.Content);
        Assert.Contains("Binding Name", editor.State.Content);
        Assert.Contains("Unsaved view", editor.State.Content);
        var closed = shell.Documents.Single(item => item.State.Path == closedView);
        Assert.Contains("Binding FullName", closed.State.Content);
        Assert.Equal(markup, await File.ReadAllTextAsync(view));
        Assert.Equal(closedMarkup, await File.ReadAllTextAsync(closedView));
        Assert.Equal(modelSource, await File.ReadAllTextAsync(modelPath));
        await model.SyncAsync(); await editor.RefreshAnalysisAsync(); await closed.RefreshAnalysisAsync();
        Assert.DoesNotContain(editor.Diagnostics.Concat(closed.Diagnostics), diagnostic => diagnostic.Id == "XAMLBIND001");
        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(unsavedView, editor.State.Content);
        Assert.Equal(unsavedModel, model.State.Content);
        Assert.Equal(closedMarkup, closed.State.Content);
        Assert.True(model.State.IsDirty);
        Assert.True(editor.State.IsDirty);
        Assert.False(closed.State.IsDirty);
        await model.SyncAsync();

        // Starting from C# must include the XAML event usage in both references and rename.
        await shell.OpenDocumentAsync(codePath);
        var code = shell.ActiveDocument!;
        await code.SyncAsync();
        code.Navigate(codeSource.IndexOf("OnSave", StringComparison.Ordinal) + 2);
        await Idle();
        await shell.FindReferencesCommand.ExecuteAsync(null);
        Assert.Contains(shell.SearchResults, hit => hit.Path == view);
        var eventRename = await StartRenameAsync("HandleSave");
        Assert.Equal(2, shell.PreviewChanges.Count);
        shell.AcceptPreviewCommand.Execute(null);
        await eventRename;
        Assert.Contains("void HandleSave", code.State.Content);
        Assert.Contains("Click=\"HandleSave\"", editor.State.Content);
        Assert.Equal(codeSource, await File.ReadAllTextAsync(codePath));
        Assert.Equal(markup, await File.ReadAllTextAsync(view));
        await code.SyncAsync(); await editor.RefreshAnalysisAsync();
        Assert.DoesNotContain(editor.Diagnostics, diagnostic => diagnostic.Id.StartsWith("XAMLEVENT", StringComparison.Ordinal));
        await shell.OpenDocumentAsync(view);
        editor.Navigate(editor.State.Content.IndexOf("HandleSave", StringComparison.Ordinal) + 2);
        await shell.GoToDefinitionCommand.ExecuteAsync(null);
        Assert.Equal(codePath, shell.ActiveDocument!.State.Path);
        Assert.Contains("HandleSave", code.State.Content.Split('\n')[code.State.CaretLine - 1]);
        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(codeSource, code.State.Content);
        Assert.Equal(unsavedView, editor.State.Content);
        Assert.Empty(dialogs.Errors);
        Directory.CreateDirectory(Path.Combine(root, "artifacts/performance"));
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/performance/xaml-symbol-references.json"), JsonSerializer.Serialize(new
        {
            Scope = "Loaded-window workstation sample: C#/XAML reference search with two XAML files after project load; caches may be warm.",
            ReferenceMs = referenceMs, CapturedUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
        // The assertions above establish preservation of unsaved work. Restore
        // this isolated fixture so later smoke workflows can switch workspaces.
        model.State.Content = modelSource;
        editor.State.Content = markup;
        await model.SyncAsync();
        await code.SyncAsync();

        async Task<Task> StartRenameAsync(string name)
        {
            dialogs.Prompts.Enqueue(name);
            var operation = shell.RenameSymbolCommand.ExecuteAsync(null);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!shell.IsPreviewOpen && !operation.IsCompleted) await Task.Delay(20, deadline.Token);
            Assert.True(shell.IsPreviewOpen, shell.Status + " | " + string.Join(" | ", dialogs.Errors));
            await Idle();
            return operation;
        }
    }

    private static async Task VerifyXamlNamesAsync(string root, string data, MainWindow window, ShellViewModel shell)
    {
        string directory = Path.Combine(data, "named-elements");
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "NamedElements.csproj");
        string view = Path.Combine(directory, "CustomerView.xaml");
        const string markup = """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="NamedElements.CustomerView">
                <StackPanel Margin="24">
                    <TextBlock Text="Customer details" FontSize="24" />
                    <TextBox x:Name="CustomerName" Text="Ada Lovelace" />
                    <TextBox Name="CustomerEmail" Text="ada@example.test" />

                    <!-- ElementName uses the named control in this XAML scope. -->
                    <TextBlock Text="{Binding ElementName=CustmerName, Path=Text}" />
                    <TextBlock Text="{Binding ElementName=CustomerEmail, Path=Text}" />
                </StackPanel>
            </UserControl>
            """;
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(view, markup);
        await File.WriteAllTextAsync(Path.Combine(directory, "CustomerView.xaml.cs"),
            "namespace NamedElements; public partial class CustomerView : System.Windows.Controls.UserControl { }");
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
        await shell.LoadWorkspaceAsync(project);
        await shell.OpenDocumentAsync(view);
        var editor = shell.ActiveDocument!;
        await editor.RefreshAnalysisAsync();
        var issue = Assert.Single(editor.Diagnostics);
        Assert.StartsWith("XAMLNAME", issue.Id);
        Assert.Equal("CustmerName", markup.Substring(issue.Start, issue.Length));
        editor.Navigate(issue.Start + 4);
        shell.ShowToolCommand.Execute("Problems");
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-named-elements.png"));

        var completion = await editor.CompleteAsync(issue.Start + 4);
        Assert.Equal(issue.Start, completion.Start);
        Assert.Equal(issue.Length, completion.Length);
        Assert.Contains(completion.Items, item => item.InsertText == "CustomerName");
        Assert.Contains(completion.Items, item => item.InsertText == "CustomerEmail");
        await editor.RefreshQuickFixesAsync(issue.Start + 4);
        var action = Assert.Single(editor.QuickFixes);
        await action.ApplyCommand.ExecuteAsync(action.Action);
        string corrected = markup.Replace("CustmerName", "CustomerName", StringComparison.Ordinal);
        Assert.Equal(corrected, editor.State.Content);
        Assert.Equal(markup, await File.ReadAllTextAsync(view));
        await editor.RefreshAnalysisAsync();
        Assert.Empty(editor.Diagnostics);
        int reference = corrected.IndexOf("ElementName=CustomerName", StringComparison.Ordinal) + "ElementName=".Length;
        var hover = await editor.HoverAsync(reference + 2);
        Assert.NotNull(hover);
        Assert.Contains("CustomerName", hover.Text);
        Assert.Contains("TextBox", hover.Text);
        editor.Navigate(reference + 2);
        await shell.GoToDefinitionCommand.ExecuteAsync(null);
        Assert.Same(editor, shell.ActiveDocument);
        Assert.Equal(corrected.IndexOf("CustomerName", StringComparison.Ordinal), editor.State.CaretOffset);

        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(markup, editor.State.Content);
        Assert.Same(editor, shell.ActiveDocument);
        Assert.Contains(editor, shell.Documents);
        // Undo publishes a new resource snapshot. Drain its queued invalidation
        // before awaiting an explicit analysis, just as the editor dispatcher does.
        await Idle();
        await editor.RefreshAnalysisAsync();
        Assert.StartsWith("XAMLNAME", Assert.Single(editor.Diagnostics).Id);
    }

    private static async Task VerifyXamlNameRefactoringAsync(string root, string data, MainWindow window, ShellViewModel shell, TestDialogs dialogs)
    {
        string directory = Path.Combine(data, "name-refactoring");
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "NameRefactoring.csproj");
        string view = Path.Combine(directory, "ContactView.xaml");
        string codePath = Path.Combine(directory, "ContactView.xaml.cs");
        const string markup = """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="NameRefactoring.ContactView">
                <StackPanel Margin="24">
                    <TextBlock Text="Contact details" FontSize="24" />
                    <TextBox Name="ContactEmail" Text="ada@example.test" />
                    <TextBlock Text="{Binding ElementName=ContactEmail, Path=Text}" />
                </StackPanel>
            </UserControl>
            """;
        const string code = """
            namespace NameRefactoring;
            public partial class ContactView : System.Windows.Controls.UserControl
            {
                public string ReadEmail() => ContactEmail.Text;
            }
            """;
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(view, markup);
        await File.WriteAllTextAsync(codePath, code);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Build, "Release"))).ExitCode);
        await shell.LoadWorkspaceAsync(project);
        var generatedNameFiles = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.NotEmpty(generatedNameFiles);
        await shell.OpenDocumentAsync(view);
        var editor = shell.ActiveDocument!;
        int reference = markup.IndexOf("ElementName=ContactEmail", StringComparison.Ordinal) + "ElementName=".Length;
        editor.Navigate(reference + 2);
        await Idle();
        await shell.FindReferencesCommand.ExecuteAsync(null);
        Assert.Contains(shell.SearchResults, location => location.Path == view);
        Assert.Contains(shell.SearchResults, location => location.Path == codePath);
        Assert.DoesNotContain(shell.SearchResults, location => location.Path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || location.Path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase));
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-name-references.png"));

        var cancelled = await StartRenameAsync();
        Assert.Equal(2, shell.PreviewChanges.Count);
        Assert.Contains(shell.PreviewChanges, change => change.Path == view && change.After.Contains("Name=\"EmailInput\"", StringComparison.Ordinal)
            && change.After.Contains("ElementName=EmailInput", StringComparison.Ordinal));
        Assert.Contains(shell.PreviewChanges, change => change.Path == codePath && change.After.Contains("EmailInput.Text", StringComparison.Ordinal));
        shell.SelectedPreviewChange = shell.PreviewChanges.Single(change => change.Path == view);
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-name-rename-review.png"));
        shell.CancelPreviewCommand.Execute(null);
        await cancelled;
        Assert.Equal(markup, editor.State.Content);
        Assert.DoesNotContain(shell.Documents, document => document.State.Path == codePath);

        var accepted = await StartRenameAsync();
        shell.AcceptPreviewCommand.Execute(null);
        await accepted;
        var codeEditor = Assert.Single(shell.Documents, document => document.State.Path == codePath);
        Assert.Equal(markup.Replace("ContactEmail", "EmailInput", StringComparison.Ordinal), editor.State.Content);
        Assert.Equal(code.Replace("ContactEmail", "EmailInput", StringComparison.Ordinal), codeEditor.State.Content);
        Assert.DoesNotContain("refresh is pending", shell.Status);
        await Idle();
        await codeEditor.RefreshAnalysisAsync();
        Assert.DoesNotContain(codeEditor.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        var renamedCompletion = await codeEditor.CompleteAsync(codeEditor.State.Content.IndexOf("EmailInput", StringComparison.Ordinal) + "EmailInput".Length);
        Assert.Contains(renamedCompletion.Items, item => item.DisplayText == "EmailInput");
        Assert.DoesNotContain(renamedCompletion.Items, item => item.DisplayText == "ContactEmail");
        shell.ActiveDocument = codeEditor;
        codeEditor.Navigate(codeEditor.State.Content.IndexOf("EmailInput", StringComparison.Ordinal) + 2);
        await shell.GoToDefinitionCommand.ExecuteAsync(null);
        Assert.Same(editor, shell.ActiveDocument);
        Assert.Equal(editor.State.Content.IndexOf("EmailInput", StringComparison.Ordinal), editor.State.CaretOffset);
        Assert.Equal(markup, await File.ReadAllTextAsync(view));
        Assert.Equal(code, await File.ReadAllTextAsync(codePath));
        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(markup, editor.State.Content);
        Assert.Equal(code, codeEditor.State.Content);
        await codeEditor.SyncAsync();
        await Idle();
        await codeEditor.RefreshAnalysisAsync();
        Assert.DoesNotContain(codeEditor.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        foreach (var generatedFile in generatedNameFiles)
            Assert.Equal(generatedFile.Value, await File.ReadAllBytesAsync(generatedFile.Key));

        // The reverse direction must use the same generated-field identity.
        await shell.OpenDocumentAsync(codePath);
        codeEditor.Navigate(code.IndexOf("ContactEmail", StringComparison.Ordinal) + 2);
        await Idle();
        await shell.FindReferencesCommand.ExecuteAsync(null);
        Assert.Contains(shell.SearchResults, location => location.Path == view);
        Assert.Contains(shell.SearchResults, location => location.Path == codePath);
        Assert.Empty(dialogs.Errors);

        // Ordinary XAML typing changes code-behind semantics without a reviewed
        // rename, save, build, or reload. Keep the C# version unchanged first.
        long codeVersion = codeEditor.State.Version;
        editor.State.Content = markup.Replace("<TextBox Name=\"ContactEmail\" Text=\"ada@example.test\" />",
            "<PasswordBox Name=\"ContactEmail\" />", StringComparison.Ordinal)
            .Replace("Path=Text", "Path=Password", StringComparison.Ordinal);
        await editor.SyncAsync();
        await Idle();
        await codeEditor.RefreshAnalysisAsync();
        Assert.Equal(codeVersion, codeEditor.State.Version);
        Assert.Contains(codeEditor.Diagnostics, diagnostic => diagnostic.Id == "CS1061"
            && diagnostic.Message.Contains("PasswordBox", StringComparison.Ordinal));
        shell.ActiveDocument = codeEditor;
        codeEditor.Navigate(codeEditor.State.Content.IndexOf("ContactEmail.Text", StringComparison.Ordinal) + "ContactEmail.".Length);
        shell.ShowToolCommand.Execute("Problems");
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-live-fields.png"));

        editor.State.Content = editor.State.Content.Replace("ContactEmail", "SecretInput", StringComparison.Ordinal);
        codeEditor.State.Content = code.Replace("ContactEmail.Text", "SecretInput.Password", StringComparison.Ordinal);
        await editor.SyncAsync();
        await codeEditor.SyncAsync();
        await Idle();
        await codeEditor.RefreshAnalysisAsync();
        Assert.DoesNotContain(codeEditor.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        var directCompletion = await codeEditor.CompleteAsync(codeEditor.State.Content.IndexOf("SecretInput", StringComparison.Ordinal) + "SecretInput".Length);
        Assert.Contains(directCompletion.Items, item => item.DisplayText == "SecretInput");
        Assert.DoesNotContain(directCompletion.Items, item => item.DisplayText == "ContactEmail");
        codeEditor.Navigate(codeEditor.State.Content.IndexOf("SecretInput", StringComparison.Ordinal) + 2);
        await shell.GoToDefinitionCommand.ExecuteAsync(null);
        Assert.Same(editor, shell.ActiveDocument);
        Assert.Equal(editor.State.Content.IndexOf("SecretInput", StringComparison.Ordinal), editor.State.CaretOffset);
        Assert.Equal(markup, await File.ReadAllTextAsync(view));
        Assert.Equal(code, await File.ReadAllTextAsync(codePath));
        foreach (var generatedFile in generatedNameFiles)
            Assert.Equal(generatedFile.Value, await File.ReadAllBytesAsync(generatedFile.Key));

        // Leave this fixture clean so the next workspace can open without the
        // smoke-test dialog's deliberate Cancel response to unsaved buffers.
        editor.State.Content = markup;
        codeEditor.State.Content = code;
        await editor.SyncAsync();
        await codeEditor.SyncAsync();
        Assert.False(editor.State.IsDirty);
        Assert.False(codeEditor.State.IsDirty);

        async Task<Task> StartRenameAsync()
        {
            dialogs.Prompts.Enqueue("EmailInput");
            var operation = shell.RenameSymbolCommand.ExecuteAsync(null);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!shell.IsPreviewOpen && !operation.IsCompleted) await Task.Delay(20, deadline.Token);
            Assert.True(shell.IsPreviewOpen, shell.Status + " | " + string.Join(" | ", dialogs.Errors));
            await Idle();
            return operation;
        }
    }

    private static async Task VerifyXamlEventsAsync(string root, string data, MainWindow window, ShellViewModel shell)
    {
        string directory = Path.Combine(data, "xaml-events");
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Events.csproj"), view = Path.Combine(directory, "EventView.xaml"),
            code = Path.Combine(directory, "EventView.xaml.cs");
        const string source = """
            using System.Windows;
            using System.Windows.Controls;
            namespace EventFixture;
            public partial class EventView : UserControl
            {
                private void OnSave(object sender, RoutedEventArgs e) { }
                private void Wrong(int value) { }
            }
            """;
        const string markup = """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="EventFixture.EventView">
                <StackPanel>
                    <Button Content="Existing handler" Click="OnSave" />
                    <Button Content="Wrong signature" MouseEnter="Wrong" />
                    <Button x:Name="CreateButton" Content="Create handler" Click="CreateClick" />
                </StackPanel>
            </UserControl>
            """;
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(view, markup);
        await File.WriteAllTextAsync(code, source);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        await shell.LoadWorkspaceAsync(project);
        await shell.OpenDocumentAsync(view);
        var editor = shell.ActiveDocument!;
        shell.ShowToolCommand.Execute("Problems");
        await Idle();
        await editor.RefreshAnalysisAsync();
        Assert.Contains(editor.Diagnostics, issue => issue.Id.StartsWith("XAMLEVENT", StringComparison.Ordinal) && markup.Substring(issue.Start, issue.Length) == "Wrong");
        Assert.Contains(editor.Diagnostics, issue => issue.Id.StartsWith("XAMLEVENT", StringComparison.Ordinal) && markup.Substring(issue.Start, issue.Length) == "CreateClick");
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-event-diagnostics.png"));
        int savePosition = markup.IndexOf("OnSave", StringComparison.Ordinal);
        var completionClock = Stopwatch.StartNew();
        var completions = await editor.CompleteAsync(savePosition + 3);
        long completionMs = completionClock.ElapsedMilliseconds;
        Assert.Contains(completions.Items, item => item.InsertText == "OnSave");
        Assert.DoesNotContain(completions.Items, item => item.InsertText == "Wrong");
        Assert.Contains("OnSave", (await editor.HoverAsync(savePosition + 2))!.Text);
        editor.Navigate(savePosition + 2);
        await shell.GoToDefinitionCommand.ExecuteAsync(null);
        Assert.Equal(code, shell.ActiveDocument!.State.Path);
        Assert.Contains("OnSave", shell.ActiveDocument.State.Content.Split('\n')[shell.ActiveDocument.State.CaretLine - 1]);
        await shell.ActiveDocument.SyncAsync();

        await shell.OpenDocumentAsync(view);
        int createPosition = markup.IndexOf("CreateClick", StringComparison.Ordinal);
        editor.Navigate(createPosition + 2);
        await Idle();
        var surface = Descendants<EditorSurface>(window).Single(item => ReferenceEquals(item.ViewModel, editor));
        surface.ContextMenu.PlacementTarget = surface;
        surface.ContextMenu.IsOpen = true;
        await Idle();
        await editor.RefreshQuickFixesAsync(surface.CaretOffset);
        var action = Assert.Single(editor.QuickFixes, fix => fix.Action.AdditionalEdits?.Any(edit => edit.Path == code) == true);
        Assert.Empty(action.Action.Edit.Edits);
        var fixes = surface.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Quick fixes"));
        Assert.True(fixes.IsEnabled);
        fixes.IsSubmenuOpen = true;
        await Idle();
        var menu = Assert.IsType<MenuItem>(fixes.ItemContainerGenerator.ContainerFromItem(action));
        Assert.Equal(action.Title, menu.Header);
        Assert.Same(action.Action, menu.CommandParameter);
        Screenshot(menu, Path.Combine(root, "artifacts/screenshots/xaml-event-actions.png"));
        // Exercise the actual bound menu command, including its code-action identity guard.
        Assert.True(menu.Command.CanExecute(menu.CommandParameter));
        await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(menu.Command).ExecuteAsync(menu.CommandParameter);
        surface.ContextMenu.IsOpen = false;
        Assert.True(shell.ActiveDocument?.State.Path == code, editor.LanguageStatus);
        Assert.Contains("CreateClick", shell.ActiveDocument!.State.Content);
        var generated = shell.ActiveDocument!;
        Assert.True(generated.State.IsDirty);
        Assert.False(editor.State.IsDirty);
        Assert.Equal(markup, editor.State.Content);
        Assert.Equal(source, await File.ReadAllTextAsync(code));
        Assert.Equal(markup, await File.ReadAllTextAsync(view));
        await generated.SyncAsync();
        await editor.RefreshAnalysisAsync();
        Assert.DoesNotContain(editor.Diagnostics, issue => issue.Message.Contains("CreateClick", StringComparison.Ordinal));
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-event-generated.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-event-generated-light.png"));
        shell.ToggleThemeCommand.Execute(null);

        await shell.OpenDocumentAsync(view);
        editor.Navigate(createPosition + 2);
        await shell.GoToDefinitionCommand.ExecuteAsync(null);
        Assert.Equal(code, shell.ActiveDocument!.State.Path);
        Assert.Contains("CreateClick", generated.State.Content.Split('\n')[generated.State.CaretLine - 1]);
        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(source, generated.State.Content);
        Assert.False(generated.State.IsDirty);
        await generated.SyncAsync();
        await editor.RefreshAnalysisAsync();
        Assert.Contains(editor.Diagnostics, issue => issue.Message.Contains("CreateClick", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(root, "artifacts/performance"));
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/performance/xaml-event-completion.json"), JsonSerializer.Serialize(new
        {
            Scope = "Loaded-window workstation sample: handler completion after project load and initial analysis; caches may be warm.",
            CompletionMs = completionMs, CapturedUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task VerifyProjectXamlAnalysisAsync(string root, string data, MainWindow window, ShellViewModel shell)
    {
        var folder = Path.Combine(data, "project-xaml-analysis");
        Directory.CreateDirectory(folder);
        const string markup = "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:vm=\"clr-namespace:Fixture\" mc:Ignorable=\"d\" d:DataContext=\"{d:DesignInstance vm:Model}\">\n  <TextBlock Text=\"{Binding Name}\" />\n</UserControl>";
        string shared = Path.Combine(folder, "Shared.xaml");
        await File.WriteAllTextAsync(shared, markup);
        foreach (string name in new[] { "Customers", "Orders" })
        {
            var projectFolder = Path.Combine(folder, name);
            Directory.CreateDirectory(projectFolder);
            var project = Path.Combine(projectFolder, name + ".csproj");
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup><ItemGroup><Page Include=\"../Shared.xaml\" Link=\"Shared.xaml\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(projectFolder, "Model.cs"), $"namespace Fixture; public class Model {{ public string {(name == "Customers" ? "Name" : "Title")} => \"Sample\"; }}");
            Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        }
        var solution = Path.Combine(folder, "Fixture.slnx");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Customers/Customers.csproj\"/><Project Path=\"Orders/Orders.csproj\"/></Solution>");
        await shell.LoadWorkspaceAsync(solution);
        var analysisClock = Stopwatch.StartNew();
        await shell.RefreshProjectXamlAnalysisCommand.ExecuteAsync(null);
        long initialScanMs = analysisClock.ElapsedMilliseconds;
        var issue = Assert.Single(shell.Diagnostics, diagnostic => diagnostic.Id == "XAMLBIND001" && diagnostic.Path == shared);
        Assert.Equal("Orders", issue.ProjectName);
        Assert.DoesNotContain(shell.Documents, document => document.State.Path == shared);
        Assert.Contains(issue, shell.WpfIssues);
        await shell.NavigateDiagnosticCommand.ExecuteAsync(issue);
        var editor = shell.ActiveDocument!;
        Assert.Equal("Orders", editor.XamlProject!.Name);
        await editor.RefreshAnalysisAsync();
        await shell.RefreshProjectXamlAnalysisCommand.ExecuteAsync(null);
        Assert.Contains("XAML checked:", shell.XamlAnalysisStatus);
        shell.ShowToolCommand.Execute("Problems");
        await Idle();
        var selector = Descendants<ComboBox>(window).Single(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "XAML project context" && ReferenceEquals(combo.DataContext, editor));
        Assert.Same(editor.XamlProject, selector.SelectedItem);
        Assert.Equal(2, selector.Items.Count);
        Assert.True(selector.IsVisible);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-project-diagnostics.png"));

        // A closed C# file changes outside the editor. The watcher must update
        // the project's types and clear the XAML issue without a manual refresh.
        var modelPath = Path.Combine(folder, "Orders/Model.cs");
        analysisClock.Restart();
        await File.WriteAllTextAsync(modelPath, "namespace Fixture; public class Model { public string Name => \"Sample\"; }");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (shell.IsProjectXamlAnalysisRunning || shell.Diagnostics.Any(diagnostic => diagnostic.Id == "XAMLBIND001" && diagnostic.Path == shared)
            || !shell.XamlAnalysisStatus.Contains("checked", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(50, deadline.Token);
        await editor.RefreshAnalysisAsync();
        Assert.Empty(editor.Diagnostics);
        long changedSourceMs = analysisClock.ElapsedMilliseconds;
        Assert.DoesNotContain(shell.Documents, document => document.State.Path == modelPath);
        selector.SelectedItem = editor.XamlProjects.Single(project => project.Name == "Customers");
        Assert.Equal("Customers", editor.XamlProject!.Name);
        await editor.RefreshAnalysisAsync();
        Assert.Empty(editor.Diagnostics);
        Assert.False(editor.State.IsDirty);
        Directory.CreateDirectory(Path.Combine(root, "artifacts/performance"));
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/performance/xaml-project-analysis.json"), JsonSerializer.Serialize(new
        {
            Scope = "Loaded-window workstation sample: two projects sharing one XAML file. Explicit check follows workspace loading; compilation and file caches may be warm.",
            ProjectCount = 2, XamlFileContexts = 2, InitialProjectScanMs = initialScanMs,
            ClosedModelChangeToDiagnosticsMs = changedSourceMs, CapturedUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task VerifyRuntimeInspectionAsync(string root, string data, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        var folder = Path.Combine(data, "runtime-inspection");
        Directory.CreateDirectory(folder);
        var projectDirectory = Path.Combine(data, "inspection-project");
        Directory.CreateDirectory(projectDirectory);
        var fixtureDirectory = Path.Combine(root, "tests/WpfStudio.Inspection.Tests/Fixtures/App");
        foreach (var file in Directory.EnumerateFiles(fixtureDirectory)) File.Copy(file, Path.Combine(projectDirectory, Path.GetFileName(file)));
        var projectPath = Path.Combine(projectDirectory, "WpfStudio.InspectionFixture.csproj");
        var projectXml = XDocument.Load(projectPath);
        projectXml.Descendants("TargetFrameworks").Single().ReplaceWith(new XElement("TargetFramework", "net10.0-windows"));
        projectXml.Descendants("XamlDebuggingInformation").Single().Value = "false";
        projectXml.Root!.Element("PropertyGroup")!.Add(new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"), new XElement("LangVersion", "latest"));
        projectXml.Save(projectPath);
        var originalProject = await File.ReadAllTextAsync(projectPath);
        Directory.CreateDirectory(Path.Combine(projectDirectory, "Properties"));
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Properties/launchSettings.json"),
            JsonSerializer.Serialize(new { profiles = new { Inspection = new { commandName = "Project", commandLineArgs = "\"" + folder + "\"" } } }));
        Process? process = null;
        try
        {
            await shell.LoadWorkspaceAsync(projectPath);
            shell.LaunchProfile = "Inspection";
            shell.LiveInspection.AutoRefresh = false;
            await shell.RunWithInspectionCommand.ExecuteAsync(null);
            using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(folder, "ready.json"))) await Task.Delay(25, readyDeadline.Token);
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "ready.json")));
            process = Process.GetProcessById(ready.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.True(shell.LiveInspection.IsConnected, shell.Status + " " + shell.LiveInspection.Status);
            Assert.Equal(originalProject, await File.ReadAllTextAsync(projectPath));
            var model = shell.LiveInspection;
            model.AutoRefresh = false;
            shell.ShowToolCommand.Execute("LiveInspection");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            RuntimeNodeViewModel? element;
            while ((element = RuntimeNodes(model.Tree).FirstOrDefault(n => n.Node.Name == "BadText")) == null)
            {
                await Task.Delay(50, deadline.Token);
                await model.RefreshCommand.ExecuteAsync(null);
            }
            await VerifyRuntimeAppearanceAsync(root, window, manager, shell);
            if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_XAML_APPEARANCE_ONLY") == "1")
            {
                await model.DisconnectCommand.ExecuteAsync(null);
                Assert.False(process.HasExited);
                return;
            }
            model.SelectedNode = element;
            while (model.Bindings.Count == 0) await Task.Delay(25, deadline.Token);
            Assert.Contains(model.Bindings, p => p.Binding?.Status == "PathError");
            Assert.Contains("RuntimeViewModel", model.DataContextDescription);
            Assert.NotEmpty(model.Traces);
            WpfStudio.Runtime.Inspection.InspectionBindingIssue? runtimeIssue;
            while ((runtimeIssue = model.IssueGroups.SelectMany(group => group.Instances).FirstOrDefault(issue => issue.NodeId == element.Node.Id && issue.State == "Active")) == null)
            {
                await model.RefreshCommand.ExecuteAsync(null);
                await Task.Delay(25, deadline.Token);
            }
            model.SelectedNode = null;
            await model.SelectBindingIssueCommand.ExecuteAsync(runtimeIssue);
            Assert.Equal(element.Node.Id, model.SelectedNode!.Node.Id);
            Assert.Contains(model.Bindings, property => property.Binding?.Status == "PathError");
            Assert.True(model.ShowSourceCommand.CanExecute(null), model.SourceDescription);
            await model.ShowSourceCommand.ExecuteAsync(null);
            Assert.StartsWith("Opened verified source location:", model.SourceDescription);
            var sourceEditor = shell.ActiveDocument!;
            Assert.Equal(Path.Combine(projectDirectory, "PrimaryWindow.xaml"), sourceEditor.State.Path, ignoreCase: true);
            var originalXaml = sourceEditor.State.Content;
            Assert.StartsWith("TextBlock", originalXaml[sourceEditor.State.CaretOffset..]);
            Assert.Contains("Name=\"BadText\"", originalXaml[sourceEditor.State.CaretOffset..].Split('>')[0]);
            sourceEditor.State.Content = originalXaml + "\n<!-- unsaved -->";
            sourceEditor.Navigate(0);
            await model.ShowSourceCommand.ExecuteAsync(null);
            Assert.Contains("open XAML buffer differs", model.SourceDescription);
            Assert.Equal(0, sourceEditor.State.CaretOffset);
            Assert.EndsWith("<!-- unsaved -->", sourceEditor.State.Content);
            sourceEditor.State.Content = originalXaml;
            var originalBytes = await File.ReadAllBytesAsync(sourceEditor.State.Path);
            try
            {
                await File.AppendAllTextAsync(sourceEditor.State.Path, "\n<!-- external change -->");
                await model.ShowSourceCommand.ExecuteAsync(null);
                Assert.Contains("file changed after", model.SourceDescription);
                Assert.Equal(0, sourceEditor.State.CaretOffset);
            }
            finally { await File.WriteAllBytesAsync(sourceEditor.State.Path, originalBytes); }
            await model.ShowSourceCommand.ExecuteAsync(null);
            Assert.StartsWith("Opened verified source location:", model.SourceDescription);
            await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-source.png"));
            model.SelectedTabIndex = 1;
            await Idle();
            var bindingPane = (FrameworkElement)manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "LiveInspection").Content;
            await model.ShowBindingIssueSourceCommand.ExecuteAsync(runtimeIssue);
            Assert.Equal("{Binding Misspelled}", sourceEditor.SelectedText);
            Assert.Contains("Opened verified", model.BindingSourceStatus);
            Assert.Equal(runtimeIssue.Id, model.SelectedBindingDeclaration?.BindingId);
            Assert.True(model.BindingExplanation.Available, model.BindingExplanation.Status);
            Assert.Equal("PathError", model.BindingExplanation.Observation?.Status);
            sourceEditor.RestoreSelection(0, 0, 0);
            var bindingProperty = model.Properties.Single(property => property.Name == "Text");
            model.SelectedProperty = bindingProperty;
            model.EditedValue = "Preserved binding navigation draft";
            var bindingSelector = Descendants<ComboBox>(bindingPane).Single(control =>
                System.Windows.Automation.AutomationProperties.GetName(control) == "Binding declaration");
            bindingSelector.SelectedItem = model.BindingDeclarations.Single(item => item.Property == "Text" && item.Declaration.ParentExpressionId is null);
            var showBinding = Descendants<Button>(bindingPane).Single(control =>
                System.Windows.Automation.AutomationProperties.GetName(control) == "Show binding XAML");
            Assert.Same(model.ShowBindingSourceCommand, showBinding.Command);
            Assert.True(showBinding.Command.CanExecute(showBinding.CommandParameter), model.BindingSourceStatus);
            await model.ShowBindingSourceCommand.ExecuteAsync(showBinding.CommandParameter);
            Assert.Equal("{Binding Misspelled}", sourceEditor.SelectedText);
            Assert.StartsWith("Opened verified binding declaration:", model.BindingSourceStatus);
            Assert.Same(bindingProperty, model.SelectedProperty);
            Assert.Equal("Preserved binding navigation draft", model.EditedValue);
            Assert.Equal(originalXaml, sourceEditor.State.Content);
            await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-binding-source.png"));
            shell.ToggleThemeCommand.Execute(null); await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-binding-source-light.png"));
            shell.ToggleThemeCommand.Execute(null);
            Assert.Contains(model.Bindings, property => property.Binding?.Details?.Evidence.Any(e => e.Kind == "MissingMember") == true);
            await VerifyBindingExplanationAsync(bindingPane, model.BindingExplanation);
            Assert.Contains(model.BindingExplanation.Evidence, evidence => evidence.Kind == "MissingMember");
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-binding-details.png"));
            model.SelectedTabIndex = 2;
            model.SelectedProperty = model.Properties.Single(property => property.Name == "Text");
            Assert.True(model.SelectedProperty.CanWriteSource, model.SelectedProperty.SourceUnavailableReason);
            model.EditedValue = "Live & reviewed";
            var originalRuntimeValue = model.SelectedProperty.Value;
            var cancelledSourceWrite = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(cancelledSourceWrite);
            Assert.Contains("binding", shell.PreviewWarnings, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Text=\"Live &amp; reviewed\"", Assert.Single(shell.PreviewChanges).After);
            Assert.Equal(originalXaml, sourceEditor.State.Content);
            Assert.Equal(originalRuntimeValue, model.SelectedProperty.Value);
            shell.CancelPreviewCommand.Execute(null);
            await cancelledSourceWrite;
            Assert.Equal(originalXaml, sourceEditor.State.Content);

            var staleSourceWrite = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(staleSourceWrite);
            sourceEditor.State.Content = originalXaml + "\n<!-- changed during review -->";
            shell.AcceptPreviewCommand.Execute(null);
            await staleSourceWrite;
            Assert.EndsWith("<!-- changed during review -->", sourceEditor.State.Content);
            sourceEditor.State.Content = originalXaml;

            var externalSourceWrite = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(externalSourceWrite);
            try
            {
                await File.AppendAllTextAsync(sourceEditor.State.Path, "\n<!-- saved externally during review -->");
                shell.AcceptPreviewCommand.Execute(null);
                await externalSourceWrite;
                Assert.Equal(originalXaml, sourceEditor.State.Content);
                Assert.Contains("file changed after", model.SourceEditStatus);
                Assert.EndsWith("<!-- saved externally during review -->", await File.ReadAllTextAsync(sourceEditor.State.Path));
            }
            finally { await File.WriteAllBytesAsync(sourceEditor.State.Path, originalBytes); }

            var sourceWrite = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(sourceWrite);
            await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-source-review.png"));
            shell.AcceptPreviewCommand.Execute(null);
            await sourceWrite;
            Assert.Contains("Text=\"Live &amp; reviewed\"", sourceEditor.State.Content);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourceEditor.State.Path));
            Assert.Contains("XAML updated", model.SourceEditStatus);
            Assert.Equal(originalRuntimeValue, model.SelectedProperty.Value);
            Assert.Equal("PathError", model.SelectedProperty.Binding!.Status);
            await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
            Assert.Equal(originalXaml, sourceEditor.State.Content);

            model.EditAsNull = true;
            var nullWrite = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(nullWrite);
            Assert.Contains("Text=\"{x:Null}\"", Assert.Single(shell.PreviewChanges).After);
            shell.AcceptPreviewCommand.Execute(null);
            await nullWrite;
            Assert.Contains("Text=\"{x:Null}\"", sourceEditor.State.Content);
            await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
            model.EditAsNull = false;
            var removeSource = model.RemovePropertyFromSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(removeSource);
            Assert.DoesNotContain("Name=\"BadText\" Text=", Assert.Single(shell.PreviewChanges).After);
            shell.AcceptPreviewCommand.Execute(null);
            await removeSource;
            Assert.DoesNotContain("Name=\"BadText\" Text=", sourceEditor.State.Content);
            await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
            Assert.Equal(originalXaml, sourceEditor.State.Content);

            // A style binding is overridden locally; its shared declaration stays intact.
            await SelectSourcePropertyAsync("StyleBindingText", "Text");
            model.EditedValue = "Local style override";
            var styleWrite = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(styleWrite);
            var styleAfter = Assert.Single(shell.PreviewChanges).After;
            Assert.Contains("Name=\"StyleBindingText\" Text=\"Local style override\"", styleAfter);
            Assert.Contains("<Setter Property=\"Text\" Value=\"{Binding MissingStyleValue}\" />", styleAfter);
            shell.AcceptPreviewCommand.Execute(null);
            await styleWrite;
            Assert.Equal(styleAfter, sourceEditor.State.Content);
            await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);

            await SelectSourcePropertyAsync("MultiBadText", "Text");
            model.EditedValue = "Replace composite binding";
            var compositeWrite = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(compositeWrite);
            var compositeAfter = Assert.Single(shell.PreviewChanges).After;
            Assert.DoesNotContain("<MultiBinding", compositeAfter);
            Assert.Contains("Replace composite binding", compositeAfter);
            shell.AcceptPreviewCommand.Execute(null);
            await compositeWrite;
            Assert.Equal(compositeAfter, sourceEditor.State.Content);
            await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);

            // Do not refresh the inspector: acceptance must independently detect
            // the target's binding changing while the review remains open.
            await SelectSourcePropertyAsync("ReplacementText", "Text");
            model.EditedValue = "Obsolete proposal";
            var changedRuntimeWrite = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            await WaitForSourceReviewAsync(changedRuntimeWrite);
            await SendFixtureCommandAsync("binding-replace");
            shell.AcceptPreviewCommand.Execute(null);
            await changedRuntimeWrite;
            Assert.Equal(originalXaml, sourceEditor.State.Content);
            Assert.DoesNotContain("XAML updated", model.SourceEditStatus);

            model.SelectedTabIndex = 0;
            await Idle();
            Assert.True(manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "LiveInspection").IsSelected);
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml.png"));
            shell.ToggleThemeCommand.Execute(null); await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-light.png"));
            shell.ToggleThemeCommand.Execute(null);
            Assert.True(model.TogglePickingCommand.CanExecute(null));
            await model.TogglePickingCommand.ExecuteAsync(null);
            Assert.True(model.IsPicking, model.PickStatus);
            await SendFixtureCommandAsync("input-main-down");
            await SendFixtureCommandAsync("input-main-up");
            while (model.SelectedNode?.Node.Name != "PrimaryPickButton")
            {
                await model.RefreshCommand.ExecuteAsync(null);
                await Task.Delay(25, deadline.Token);
            }
            Assert.False(model.IsPicking);
            model.SelectedTabIndex = 2;
            await Idle();
            var inspectionContent = (FrameworkElement)manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "LiveInspection").Content;
            var runtimeTree = Descendants<TreeView>(inspectionContent).Single();
            var selectedRow = Descendants<TreeViewItem>(runtimeTree).Single(item => ReferenceEquals(item.DataContext, model.SelectedNode));
            Assert.True(selectedRow.IsSelected);
            var rowTop = selectedRow.TransformToAncestor(runtimeTree).Transform(new Point(0, 0)).Y;
            Assert.True(rowTop >= 0 && rowTop + 20 <= runtimeTree.ActualHeight, $"Picked row must be visible: {rowTop} / {runtimeTree.ActualHeight}.");
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-picked.png"));
            Assert.True(model.HasLayout, model.LayoutStatus);
            Assert.Contains(model.Layout!.Facts, fact => fact.Name == "Render size");
            var runtimeLayoutTab = Descendants<TabItem>(inspectionContent).Single(tab =>
                System.Windows.Automation.AutomationProperties.GetName(tab) == "Element layout");
            runtimeLayoutTab.IsSelected = true;
            await Idle();
            var runtimeLayoutToggle = Descendants<CheckBox>(inspectionContent).Single(box =>
                System.Windows.Automation.AutomationProperties.GetName(box) == "Show layout overlay");
            Assert.True(runtimeLayoutToggle.IsEnabled);
            runtimeLayoutToggle.IsChecked = true;
            Assert.True(model.ShowLayoutOverlay);
            await model.RefreshCommand.ExecuteAsync(null);
            await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-layout.png"));
            shell.ToggleThemeCommand.Execute(null); await Idle();
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-layout-light.png"));
            shell.ToggleThemeCommand.Execute(null);
            runtimeLayoutToggle.IsChecked = false;
            await model.RefreshCommand.ExecuteAsync(null);
            model.SelectedTabIndex = 2;
            await SendFixtureCommandAsync("edit-create");
            await model.RefreshCommand.ExecuteAsync(null);
            model.SelectedNode = RuntimeNodes(model.Tree).Single(node => node.Node.Name == "EditTwoWay");
            while (!model.Properties.Any(property => property.Name == "Value")) await Task.Delay(25, deadline.Token);
            model.SelectedProperty = model.Properties.Single(property => property.Name == "Value");
            model.EditedValue = "Temporary from Live XAML";
            await Idle();
            var valueEditor = Descendants<TextBox>(inspectionContent).Single(text => System.Windows.Automation.AutomationProperties.GetName(text) == "Temporary property value");
            Assert.Equal(model.EditedValue, valueEditor.Text);
            Assert.True(model.ValidatePropertyCommand.CanExecute(null), model.PropertyEditHint);
            await model.ValidatePropertyCommand.ExecuteAsync(null);
            Assert.Contains("valid", model.EditStatus);
            await model.ApplyPropertyCommand.ExecuteAsync(null);
            Assert.True(model.SelectedProperty?.IsOverridden == true, model.EditStatus + " | " + model.Status + " | Selected: " + model.SelectedNode?.Node.Name);
            Assert.Equal("Temporary from Live XAML", model.SelectedProperty.EditableValue);
            await SendFixtureCommandAsync("edit-state");
            using (var editState = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "edit-state.json"))))
            {
                var probe = editState.RootElement.GetProperty("Probes").EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "EditTwoWay");
                Assert.Equal("Temporary from Live XAML", probe.GetProperty("Value").GetString());
                Assert.Equal("EditTwoWay original", probe.GetProperty("SourceValue").GetString());
                Assert.Equal(0, probe.GetProperty("SourceWrites").GetInt32());
            }
            await Idle();
            var liveProperties = Descendants<DataGrid>(inspectionContent).Single();
            Assert.True(liveProperties.ActualHeight >= 100, $"Live property table must remain usable: {liveProperties.RenderSize}.");
            Assert.Same(model.SelectedProperty, liveProperties.SelectedItem);
            Assert.NotNull(liveProperties.ItemContainerGenerator.ContainerFromItem(model.SelectedProperty));
            Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-editing.png"));
            await model.ResetPropertyCommand.ExecuteAsync(null);
            Assert.False(model.SelectedProperty!.IsOverridden, model.EditStatus);
            Assert.Equal("EditTwoWay original", model.SelectedProperty.EditableValue);
            await SendFixtureCommandAsync("edit-model-update");
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("edit-model-update: EditTwoWay", model.SelectedProperty!.EditableValue);
            await model.DisconnectCommand.ExecuteAsync(null);
            Assert.False(model.IsConnected);
            Assert.False(process.HasExited);

            async Task SendFixtureCommandAsync(string action)
            {
                var id = Guid.NewGuid().ToString("N");
                var temporary = Path.Combine(folder, id + ".tmp");
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { Id = id, Action = action }));
                File.Move(temporary, Path.Combine(folder, "command.json"), overwrite: true);
                while (!File.Exists(Path.Combine(folder, id + ".ack"))) await Task.Delay(25, deadline.Token);
            }

            async Task WaitForSourceReviewAsync(Task operation)
            {
                using var reviewDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!shell.IsPreviewOpen && !operation.IsCompleted) await Task.Delay(20, reviewDeadline.Token);
                Assert.True(shell.IsPreviewOpen, model.SourceEditStatus + " | " + model.SourcePropertyEditHint);
            }

            async Task SelectSourcePropertyAsync(string nodeName, string propertyName)
            {
                model.SelectedNode = RuntimeNodes(model.Tree).Single(node => node.Node.Name == nodeName);
                using var selectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!model.Properties.Any(property => property.Name == propertyName)) await Task.Delay(20, selectionDeadline.Token);
                model.SelectedProperty = model.Properties.Single(property => property.Name == propertyName);
                Assert.True(model.SelectedProperty.CanWriteSource, model.SelectedProperty.SourceUnavailableReason);
            }
        }
        finally
        {
            if (process is null || !process.HasExited)
            {
                var command = Path.Combine(folder, "ui-shutdown.tmp");
                await File.WriteAllTextAsync(command, JsonSerializer.Serialize(new { Id = "ui-shutdown", Action = "shutdown" }));
                File.Move(command, Path.Combine(folder, "command.json"), overwrite: true);
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (process != null)
                {
                    try { await process.WaitForExitAsync(shutdown.Token); }
                    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                }
            }
            process?.Dispose();
        }
    }

    private static IEnumerable<RuntimeNodeViewModel> RuntimeNodes(IEnumerable<RuntimeNodeViewModel> roots)
    {
        foreach (var node in roots) { yield return node; foreach (var child in RuntimeNodes(node.Children)) yield return child; }
    }

    private static async Task VerifyRuntimeAppearanceAsync(string root, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        var model = shell.LiveInspection;
        var pane = (FrameworkElement)manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "LiveInspection").Content;
        model.SelectedNode = RuntimeNodes(model.Tree).Single(node => node.Node.Name == "AppearanceStyle");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!model.Properties.Any(property => property.Name == "Foreground")) await Task.Delay(20, deadline.Token);
        model.SelectedProperty = model.Properties.Single(property => property.Name == "Foreground");
        model.EditedValue = "Unapplied appearance draft";
        Descendants<TabItem>(pane).Single(tab => System.Windows.Automation.AutomationProperties.GetName(tab) == "Property appearance").IsSelected = true;
        await Idle();
        var refresh = Descendants<Button>(pane).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Refresh appearance");
        await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(refresh.Command).ExecuteAsync(null);
        Assert.True(model.AppearanceDetails.Available, model.AppearanceDetails.Status);
        Assert.Contains(model.AppearanceDetails.Facts, fact => fact.Name == "Base value source" && fact.Value == "Style");
        Assert.Contains(model.AppearanceDetails.Declarations, declaration => declaration.Kind == "BasedOn");
        Assert.Contains(model.AppearanceDetails.ResourceScopes.SelectMany(scope => scope.Keys), key => key.Contains("UnusedDeferredValue", StringComparison.Ordinal));
        Assert.Equal("Unapplied appearance draft", model.EditedValue);
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-appearance.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/live-xaml-appearance-light.png"));
        shell.ToggleThemeCommand.Execute(null);
        model.SelectedTabIndex = 0;
        model.SelectedNode = null;
        Assert.Null(model.AppearanceDetails.Snapshot);
    }

    private static async Task VerifyPreviewAppearanceAsync(string root, string data, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        string path = Path.Combine(data, "Appearance.xaml");
        const string source = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="#F0F3F9">
              <Grid.Resources>
                <SolidColorBrush x:Key="Accent" Color="#345CC4"/>
                <Style x:Key="BaseText" TargetType="TextBlock"><Setter Property="FontSize" Value="28"/><Setter Property="Foreground" Value="Navy"/></Style>
                <Style x:Key="Heading" TargetType="TextBlock" BasedOn="{StaticResource BaseText}">
                  <Setter Property="Foreground" Value="{StaticResource Accent}"/>
                  <Style.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="Gray"/></Trigger></Style.Triggers>
                </Style>
              </Grid.Resources>
              <Border Margin="24" Padding="24" Background="White" CornerRadius="12">
                <StackPanel>
                  <TextBlock x:Name="StyledHeading" Text="Understand every property" Style="{StaticResource Heading}"/>
                  <TextBlock x:Name="DynamicText" Text="A live resource reference" Foreground="{DynamicResource Accent}" Margin="0,12,0,0"/>
                  <TextBlock x:Name="StaticText" Text="A captured resource resolution" Foreground="{StaticResource Accent}" Margin="0,12,0,0"/>
                </StackPanel>
              </Border>
            </Grid>
            """;
        await File.WriteAllTextAsync(path, source);
        await shell.OpenDocumentAsync(path);
        var editor = shell.ActiveDocument!;
        var model = shell.Designer;
        model.Mode = PreviewMode.Source; model.AutoRefresh = false;
        await shell.OpenDesignerCommand.ExecuteAsync(null);
        Assert.True(model.IsCurrent, model.Status);
        var pane = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "Designer");
        var paneView = (FrameworkElement)pane.Content;
        await Select("StyledHeading");
        model.EditedValue = "Unapplied appearance draft";
        var appearanceTab = Descendants<TabItem>(paneView).Single(tab => System.Windows.Automation.AutomationProperties.GetName(tab) == "Property appearance");
        appearanceTab.IsSelected = true;
        await Idle();
        var tabStrip = Descendants<ScrollViewer>(paneView).Single(scroll => System.Windows.Automation.AutomationProperties.GetName(scroll) == "Preview inspector tabs");
        var tabViewport = Descendants<ScrollContentPresenter>(tabStrip).First();
        var tabBounds = appearanceTab.TransformToAncestor(tabViewport).TransformBounds(new Rect(appearanceTab.RenderSize));
        Assert.True(tabBounds.Left >= -1 && tabBounds.Right <= tabViewport.ActualWidth + 1,
            $"Selected Appearance tab must be fully visible: {tabBounds} in {tabViewport.ActualWidth}px viewport.");
        var selector = Descendants<ComboBox>(paneView).Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == "Appearance property");
        Assert.Same(model.SelectedProperty, selector.SelectedItem);
        var refresh = Descendants<Button>(paneView).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Refresh appearance");
        await Refresh();
        Assert.Contains(model.AppearanceDetails.Facts, fact => fact.Name == "Base value source" && fact.Value == "Style");
        Assert.Contains(model.AppearanceDetails.Declarations, declaration => declaration.Kind == "BasedOn");
        Assert.Contains(model.AppearanceDetails.Declarations, declaration => declaration.Kind == "Trigger");
        Assert.Contains(model.AppearanceDetails.ResourceScopes.SelectMany(scope => scope.Keys), key => key.Contains("Accent", StringComparison.Ordinal));
        Assert.Equal("Unapplied appearance draft", model.EditedValue);
        // Obsolete read replies must not terminate the real preview process.
        var inFlight = model.RefreshAppearanceCommand.ExecuteAsync(null);
        selector.SelectedItem = model.Properties.Single(property => property.Name == "FontSize");
        selector.SelectedItem = model.Properties.Single(property => property.Name == "Foreground");
        await inFlight;
        await Refresh();
        Assert.True(model.IsCurrent, model.Status);
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-appearance.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-appearance-light.png"));
        shell.ToggleThemeCommand.Execute(null);
        await Select("DynamicText"); await Refresh();
        Assert.Contains(model.AppearanceDetails.Facts, fact => fact.Name == "Local DynamicResource key" && fact.Value.Contains("Accent", StringComparison.Ordinal));
        await Select("StaticText"); await Refresh();
        Assert.Contains(model.AppearanceDetails.ResourceEvents, item => item.Key.Contains("Accent", StringComparison.Ordinal));
        var appearancePanel = Descendants<WpfStudio.App.Features.Appearance.AppearancePanel>(paneView).Single();
        foreach (var expander in Descendants<Expander>(appearancePanel))
            if (expander.Header is TextBlock header && (header.Text.StartsWith("Available resource scopes", StringComparison.Ordinal) ||
                header.Text.StartsWith("Historical resource resolutions", StringComparison.Ordinal))) expander.IsExpanded = true;
        await Idle();
        Descendants<ScrollViewer>(appearancePanel).First().ScrollToBottom();
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-appearance-resources.png"));
        Assert.Equal(source, editor.State.Content);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
        editor.State.Content = source + "\n<!-- invalidate observation -->";
        Assert.Null(model.AppearanceDetails.Snapshot);
        Assert.False(model.RefreshAppearanceCommand.CanExecute(null));
        editor.State.Content = source;
        Descendants<TabItem>(paneView).Single(tab => Equals(tab.Header, "Properties")).IsSelected = true;
        Assert.True(await shell.CloseDocumentAsync(editor)); pane.Hide();

        async Task Select(string name)
        {
            static IEnumerable<WpfStudio.App.Features.Designer.DesignerNode> Flatten(IEnumerable<WpfStudio.App.Features.Designer.DesignerNode> nodes) =>
                nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
            var node = Flatten(model.Tree).Single(item => item.Node.Name == name);
            var bounds = node.Node.Bounds!;
            await model.PickAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            model.SelectedProperty = model.Properties.Single(property => property.Name == "Foreground");
        }
        async Task Refresh()
        {
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(refresh.Command).ExecuteAsync(null);
            Assert.True(model.AppearanceDetails.Available, model.AppearanceDetails.Status);
        }
    }

    private static async Task VerifyPreviewScenariosAsync(string root, string data, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        string directory = Path.Combine(data, "preview-scenarios");
        string outputDirectory = Path.Combine(directory, "bin");
        Directory.CreateDirectory(outputDirectory);
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "CompiledFixture");
        foreach (string file in Directory.EnumerateFiles(fixtureDirectory, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(outputDirectory, Path.GetRelativePath(fixtureDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        string path = Path.Combine(directory, "Orders.xaml");
        const string source = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                  xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                  mc:Ignorable="d" Background="#F0F3F9">
              <Border Margin="24" Padding="24" Background="White" CornerRadius="12">
                <StackPanel>
                  <TextBlock Text="ORDERS" FontSize="12" Foreground="#667085" Margin="0,0,0,12"/>
                  <TextBlock x:Name="ScenarioMessage" Text="{Binding Title}" d:Text="Design-time order title"
                             FontSize="28" FontWeight="SemiBold" Foreground="#20243A"/>
                  <TextBlock Text="Switch the preview scenario to exercise each UI state." Margin="0,12,0,0" Foreground="#667085"/>
                </StackPanel>
              </Border>
            </Grid>
            """;
        await File.WriteAllTextAsync(path, source);
        PreviewFactory Factory(string method) => new("WpfStudio.PreviewFixture.ScenarioFactories", method);
        var declarations = new List<PreviewScenario>
        {
            new("Loading", DataContextFactory: Factory("Loading")),
            new("Empty", DataContextFactory: Factory("Empty")),
            new("Populated", DataContextFactory: Factory("Populated")),
            new("Error", DataContextFactory: Factory("Error")),
            new("Injected view", Factory("CreateView"), Factory("Populated"))
        };
        await File.WriteAllTextAsync(Path.Combine(directory, PreviewScenarioCatalog.FileName), JsonSerializer.Serialize(new
        {
            version = 1,
            views = new[] { new { path = "Orders.xaml", scenarios = declarations } }
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        }));
        await shell.OpenDocumentAsync(path);
        var editor = shell.ActiveDocument!;
        var model = shell.Designer;
        model.Mode = PreviewMode.Source; model.AutoRefresh = false; model.UseDesignTimeValues = true;
        model.ApplicationResourcePath = "App.xaml";
        await shell.OpenDesignerCommand.ExecuteAsync(null);
        await model.OpenAsync(editor.State, Path.Combine(outputDirectory, "WpfStudio.PreviewFixture.dll"), directory, "WpfStudio.PreviewFixture");
        await Idle();
        Assert.True(model.IsCurrent, model.Status + " | " + string.Join(" | ", model.Diagnostics.Select(issue => issue.Message)));
        var pane = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "Designer");
        var paneView = (FrameworkElement)pane.Content;
        var selector = Descendants<ComboBox>(paneView).Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == "Preview scenario");
        var designValues = Descendants<CheckBox>(paneView).Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == "Use design-time values");
        var refresh = Descendants<Button>(paneView).Single(item => ReferenceEquals(item.Command, model.RefreshCommand));
        Assert.Equal(6, selector.Items.Count);
        await InspectMessage("Design-time order title");
        Assert.Contains("design baseline", model.SelectedProperty!.ValueSource);
        Assert.False(model.WritePropertyToSourceCommand.CanExecute(null));
        await Idle(); AssertInspectorRoom();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-design-time-values.png"));
        designValues.IsChecked = false;
        Assert.False(model.IsCurrent);
        var timings = new Dictionary<string, long>();
        foreach (var state in new[] { ("Loading", "Loading orders"), ("Empty", "No orders"), ("Populated", "3 orders"), ("Error", "Unable to load orders") })
        {
            selector.SelectedItem = model.Scenarios.Single(item => item.Name == state.Item1);
            Assert.False(model.IsCurrent);
            var timer = Stopwatch.StartNew();
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(refresh.Command).ExecuteAsync(null);
            timings[state.Item1] = timer.ElapsedMilliseconds;
            Assert.True(model.IsCurrent, model.Status + " | " + string.Join(" | ", model.Diagnostics.Select(issue => issue.Message)));
            Assert.Contains(state.Item1, model.ScenarioDescription);
            await InspectMessage(state.Item2);
            Assert.Equal(source, editor.State.Content);
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        await Idle(); AssertInspectorRoom();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-scenarios.png"));
        var updateSnapshot = Descendants<Button>(paneView).Single(item =>
            System.Windows.Automation.AutomationProperties.GetName(item) == "Update preview snapshot");
        Assert.Same(model.UpdateSnapshotCommand, updateSnapshot.Command);
        string selectedNodeId = model.SelectedNode!.Node.Id;
        model.EditedValue = "Runtime snapshot observation";
        await model.ApplyPropertyCommand.ExecuteAsync(null);
        Assert.Equal("Runtime snapshot observation", model.SelectedProperty!.Value);
        model.EditedValue = "Draft retained across observation";
        await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(updateSnapshot.Command).ExecuteAsync(null);
        Assert.True(model.IsCurrent, model.Status);
        Assert.Equal(selectedNodeId, model.SelectedNode!.Node.Id);
        Assert.Equal("Runtime snapshot observation", model.SelectedProperty!.Value);
        Assert.True(model.SelectedProperty.IsOverridden);
        Assert.Equal("Draft retained across observation", model.EditedValue);
        Assert.Equal(source, editor.State.Content);
        await Idle(); AssertInspectorRoom();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-snapshot.png"));
        await model.ResetPropertyCommand.ExecuteAsync(null);
        Assert.Equal("Unable to load orders", model.SelectedProperty!.Value);
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-scenarios-light.png"));
        shell.ToggleThemeCommand.Execute(null);
        selector.SelectedItem = model.Scenarios.Single(item => item.Name == "Injected view");
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.False(model.IsCurrent);
        Assert.Contains("Compiled", model.Status);
        model.Mode = PreviewMode.Compiled;
        model.ViewTypeName = "WpfStudio.PreviewFixture.DependencyView";
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.IsCurrent, model.Status + " | " + string.Join(" | ", model.Diagnostics.Select(issue => issue.Message)));
        await InspectMessage("3 orders");
        Assert.Contains("Injected view", model.ScenarioDescription);
        Assert.Null(model.SelectedNode!.Node.Source);
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-factory.png"));
        Directory.CreateDirectory(Path.Combine(root, "artifacts/performance"));
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/performance/xaml-preview-scenarios.json"), JsonSerializer.Serialize(new
        {
            Scope = "Loaded-window workstation sample: switch four source data scenarios, each in a fresh preview process; project/output caches may be warm.",
            ScenarioRenderMs = timings,
            PreviewHostOverride = Environment.GetEnvironmentVariable("WPFSTUDIO_PREVIEW_HOST_UNDER_TEST"),
            CapturedUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(await shell.CloseDocumentAsync(editor));
        pane.Hide();

        async Task InspectMessage(string expected)
        {
            static IEnumerable<WpfStudio.App.Features.Designer.DesignerNode> Flatten(IEnumerable<WpfStudio.App.Features.Designer.DesignerNode> nodes) =>
                nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
            var message = Flatten(model.Tree).Single(node => node.Node.Name == "ScenarioMessage");
            var bounds = message.Node.Bounds!;
            await model.PickAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            model.SelectedProperty = model.Properties.Single(property => property.Name == "Text");
            Assert.Equal(expected, model.SelectedProperty.Value);
        }

        void AssertInspectorRoom()
        {
            var settings = Descendants<Expander>(paneView).Single(item => item.Name == "PreviewSettings");
            Assert.False(settings.IsExpanded);
            var header = Descendants<StackPanel>(paneView).Single(item => item.Name == "DesignerHeader");
            Assert.True(header.ActualHeight <= 120, $"The collapsed preview header uses {header.ActualHeight:N0}px.");
            var properties = Descendants<DataGrid>(paneView).Single(grid => ReferenceEquals(grid.ItemsSource, model.Properties));
            Assert.True(properties.ActualHeight >= 100,
                $"Preview settings leave only {properties.ActualHeight:N0}px for property rows in a {paneView.ActualHeight:N0}px designer pane.");
            Assert.Contains(Descendants<DataGridRow>(properties), row => row.IsVisible && row.ActualHeight > 0);
        }
    }

    private static async Task VerifyDesignerAsync(string root, string data, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        var path = Path.Combine(data, "DesignerFixture.xaml");
        const string source = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="Root" Background="#F0F3F9">
              <Border Margin="32" Padding="28" Background="White" CornerRadius="12">
                <StackPanel>
                  <TextBlock x:Name="Greeting" Text="Live XAML preview" FontSize="28" FontWeight="SemiBold" Foreground="#20243A" />
                  <TextBlock Text="Select an element to inspect its values and bindings." Margin="0,12,0,20" Foreground="#667085" />
                  <Button Content="Preview button" Width="160" Height="36" HorizontalAlignment="Left" />
                  <Border Width="320" Height="76" HorizontalAlignment="Left" ClipToBounds="True" Margin="0,18,0,8" Background="#E5EAF3">
                    <Border x:Name="LayoutTarget" Width="380" Height="48" Margin="12,8,10,6" HorizontalAlignment="Left" VerticalAlignment="Top" Background="#CBD9F7">
                      <TextBlock Text="A fixed-width element clipped by its parent" Margin="12" />
                    </Border>
                  </Border>
                  <TextBlock x:Name="BadBinding" Text="{Binding MisspelledProperty, ElementName=Root}" />
                </StackPanel>
              </Border>
            </Grid>
            """;
        await File.WriteAllTextAsync(path, source);
        await shell.OpenDocumentAsync(path);
        var document = shell.ActiveDocument!;
        await shell.OpenDesignerCommand.ExecuteAsync(null); await Idle();
        var model = shell.Designer;
        Assert.True(model.IsCurrent, model.Status + "\n" + string.Join("\n", model.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(model.Image!);
        Assert.Contains(model.Diagnostics, d => d.Message.Contains("MisspelledProperty"));
        var pane = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "Designer");
        Assert.True(pane.IsSelected);
        var outputPane = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "Output");
        Assert.Same(outputPane.Parent, pane.Parent); // Keep enough room for source above the designer.
        var paneView = (FrameworkElement)pane.Content;
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-designer-layout.png"));
        Assert.True(paneView.ActualHeight >= 300, $"Designer has {paneView.ActualWidth} × {paneView.ActualHeight} available; parent {pane.Parent.GetType().Name}.");
        static IEnumerable<WpfStudio.App.Features.Designer.DesignerNode> Flatten(IEnumerable<WpfStudio.App.Features.Designer.DesignerNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
        var greeting = Flatten(model.Tree).Single(n => n.Node.Name == "Greeting");
        var badBinding = Flatten(model.Tree).Single(n => n.Node.Name == "BadBinding");
        var diagnostic = Assert.Single(model.Diagnostics, item => item.NodeId == badBinding.Node.Id && item.Property == "Text" && item.BindingId is not null);
        Assert.True(model.ShowBindingDiagnosticCommand.CanExecute(diagnostic));
        await model.ShowBindingDiagnosticCommand.ExecuteAsync(diagnostic);
        Assert.Equal(badBinding.Node.Id, model.SelectedNode?.Node.Id);
        Assert.Equal(diagnostic.BindingId, model.SelectedBindingDeclaration?.BindingId);
        Assert.True(model.BindingExplanation.Available, model.BindingExplanation.Status);
        Assert.Equal("PathError", model.BindingExplanation.Observation?.Status);
        Descendants<TabItem>(paneView).Single(tab => Equals(tab.Header, "Bindings")).IsSelected = true;
        model.SelectedProperty = model.Properties.Single(property => property.Name == "Text");
        var selectedBindingProperty = model.SelectedProperty;
        model.EditedValue = "Preserved preview navigation draft";
        await Idle();
        var bindingSelector = Descendants<ComboBox>(paneView).Single(control =>
            System.Windows.Automation.AutomationProperties.GetName(control) == "Binding declaration");
        bindingSelector.SelectedItem = model.BindingDeclarations.Single(item => item.Property == "Text" && item.Declaration.ParentExpressionId is null);
        var showBinding = Descendants<Button>(paneView).Single(control =>
            System.Windows.Automation.AutomationProperties.GetName(control) == "Show binding XAML");
        Assert.Same(model.ShowBindingSourceCommand, showBinding.Command);
        Assert.True(showBinding.Command.CanExecute(showBinding.CommandParameter), model.BindingSourceStatus);
        await model.ShowBindingSourceCommand.ExecuteAsync(showBinding.CommandParameter);
        Assert.Equal("{Binding MisspelledProperty, ElementName=Root}", document.SelectedText);
        Assert.Same(selectedBindingProperty, model.SelectedProperty);
        Assert.Equal("Preserved preview navigation draft", model.EditedValue);
        Assert.Equal(source, document.State.Content);
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-binding-source.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-binding-source-light.png"));
        shell.ToggleThemeCommand.Execute(null);
        await VerifyBindingExplanationAsync(paneView, model.BindingExplanation);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-preview-binding-details.png"));
        model.SelectedNode = Flatten(model.Tree).Single(n => n.Node.Name == "LayoutTarget");
        using (var layoutDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (!model.HasLayout) await Task.Delay(25, layoutDeadline.Token);
        Assert.Contains(model.Layout!.Facts, fact => fact.Name == "Render size");
        Assert.Contains(model.Layout.Overlays, overlay => overlay.Kind == "clip-bounds");
        var designerLayoutTab = Descendants<TabItem>(paneView).Single(tab =>
            System.Windows.Automation.AutomationProperties.GetName(tab) == "Element layout");
        designerLayoutTab.IsSelected = true;
        await Idle();
        var designerLayoutToggle = Descendants<CheckBox>(paneView).Single(box =>
            System.Windows.Automation.AutomationProperties.GetName(box) == "Show layout overlay");
        designerLayoutToggle.IsChecked = false;
        await Idle();
        var previewSurface = Descendants<WpfStudio.App.Features.Designer.PreviewSurface>(paneView).Single();
        var plainPreview = RenderPixels(previewSurface);
        designerLayoutToggle.IsChecked = true;
        Assert.True(model.ShowLayoutOverlay);
        await Idle();
        Assert.False(plainPreview.SequenceEqual(RenderPixels(previewSurface)), "Layout overlay should visibly change the rendered preview surface.");
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-designer-layout-details.png"));
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-designer-layout-details-light.png"));
        shell.ToggleThemeCommand.Execute(null);
        designerLayoutToggle.IsChecked = false;
        Descendants<TabItem>(paneView).Single(tab => Equals(tab.Header, "Properties")).IsSelected = true;
        var bounds = greeting.Node.Bounds!;
        await model.PickAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); await Idle();
        Assert.Equal("Greeting", model.SelectedNode!.Node.Name);
        Assert.True(model.SelectedNode.IsSelected);
        model.SelectedProperty = model.Properties.Single(p => p.Name == "Text");
        Assert.Equal("Local", model.SelectedProperty.ValueSource);
        model.EditedValue = "Edited in preview";
        await model.ApplyPropertyCommand.ExecuteAsync(null); await Idle();
        Assert.Equal(source, document.State.Content);
        Assert.Equal("Edited in preview", model.SelectedProperty!.Value);
        var propertiesGrid = Descendants<DataGrid>((FrameworkElement)pane.Content).Single();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-designer.png"));
        Assert.Same(model.SelectedProperty, propertiesGrid.SelectedItem);
        Assert.True(propertiesGrid.ItemContainerGenerator.ContainerFromItem(model.SelectedProperty) != null,
            $"Selected property {model.SelectedProperty.Name}, index {propertiesGrid.SelectedIndex}, follow {Interaction.GetFollowSelection(propertiesGrid)}, loaded {propertiesGrid.IsLoaded}, size {propertiesGrid.RenderSize}; scroll: " +
            string.Join("; ", Descendants<ScrollViewer>(propertiesGrid).Select(scroll => $"{scroll.VerticalOffset}/{scroll.ExtentHeight}, viewport {scroll.ViewportHeight}")));
        await model.ResetPropertyCommand.ExecuteAsync(null); await Idle();
        Assert.Equal("Live XAML preview", model.SelectedProperty!.Value);
        shell.ToggleThemeCommand.Execute(null); await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-designer-light.png"));
        shell.ToggleThemeCommand.Execute(null);
        model.AutoRefresh = false;
        model.EditedValue = "Written & saved in source buffer";
        Assert.True(model.WritePropertyToSourceCommand.CanExecute(null));
        var diffOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler diffChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(shell.IsPreviewOpen) && shell.IsPreviewOpen) diffOpened.TrySetResult();
        };
        shell.PropertyChanged += diffChanged;
        Task writeSource;
        try
        {
            writeSource = model.WritePropertyToSourceCommand.ExecuteAsync(null);
            var finished = await Task.WhenAny(diffOpened.Task, writeSource).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(shell.IsPreviewOpen, model.Status);
        }
        finally { shell.PropertyChanged -= diffChanged; }
        Assert.Equal(source, document.State.Content);
        Assert.Contains("Written &amp; saved", Assert.Single(shell.PreviewChanges).After);
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-designer-source-diff.png"));
        shell.AcceptPreviewCommand.Execute(null);
        await writeSource;
        Assert.Contains("Text=\"Written &amp; saved in source buffer\"", document.State.Content);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
        await model.RefreshCommand.ExecuteAsync(null);
        greeting = Flatten(model.Tree).Single(n => n.Node.Name == "Greeting");
        bounds = greeting.Node.Bounds!;
        await model.PickAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        Assert.Contains(model.Properties, p => p.Name == "Text" && p.Value == "Written & saved in source buffer");
        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(source, document.State.Content);

        var lastFrame = model.DisplayImage;
        Assert.NotNull(lastFrame);
        document.State.Content = "<Grid";
        Assert.False(model.IsCurrent);
        Assert.Null(model.Image);
        // The canvas keeps showing the last successful render while the source is broken.
        Assert.Same(lastFrame, model.DisplayImage);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.False(model.IsCurrent);
        Assert.NotEmpty(model.Diagnostics);
        Assert.True(model.HasRenderError, model.Status);
        Assert.Same(lastFrame, model.DisplayImage);
        await Idle();
        var errorBanner = Descendants<Border>(paneView).Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == "Preview render error");
        Assert.True(errorBanner.IsVisible);
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-designer-render-error.png"));
        document.State.Content = source;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.IsCurrent, model.Status);
        Assert.False(model.HasRenderError);
        // The element picked before the edits is selected again in the new render.
        Assert.Equal("Greeting", model.SelectedNode?.Node.Name);

        model.AssemblyPath = Path.Combine(AppContext.BaseDirectory, "CompiledFixture", "WpfStudio.PreviewFixture.dll");
        model.ProjectDirectory = AppContext.BaseDirectory;
        model.Mode = PreviewMode.Compiled;
        model.ViewTypeName = "WpfStudio.PreviewFixture.FixtureView";
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.IsCurrent, model.Status + "\n" + string.Join("\n", model.Diagnostics.Select(d => d.Message)));
        Assert.Contains("SHA-256", model.BuildDescription);
        Assert.All(Flatten(model.Tree), node => Assert.Null(node.Node.Source));
        var compiledMessage = Flatten(model.Tree).Single(node => node.Node.Name == "Message");
        bounds = compiledMessage.Node.Bounds!;
        await model.PickAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        model.SelectedProperty = model.Properties.Single(property => property.Name == "Text");
        Assert.Equal("Constructor in design mode / Loaded handler", model.SelectedProperty.Value);
        Assert.False(model.WritePropertyToSourceCommand.CanExecute(null));
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-designer-compiled.png"));
        Assert.True(await shell.CloseDocumentAsync(document));
        Assert.False(model.IsCurrent);
        pane.Hide();
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

    private static async Task VerifyBindingExplanationAsync(FrameworkElement pane,
        WpfStudio.App.Features.BindingDiagnostics.BindingExplanationViewModel explanation)
    {
        await Idle();
        var panel = Assert.Single(Descendants<WpfStudio.App.Features.BindingDiagnostics.BindingExplanationPanel>(pane));
        Assert.Same(explanation, panel.DataContext);
        Assert.True(explanation.Available, explanation.Status);
        Assert.Equal("PathError", explanation.Observation?.Status);
        var path = explanation.Observation?.Details?.PathState;
        Assert.NotNull(path);
        Assert.True(path.Available, path.UnavailableReason);
        Assert.Equal(0, path.FirstUnresolvedLevel);
        Assert.NotEmpty(explanation.Segments);
        var pathText = Assert.Single(Descendants<TextBlock>(panel), text =>
            System.Windows.Automation.AutomationProperties.GetName(text) == "Binding path state");
        Assert.Equal(explanation.PathStatus, pathText.Text);
        Assert.True(pathText.IsVisible);
        var expanders = Descendants<Expander>(panel).ToArray();
        Assert.Contains(expanders, expander => Equals(expander.Header, "Cached path steps"));
        Assert.Contains(expanders, expander => Equals(expander.Header, "Current binding details"));
        foreach (var expander in expanders) expander.IsExpanded = true;
        await Idle();
        var scroll = Assert.Single(Descendants<ScrollViewer>(panel), view =>
            System.Windows.Automation.AutomationProperties.GetName(view) == "Binding explanation details");
        Assert.True(scroll.ActualHeight > 50, $"Binding details viewport height: {scroll.ActualHeight}");
        scroll.ScrollToBottom();
        await Idle();
        Assert.True(scroll.VerticalOffset > 0, "Expanded binding details must remain reachable by scrolling.");
        scroll.ScrollToTop();
        await Idle();
        // Keep the cached-step evidence in view in the feature-tour capture.
        scroll.ScrollToVerticalOffset(pathText.TranslatePoint(new Point(), scroll).Y - 4);
        await Idle();
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
    private static byte[] RenderPixels(FrameworkElement content)
    {
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
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
        public Queue<string?> Prompts { get; } = new();
        public List<string> Errors { get; } = [];
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(SaveDecision.Cancel);
        public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult(Prompts.TryDequeue(out var value) ? value : null);
        public Task ShowErrorAsync(string title, string message) { Errors.Add(title + ": " + message); return Task.CompletedTask; }
    }
}
