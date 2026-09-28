using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Services;
using WpfStudio.Core.Documents;
using WpfStudio.Runtime.Design;

namespace WpfStudio.NativeView.Tests;

[Collection("Native preview view")]
public sealed partial class NativePreviewIntegrationTests
{
    [Fact]
    public async Task RealHostAttachesPreservesStateAndDetachesForInspectionAndInvalidation()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint previous = SetThreadDpiAwarenessContext(new nint(-4));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await VerifyAsync(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }) { IsBackground = true, Name = "Native preview process integration" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }

    private static async Task VerifyAsync()
    {
        const string markup = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel Margin="20">
                <TextBox x:Name="Input" Text="Original preview text" Width="280"/>
                <Button Content="Focusable action" Margin="0,12,0,0"/>
              </StackPanel>
            </Grid>
            """;
        var document = new DocumentState(Path.Combine(Path.GetTempPath(), "WpfStudio-native-" + Guid.NewGuid().ToString("N") + ".xaml"), markup);
        var client = new PreviewClient(Environment.GetEnvironmentVariable("WPFSTUDIO_PREVIEW_HOST_UNDER_TEST"));
        var model = new DesignerViewModel(client, new UiDispatcher(Dispatcher.CurrentDispatcher))
        { AutoRefresh = false, PreviewWidth = 640, PreviewHeight = 420 };
        var pane = new NativePreviewPane { DataContext = model };
        pane.SetBinding(NativePreviewPane.SessionProperty, new Binding(nameof(model.InteractionSession)));
        pane.SetBinding(NativePreviewPane.IsActiveProperty, new Binding(nameof(model.IsInteracting)));
        pane.SetBinding(NativePreviewPane.ContentWidthProperty, new Binding(nameof(model.InteractionWidth)));
        pane.SetBinding(NativePreviewPane.ContentHeightProperty, new Binding(nameof(model.InteractionHeight)));
        pane.SetBinding(NativePreviewPane.ExitInteractionCommandProperty, new Binding(nameof(model.ExitInteractionCommand)));
        var window = new Window { Content = pane, Width = 440, Height = 310,
            Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            await model.OpenAsync(document);
            Assert.True(model.IsCurrent, model.Status);
            Assert.True(model.CanInteract, model.Status);
            int processId = Assert.IsType<int>(client.ProcessId);
            model.SelectedNode = Flatten(model.Tree).Single(node => node.Node.Name == "Input");
            await Until(pane, () => model.Properties.Any(property => property.Name == "Text"), () => model.Status);
            model.SelectedProperty = model.Properties.Single(property => property.Name == "Text");
            string nodeId = model.SelectedNode.Node.Id;
            model.EditedValue = "Unapplied draft before interaction";
            model.EnterInteractionCommand.Execute(null);
            await Until(pane, () => pane.IsAttached, () => pane.Status + " / " + model.Status);
            nint bridge = pane.Surface.BridgeHandle;
            nint child = Assert.Single(ForeignChildren(bridge, processId));
            var session = Assert.IsAssignableFrom<IPreviewInteractionSession>(model.InteractionSession);
            Assert.Equal(bridge, GetParent(child));
            Assert.True(IsWindowVisible(child));
            Assert.Equal("Unapplied draft before interaction", model.EditedValue);

            // This is an inspector edit, not simulated physical keyboard input.
            // It proves that native attach/detach and capture keep the actual view.
            model.EditedValue = "Temporary runtime text";
            await model.ApplyPropertyCommand.ExecuteAsync(null);
            Assert.Equal("Temporary runtime text", model.SelectedProperty!.Value);
            model.EditedValue = "Unapplied inspector draft";
            await model.ExitInteractionCommand.ExecuteAsync(null);
            await Until(pane, () => !pane.IsAttached && !session.IsAttached, () => pane.Status);
            Assert.False(model.IsInteracting);
            Assert.Equal(processId, client.ProcessId);
            Assert.Empty(ForeignChildren(bridge, processId));
            Assert.NotEqual(bridge, GetParent(child));
            Assert.Equal(nodeId, model.SelectedNode!.Node.Id);
            Assert.Equal("Temporary runtime text", model.SelectedProperty!.Value);
            Assert.Equal("Unapplied inspector draft", model.EditedValue);
            Assert.Equal(markup, document.Content);
            Assert.Equal(bridge, pane.Surface.BridgeHandle);

            model.EnterInteractionCommand.Execute(null);
            await Until(pane, () => pane.IsAttached, () => pane.Status + " / " + model.Status);
            Assert.Same(session, model.InteractionSession);
            Assert.Equal(processId, client.ProcessId);
            Assert.Equal(bridge, pane.Surface.BridgeHandle);
            Assert.Equal(child, Assert.Single(ForeignChildren(bridge, processId)));

            pane.IsSuppressed = true;
            await Until(pane, () => !pane.IsAttached && !session.IsAttached, () => pane.Status);
            Assert.Equal(Visibility.Hidden, pane.Surface.Visibility);
            Assert.Empty(ForeignChildren(bridge, processId));
            Assert.True(model.IsInteracting);
            pane.IsSuppressed = false;
            await Until(pane, () => pane.IsAttached, () => pane.Status);
            Assert.Equal(child, Assert.Single(ForeignChildren(bridge, processId)));

            document.Content = markup + "\n<!-- Unsaved source invalidates the current preview. -->";
            await Until(pane, () => model.InteractionSession is null && !pane.IsAttached && !session.IsAttached, () => pane.Status + " / " + model.Status);
            Assert.False(model.IsInteracting);
            Assert.False(model.IsCurrent);
            Assert.Empty(ForeignChildren(bridge, processId));
            Assert.Equal(bridge, pane.Surface.BridgeHandle);
            Assert.Equal(processId, client.ProcessId);
        }
        finally
        {
            await model.DisposeAsync();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            window.Close();
        }
    }

    private static IEnumerable<DesignerNode> Flatten(IEnumerable<DesignerNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private static async Task Until(NativePreviewPane pane, Func<bool> condition, Func<string> status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Native preview state did not settle: " + status() + ". " + NativePreviewTestState.Describe(pane));
            await Task.Delay(15);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
    }

    private static IReadOnlyList<nint> ForeignChildren(nint bridge, int expectedProcess)
    {
        var children = new List<nint>();
        EnumChildWindows(bridge, (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == (uint)expectedProcess && GetParent(hwnd) == bridge) children.Add(hwnd);
            return true;
        }, 0);
        return children;
    }

    private delegate bool EnumWindow(nint hwnd, nint parameter);
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32")] private static extern bool EnumChildWindows(nint hwnd, EnumWindow callback, nint parameter);
}
