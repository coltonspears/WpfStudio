using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using WpfStudio.App;
using WpfStudio.App.Features.Profiling;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    private static async Task VerifyMemoryProfilerAsync(string root, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        shell.ShowToolCommand.Execute("Profiler"); await Idle();
        var tab = manager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "Profiler");
        var pane = Assert.IsType<MemoryProfilerPane>(tab.Content);
        Assert.Same(shell.MemoryProfiler, pane.DataContext); Assert.Equal("Profiler", shell.ActiveWorkbench);
        // Exercise the shorter space left by docked tools even in the focused test branch.
        if (Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_MEMORY_ONLY") == "1") { pane.Height = 460; await Idle(); }
        Assert.True(pane.ActualHeight > 300);
        Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-empty.png"));
        var configuration = typeof(ShellSmokeTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var fixturePath = Path.Combine(root, "tests", "WpfStudio.Profiling.Tests", "Fixtures", "RetentionFixture", "bin", configuration, "net10.0", "WpfStudio.RetentionFixture.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(fixturePath);
        using var fixture = Process.Start(start)!;
        try
        {
            Assert.Equal("READY", await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            var model = shell.MemoryProfiler;
            await model.RefreshProcessesCommand.ExecuteAsync(null);
            model.SelectedProcess = model.Processes.Single(p => p.Id == fixture.Id);
            await model.CaptureCommand.ExecuteAsync(null);
            Assert.True(model.HasCapture, model.Status); Assert.True(model.Summary!.IsComplete, model.Status);
            var pageType = model.Types.Single(t => t.Name == "WpfStudio.RetentionFixture.RetainedPageModel");
            model.SelectedType = pageType;
            using var selectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (model.Details?.Object.Type != pageType.Name || model.IsInspecting) await Task.Delay(20, selectionDeadline.Token);
            Assert.NotEmpty(model.RootPaths); Assert.Contains(model.Details.Fields, f => f.Name == "CustomerId");
            await Idle();
            var graph = Descendants<MemoryGraphSurface>(pane).Single();
            Assert.True(graph.ActualWidth > 150 && graph.ActualHeight > 120, $"Inline graph is {graph.ActualWidth} × {graph.ActualHeight}");
            model.ToggleGraphFocusCommand.Execute(null); await Idle();
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-map.png"));
            Assert.True(graph.ActualWidth > 650 && graph.ActualHeight > 160, $"Focused graph is {graph.ActualWidth} × {graph.ActualHeight}");
            model.ToggleGraphFocusCommand.Execute(null); await Idle();
            Assert.True(MemoryGraphSurface.FitGraphCommand.CanExecute(null, graph));
            MemoryGraphSurface.FitGraphCommand.Execute(null, graph); await Idle();
            var initialZoom = graph.Zoom;
            MemoryGraphSurface.ZoomInCommand.Execute(null, graph); Assert.True(graph.Zoom > initialZoom);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-roots.png"));
            model.InspectorTab = 1; await Idle();
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-fields.png"));
            var fields = Descendants<System.Windows.Controls.DataGrid>(pane).Single(g => System.Windows.Automation.AutomationProperties.GetName(g) == "Object fields");
            Assert.True(fields.ActualHeight > 60, $"Object fields grid height is {fields.ActualHeight}");
            model.SelectedReference = model.Details.Incoming.First(r => !r.IsRoot && r.Owner.Contains("[]"));
            await model.EstimateReferenceCommand.ExecuteAsync(null); await Idle();
            Assert.True(model.ReleaseEstimate!.SelectedObjectRemainsReachable);
            Assert.NotNull(model.ReleaseEstimate.RemainingRootPath);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-shared-root.png"));
            await model.EstimateObjectCommand.ExecuteAsync(null); await Idle();
            Assert.True(model.ReleaseEstimate.ReclaimableBytes >= 65_536); Assert.False(model.ReleaseEstimate.SelectedObjectRemainsReachable);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-release.png"));
            ThemeService.Apply("Light"); await Idle(); Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-release-light.png"));
            ThemeService.Apply(shell.ThemeName);
            model.SetBaselineCommand.Execute(null);
            await fixture.StandardInput.WriteLineAsync("grow"); await fixture.StandardInput.FlushAsync();
            Assert.Equal("DONE", await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await model.CaptureCommand.ExecuteAsync(null);
            Assert.Equal(1, model.Types.Single(t => t.Key == pageType.Key).Count - pageType.Count);
            model.TypeSort = "Growth since baseline"; model.TypeFilter = "RetentionFixture";
            await Idle(); Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-comparison.png"));
            model.TypeFilter = "";
            await model.CloseCaptureCommand.ExecuteAsync(null);
            Assert.False(model.HasCapture); Assert.False(fixture.HasExited);
            // Closing/reopening the docked workbench preserves its view model and pinned baseline.
            tab.Close(); await Idle(); shell.ShowToolCommand.Execute("Profiler"); await Idle();
            Assert.True(model.HasBaseline);
            Assert.Same(pane, manager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "Profiler").Content);
        }
        finally { if (!fixture.HasExited) fixture.Kill(); }
    }
}
