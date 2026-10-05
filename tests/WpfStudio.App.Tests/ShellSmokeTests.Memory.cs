using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using WpfStudio.App;
using WpfStudio.App.Features.Profiling;
using WpfStudio.App.Features.Profiling.Visuals;
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
        var focused = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_MEMORY_ONLY") == "1";
        // The focused run gives the docked workbench a desktop-sized document area for review screenshots.
        if (focused) { window.Width = 1880; window.Height = 1120; await Idle(); }
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
            // Three pages make the fixture's growth and grouping visible.
            for (var i = 0; i < 2; i++) await Command(fixture, "grow");
            await model.RefreshProcessesCommand.ExecuteAsync(null);
            model.SelectedProcess = model.Processes.Single(p => p.Id == fixture.Id);
            // The empty state watches the selected process live while the pane is visible.
            using (var live = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (model.LiveSamples.Count < 3) await Task.Delay(50, live.Token);
            Assert.True(model.IsMonitorActive); Assert.Contains("Private", model.LiveMemoryText);
            await Idle(); Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-empty-live.png"));
            await model.CaptureCommand.ExecuteAsync(null);
            Assert.True(model.HasCapture, model.Status); Assert.True(model.Summary!.IsComplete, model.Status);
            Assert.Equal(5, model.Kpis.Count); Assert.NotEmpty(model.GenerationSegments); Assert.NotEmpty(model.CompositionItems);
            Assert.NotEmpty(model.Summary.TopRetainers!);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (model.DominatorRoots.Count == 0 || model.IsInspecting) await Task.Delay(20, deadline.Token);
            await Idle();
            Assert.Equal(MemoryProfilerViewModel.OverviewView, model.SelectedView);
            Assert.True(Descendants<MemoryTreemap>(pane).First(t => t.IsVisible).ActualHeight > 200);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-overview.png"));

            // Types: the Sankey shows the static cache and the static event that keep the pages alive.
            var pageType = model.Types.Single(t => t.Name == "WpfStudio.RetentionFixture.RetainedPageModel");
            Assert.Equal(3, pageType.Count);
            model.OpenTypeCommand.Execute(pageType.Key);
            Assert.Equal(MemoryProfilerViewModel.TypesView, model.SelectedView);
            while (model.Details?.Object.Type != pageType.Name || model.IsInspecting || model.Flow is null || model.IsLoadingFlow) await Task.Delay(20, deadline.Token);
            Assert.Contains(model.Flow!.Nodes, n => n.Kind == "Static" && n.Label.Contains("Cache.Pages"));
            Assert.Equal(3, model.Flow.Nodes.Single(n => n.Kind == "Target").Count);
            Assert.NotEmpty(model.RootPaths); Assert.Contains(model.FieldNodes, f => f.Name == "CustomerId");
            await Idle();
            var sankey = Descendants<MemorySankey>(pane).Single();
            Assert.True(sankey.ActualWidth > 300 && sankey.ActualHeight > 150, $"Sankey is {sankey.ActualWidth} × {sankey.ActualHeight}");
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-types.png"));
            var fields = Descendants<System.Windows.Controls.TreeView>(pane).Single(g => System.Windows.Automation.AutomationProperties.GetName(g) == "Object fields");
            Assert.True(fields.ActualHeight > 60, $"Object fields tree height is {fields.ActualHeight}");
            var payload = model.FieldNodes.Single(f => f.Name == "Payload");
            Assert.True(payload.IsReference);
            payload.IsExpanded = true;
            while (payload.IsLoading || payload.Children.Any(c => c.IsPlaceholder)) await Task.Delay(20, deadline.Token);
            Assert.Contains(payload.Children, c => c.Name == "[0]");

            // Right-click menus offer the same actions everywhere.
            var typeRow = Descendants<System.Windows.Controls.Grid>(pane).First(g => MemoryMenus.GetTarget(g) is MemoryTypeRow);
            Assert.True(MemoryMenus.Populate(typeRow));
            Assert.Contains(typeRow.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>(), i => (string)i.Header == "Group instances by retention");

            // Grouping: all three pages share one static retention path; their payload arrays are identical.
            model.TypeDetailTab = 1; model.InstanceGrouping = "Retention";
            while (model.GroupRows.Count == 0 || model.IsLoadingGroups) await Task.Delay(20, deadline.Token);
            var cacheGroup = model.GroupRows.First();
            Assert.Equal(3, cacheGroup.Group.Count); Assert.True(cacheGroup.IsRoot); Assert.True(cacheGroup.HasSteps);
            cacheGroup.IsExpanded = true; await Idle();
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-groups.png"));
            var bytesType = model.Types.Single(t => t.Name == "System.Byte[]");
            model.InstanceGrouping = "Value"; model.OpenTypeCommand.Execute(bytesType.Key);
            while (model.InstanceGroups?.TypeKey != bytesType.Key || model.IsLoadingGroups) await Task.Delay(20, deadline.Token);
            Assert.Contains(model.GroupRows, g => g.Kind == "Value" && g.Group.Count == 3 && g.HasWaste);
            await Idle(); Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-groups-value.png"));
            model.InstanceGrouping = "None"; model.TypeDetailTab = 0; model.OpenTypeCommand.Execute(pageType.Key);
            while (model.Objects.FirstOrDefault()?.TypeKey != pageType.Key || model.IsInspecting) await Task.Delay(20, deadline.Token);

            // Graph: vertical layout with roots on top and a minimap.
            model.SelectedView = MemoryProfilerViewModel.GraphView; await Idle();
            var graph = Descendants<MemoryGraphSurface>(pane).Single();
            Assert.True(graph.ActualWidth > 300 && graph.ActualHeight > 200, $"Graph is {graph.ActualWidth} × {graph.ActualHeight}");
            Assert.Contains(model.Graph!.References, r => r.Label.StartsWith("Static "));
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-graph.png"));
            var owners = model.Graph.Nodes.Count;
            var focusNode = model.Graph.Nodes.Single(n => n.IsFocus);
            await model.ExpandGraphCommand.ExecuteAsync(new GraphExpandRequest(focusNode.Object.Id, false));
            Assert.True(model.Graph.Nodes.Count >= owners);
            var inlineWidth = graph.ActualWidth;
            model.ToggleGraphFocusCommand.Execute(null); await Idle();
            Assert.False(model.IsBrowserVisible);
            Assert.True(graph.ActualWidth >= inlineWidth + 250, $"Focused graph is {graph.ActualWidth} wide; inline it was {inlineWidth}");
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-graph-focus.png"));
            model.ToggleGraphFocusCommand.Execute(null); await Idle();
            MemoryGraphSurface.FitGraphCommand.Execute(null, graph); await Idle();
            var initialZoom = graph.Zoom;
            MemoryGraphSurface.ZoomInCommand.Execute(null, graph); Assert.True(graph.Zoom > initialZoom);

            // Retention: dominator tree and treemap.
            model.SelectedView = MemoryProfilerViewModel.RetentionView; await Idle();
            Assert.NotEmpty(model.RetentionMapItems);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-retention.png"));
            model.RetentionChart = "Sunburst";
            while (model.Sunburst is null || model.IsLoadingSunburst) await Task.Delay(20, deadline.Token);
            await Idle();
            var sunburst = Descendants<SunburstChart>(pane).Single();
            // The full smoke run uses the default window, so only require room for a readable chart.
            Assert.True(sunburst.IsVisible && Math.Min(sunburst.ActualWidth, sunburst.ActualHeight) > (focused ? 250 : 140), $"Sunburst is {sunburst.ActualWidth} × {sunburst.ActualHeight}");
            Assert.NotEmpty(model.Sunburst!.Root.Children);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-sunburst.png"));
            model.RetentionChart = "Treemap";

            // Why alive, then the shared-root and exclusive-release estimates.
            model.SelectedView = MemoryProfilerViewModel.TypesView;
            await model.InspectObjectCommand.ExecuteAsync(model.Objects[0].Id);
            model.InspectorTab = MemoryProfilerViewModel.RootsTab; await Idle();
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-roots.png"));
            model.SelectedReference = model.Details!.Incoming.First(r => !r.IsRoot && r.Owner.Contains("[]"));
            await model.EstimateReferenceCommand.ExecuteAsync(null); await Idle();
            Assert.True(model.ReleaseEstimate!.SelectedObjectRemainsReachable);
            Assert.NotNull(model.ReleaseEstimate.RemainingRootPath);
            Assert.Equal(MemoryProfilerViewModel.KeepsAliveTab, model.InspectorTab);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-shared-root.png"));
            await model.EstimateObjectCommand.ExecuteAsync(null); await Idle();
            Assert.True(model.ReleaseEstimate.ReclaimableBytes >= 65_536); Assert.False(model.ReleaseEstimate.SelectedObjectRemainsReachable);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-release.png"));
            ThemeService.Apply("Light"); await Idle();
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-release-light.png"));
            model.SelectedView = MemoryProfilerViewModel.OverviewView; await Idle();
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-overview-light.png"));
            model.SelectedView = MemoryProfilerViewModel.GraphView; await Idle();
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-graph-light.png"));
            ThemeService.Apply(shell.ThemeName);

            // Baseline comparison: growth shows up as a finding and in the Overview.
            model.SetBaselineCommand.Execute(null);
            await Command(fixture, "grow");
            await model.CaptureCommand.ExecuteAsync(null);
            Assert.Equal(1, model.Types.Single(t => t.Key == pageType.Key).Count - pageType.Count);
            Assert.Contains(model.Findings, f => f.Insight.Id == "baseline-growth");
            Assert.Contains(model.GrowthRows, g => g.Type.Key == pageType.Key);
            model.SelectedView = MemoryProfilerViewModel.OverviewView;
            while (model.IsInspecting) await Task.Delay(20, deadline.Token);
            await Idle(); Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-comparison.png"));
            model.TypeSort = "Growth since baseline"; model.TypeFilter = "RetentionFixture"; model.SelectedView = MemoryProfilerViewModel.TypesView;
            await Idle(); Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-comparison-types.png"));
            model.TypeFilter = "";

            // Snapshots: history, live timeline and type changes; a third capture confirms steady growth.
            model.SelectedView = MemoryProfilerViewModel.SnapshotsView; await Idle();
            Assert.Equal(2, model.Snapshots.Count); Assert.True(model.HasComparison);
            Assert.Contains(model.ComparisonRows, r => r.Key == pageType.Key && r.CountDelta == 1);
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-snapshots.png"));
            await Command(fixture, "grow");
            await model.CaptureCommand.ExecuteAsync(null);
            while (model.IsInspecting) await Task.Delay(20, deadline.Token);
            Assert.Same(model.Snapshots[0], model.BaselineSnapshot);
            Assert.True(model.ComparisonRows.First(r => r.Key == pageType.Key).GrowsEveryTime);
            Assert.Contains(model.Findings, f => f.Insight.Id == "steady-growth");
            Assert.Equal(3, model.TimelineMarkers.Count);
            await Idle(); Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-snapshots-steady.png"));
            ThemeService.Apply("Light"); await Idle();
            Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-snapshots-light.png"));
            ThemeService.Apply(shell.ThemeName); await Idle();
            // A short docked workbench still leaves room for the views and the browser.
            if (focused) { pane.Height = 460; await Idle(); Screenshot(pane, Path.Combine(root, "artifacts/screenshots/memory-short.png")); pane.Height = double.NaN; await Idle(); }
            await model.CloseCaptureCommand.ExecuteAsync(null);
            Assert.False(model.HasCapture); Assert.False(fixture.HasExited);
            // Closing/reopening the docked workbench preserves its view model and pinned baseline.
            tab.Close(); await Idle(); shell.ShowToolCommand.Execute("Profiler"); await Idle();
            Assert.True(model.HasBaseline);
            Assert.Same(pane, manager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "Profiler").Content);
        }
        finally { if (!fixture.HasExited) fixture.Kill(); }

        static async Task Command(Process process, string command)
        {
            await process.StandardInput.WriteLineAsync(command); await process.StandardInput.FlushAsync();
            Assert.Equal("DONE", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }
}
