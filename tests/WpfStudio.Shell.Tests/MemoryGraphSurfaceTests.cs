using System.Windows;
using System.Windows.Input;
using WpfStudio.App.Features.Profiling;
using WpfStudio.App.Features.Profiling.Visuals;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Shell.Tests;

public sealed class MemoryGraphSurfaceTests
{
    private static MemoryObjectInfo Object(int id, long retained) => new(id, "0x1", "Page", "App.Page", "app", 10, retained, true, "Generation2", false);
    private static MemoryGraph Graph() => new([new(Object(1, 40), 0, true, false, IncomingCount: 3)], [], false, "");

    private static MemoryGraphSurface Arranged()
    {
        var surface = new MemoryGraphSurface { Graph = Graph() };
        surface.Measure(new Size(800, 600));
        surface.Arrange(new Rect(0, 0, 800, 600));
        surface.UpdateLayout();
        return surface;
    }

    [Fact]
    public async Task PlainKeysZoomButControlAndAltChordsAreLeftForTheShell()
    {
        await OnStaThread(() =>
        {
            var surface = Arranged();
            foreach (var key in new[] { Key.OemMinus, Key.OemPlus, Key.Subtract, Key.Add, Key.M, Key.Left, Key.Enter })
            {
                Assert.False(surface.HandleKey(key, ModifierKeys.Control), key.ToString());
                Assert.False(surface.HandleKey(key, ModifierKeys.Control | ModifierKeys.Shift), key.ToString());
                Assert.False(surface.HandleKey(key, ModifierKeys.Alt), key.ToString());
            }
            var zoom = surface.Zoom;
            Assert.True(surface.HandleKey(Key.OemMinus, ModifierKeys.None));
            Assert.True(surface.Zoom < zoom);
            zoom = surface.Zoom;
            Assert.True(surface.HandleKey(Key.OemPlus, ModifierKeys.Shift));
            Assert.True(surface.Zoom > zoom);
            Assert.True(surface.HandleKey(Key.D0, ModifierKeys.Control));
        });
    }

    [Fact]
    public async Task HandleHighlightClearsWhenThePointerMovesOffIt()
    {
        await OnStaThread(() =>
        {
            var surface = Arranged();
            var handle = Assert.Single(surface.Handles);
            surface.UpdateHover(surface.Screen(new(handle.Bounds.X + handle.Bounds.Width / 2, handle.Bounds.Y + handle.Bounds.Height / 2)));
            Assert.Equal((1, true), surface.HoverHandle);
            surface.UpdateHover(new(1, 1));
            Assert.Null(surface.HoverHandle);
        });
    }

    [Fact]
    public void LargestRetainedSizeIsWorkedOutOnceForTheCardsOnly()
    {
        var graph = new MemoryGraph([new(Object(1, 40), 0, true, false), new(Object(2, 900), -1, false, false)],
            [new(10, 2, 1, "owner", "page", "_page", "Strong reference", false, false)], false, "");
        Assert.Equal(900, GraphLayout.Build(graph, new HashSet<string>(), (_, _) => 10).MaxRetainedBytes);
        Assert.Equal(1, GraphLayout.Build(new([], [], false, ""), new HashSet<string>(), (_, _) => 10).MaxRetainedBytes);
    }

    private static Task OnStaThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
