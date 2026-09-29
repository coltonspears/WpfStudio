using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    private static async Task VerifyXamlLayoutEditingAsync(string root, string data, MainWindow window, DockingManager manager, ShellViewModel shell)
    {
        const string source = """
            <Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="640" Height="340" Background="#F2F5FA">
              <TextBlock Canvas.Left="32" Canvas.Top="24" Text="Design your next screen" FontSize="26" FontWeight="SemiBold" Foreground="#172B4D"/>
              <TextBlock Canvas.Left="32" Canvas.Top="66" Text="Move and resize, then review the XAML." Foreground="#53627B"/>
              <Border Canvas.Left="32" Canvas.Top="112" Width="264" Height="100" Background="White" CornerRadius="8" Padding="20">
                <StackPanel><TextBlock Text="Workspace" FontSize="18" Foreground="#172B4D"/><TextBlock Text="Changes stay in your editor until saved." TextWrapping="Wrap" Margin="0,8,0,0" Foreground="#53627B"/></StackPanel>
              </Border>
              <Button x:Name="PrimaryAction" Canvas.Left="32" Canvas.Top="244"
                      Width="160" Height="42" Content="Create workspace"
                      Background="#2457C5" Foreground="White"/>
              <Button x:Name="SecondaryAction" Canvas.Left="328" Canvas.Top="244"
                      Width="160" Height="42" Content="Browse examples"
                      Background="White" Foreground="#2457C5"/>
            </Canvas>
            """;
        string path = Path.Combine(data, "LayoutAuthoring.xaml");
        await File.WriteAllTextAsync(path, source);
        await shell.OpenDocumentAsync(path);
        var document = shell.ActiveDocument!;
        var model = shell.Designer;
        model.AutoRefresh = false;
        model.Mode = PreviewMode.Source;
        model.PreviewWidth = 640;
        model.PreviewHeight = 340;
        model.Zoom = .55;
        await shell.OpenDesignerCommand.ExecuteAsync(null);
        await Idle();
        Assert.True(model.IsCurrent, model.Status);
        var pane = manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(t => t.ContentId == "Designer");
        var view = (FrameworkElement)pane.Content;
        var toggle = Descendants<CheckBox>(view).Single(box => AutomationProperties.GetName(box) == "Edit preview layout");
        toggle.IsChecked = true;
        Assert.True(model.IsLayoutEditingEnabled);
        static IEnumerable<DesignerNode> Flatten(IEnumerable<DesignerNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
        async Task<PreviewLayoutEditContext> SelectAsync()
        {
            model.SelectedNode = Flatten(model.Tree).Single(n => n.Node.Name == "PrimaryAction");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (model.LayoutEditing is null) await Task.Delay(25, deadline.Token);
            Assert.True(model.LayoutEditing.Available, model.LayoutEditing.Status);
            return model.LayoutEditing;
        }
        var original = await SelectAsync();
        Assert.Equal(new PreviewBounds(32, 244, 160, 42), original.Bounds);
        await Idle();
        var surface = Descendants<PreviewSurface>(view).Single();
        Assert.Same(model.LayoutEditing, surface.LayoutEditing);
        Assert.Same(model.LayoutGestureCommand, surface.LayoutGestureCommand);
        var pixels = RenderPixels(surface);
        await model.LayoutGestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Begin, XamlLayoutHandle.Right, Zoom: model.Zoom));
        await model.LayoutGestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Update, XamlLayoutHandle.Right, 103, 0, Zoom: model.Zoom));
        Assert.True(model.LayoutDraft?.Success, model.LayoutEditingStatus);
        Assert.Equal(264, model.LayoutDraft!.Bounds!.Width); // Snaps to the card's right edge.
        Assert.NotEmpty(model.LayoutDraft.Guides);
        Assert.Equal(source, document.State.Content);
        await Idle();
        Assert.False(pixels.SequenceEqual(RenderPixels(surface)), "A resize draft must visibly show its frame and snap guide.");
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-layout-editing.png"));
        var diffOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == nameof(shell.IsPreviewOpen) && shell.IsPreviewOpen) diffOpened.TrySetResult();
        };
        shell.PropertyChanged += changed;
        Task commit;
        try
        {
            commit = model.LayoutGestureCommand.ExecuteAsync(new PreviewLayoutGesture(PreviewLayoutGesturePhase.Commit, XamlLayoutHandle.Right, 103, 0, Zoom: model.Zoom));
            await Task.WhenAny(diffOpened.Task, commit).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(shell.IsPreviewOpen, model.Status);
        }
        finally { shell.PropertyChanged -= changed; }
        var change = Assert.Single(shell.PreviewChanges);
        Assert.Contains("Width=\"264\" Height=\"42\" Content=\"Create workspace\"", change.After);
        Assert.Equal(source, document.State.Content);
        await Idle();
        Screenshot((FrameworkElement)window.Content, Path.Combine(root, "artifacts/screenshots/xaml-layout-editing-review.png"));
        shell.AcceptPreviewCommand.Execute(null);
        await commit;
        Assert.Equal(change.After, document.State.Content);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(new PreviewBounds(32, 244, 264, 42), (await SelectAsync()).Bounds);
        await shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(source, document.State.Content);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(original.Bounds, (await SelectAsync()).Bounds);
        toggle.IsChecked = false;
        await shell.CloseDocumentAsync(document);
        model.PreviewWidth = 960;
        model.PreviewHeight = 640;
        model.Zoom = .65;
        pane.Hide();
    }
}
