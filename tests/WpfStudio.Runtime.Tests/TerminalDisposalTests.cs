using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Terminal;

namespace WpfStudio.Runtime.Tests;

public sealed class TerminalDisposalTests
{
    [Fact]
    public async Task BackgroundDisposalClearsBoundSessionsOnTheirDispatcherAndClosesProcesses()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                TerminalViewModel? model = null;
                try
                {
                    model = new TerminalViewModel(new TestDispatcher(dispatcher)) { WorkingDirectory = Path.GetTempPath() };
                    model.SelectedShell = model.Shells.Single(shell => shell.Name == "Command Prompt");
                    var collectionView = CollectionViewSource.GetDefaultView(model.Sessions);
                    collectionView.CollectionChanged += (_, _) => Assert.True(dispatcher.CheckAccess());
                    model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(TerminalViewModel.SelectedSession)) Assert.True(dispatcher.CheckAccess()); };
                    await model.NewSessionCommand.ExecuteAsync(null);
                    Assert.Single(model.Sessions);
                    using var shellProcess = Process.GetProcessById(model.Sessions[0].Session.ProcessId);
                    await Task.Run(async () => await model.DisposeAsync());
                    Assert.Empty(model.Sessions); Assert.Null(model.SelectedSession); Assert.True(collectionView.IsEmpty);
                    await shellProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await model.DisposeAsync(); // Repeated disposal does not require a second dispatcher operation.
                    await model.NewSessionCommand.ExecuteAsync(null);
                    Assert.Empty(model.Sessions);

                    // Dispose while the process creation command is between its async start and UI continuation.
                    var racing = new TerminalViewModel(new TestDispatcher(dispatcher)) { WorkingDirectory = Path.GetTempPath() };
                    racing.SelectedShell = racing.Shells.Single(shell => shell.Name == "Command Prompt");
                    var creation = racing.NewSessionCommand.ExecuteAsync(null);
                    await Task.Run(async () => await racing.DisposeAsync());
                    await creation;
                    Assert.Empty(racing.Sessions); Assert.Null(racing.SelectedSession);
                    result.TrySetResult();
                }
                catch (Exception exception) { result.TrySetException(exception); }
                finally
                {
                    if (model is not null) await model.DisposeAsync();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Terminal disposal regression" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private sealed class TestDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public void Post(Action action) => dispatcher.BeginInvoke(action);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }
}
