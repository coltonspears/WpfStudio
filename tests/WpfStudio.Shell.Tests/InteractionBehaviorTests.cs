using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Behaviors;

namespace WpfStudio.Shell.Tests;

public sealed partial class InteractionBehaviorTests
{
    private sealed partial class Model : ObservableObject
    {
        [ObservableProperty] public partial object? Node { get; set; }
        [ObservableProperty] public partial bool WantsFocus { get; set; }
    }

    [Fact]
    public Task TreeSelectionReachesASourceThatStartsEmpty() => OnDispatcherAsync(async () =>
    {
        var model = new Model();
        var first = new TreeViewItem { Header = "first" };
        var second = new TreeViewItem { Header = "second" };
        var tree = new TreeView { DataContext = model };
        tree.Items.Add(first); tree.Items.Add(second);
        Interaction.SetTrackSelection(tree, true);
        BindingOperations.SetBinding(tree, Interaction.SelectionSourceProperty, new Binding(nameof(Model.Node)));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        first.IsSelected = true;
        Assert.Same(first, model.Node);
        second.IsSelected = true;
        Assert.Same(second, model.Node);

        Interaction.SetTrackSelection(tree, false);
        first.IsSelected = true;
        Assert.Same(second, model.Node);
    });

    [Fact]
    public Task FocusWhenTrueFocusesTheControlEachTimeItIsAsked() => OnDispatcherAsync(async () =>
    {
        var model = new Model();
        var box = new TextBox { Text = "query" };
        var other = new TextBox();
        var window = new Window
        {
            DataContext = model, ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Width = 200, Height = 120,
            Content = new StackPanel { Children = { box, other } }
        };
        BindingOperations.SetBinding(box, Interaction.FocusWhenTrueProperty, new Binding(nameof(Model.WantsFocus)));
        window.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            for (var request = 0; request < 2; request++)
            {
                other.Focus();
                Assert.Same(other, FocusManager.GetFocusedElement(window));

                model.WantsFocus = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                Assert.Same(box, FocusManager.GetFocusedElement(window));
                Assert.Equal("query", box.SelectedText);
                Assert.False(model.WantsFocus); // Cleared, so that the next request raises a change.
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ActivateCommandRunsForTheRowUnderTheMouseAndNotForTheListAroundIt() => OnDispatcherAsync(async () =>
    {
        var opened = new List<string?>();
        var list = new ListBox { ItemsSource = new[] { "first", "second", "locked" }, Width = 200, Height = 120 };
        Interaction.SetActivateCommand(list, new RelayCommand<string>(opened.Add, item => item != "locked"));
        var window = new Window { ShowInTaskbar = false, ShowActivated = false, Opacity = 0, SizeToContent = SizeToContent.WidthAndHeight, Content = list };
        window.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            list.SelectedIndex = 0;
            ListBoxItem Row(int index) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            void DoubleClick(object source) => list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = source });

            DoubleClick(list);
            Assert.Empty(opened); // The selected row stays put when the click was beside it.

            DoubleClick(Row(1));
            Assert.Equal(["second"], opened);

            DoubleClick(Row(2));
            Assert.Equal(["second"], opened); // The command said no.

            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.Equal(["second", "first"], opened);
        }
        finally { window.Close(); }
    });

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
