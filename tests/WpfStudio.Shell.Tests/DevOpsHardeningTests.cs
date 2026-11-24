using WpfStudio.Api.Requests;
using WpfStudio.Api.Storage;
using WpfStudio.App.Features.Api;
using WpfStudio.App.Features.DevOps;
using WpfStudio.App.Features.Git;
using WpfStudio.Contracts;
using WpfStudio.Core.DevOps;
using WpfStudio.Core.Net;

namespace WpfStudio.Shell.Tests;

public sealed class DevOpsHardeningTests
{
    [Fact]
    public async Task ForgetOnUiMarshalsFaultHandlerOntoDispatcherAndIgnoresCancellation()
    {
        var dispatcher = new RecordingDispatcher();
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task.FromException(new InvalidOperationException("boom"))
            .ForgetOnUi(dispatcher, ex => observed.TrySetResult(ex));

        var exception = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("boom", exception.Message);
        Assert.Equal(1, dispatcher.PostCount);

        var cancelledCalled = false;
        Task.FromCanceled(new CancellationToken(canceled: true))
            .ForgetOnUi(dispatcher, _ => cancelledCalled = true);

        await Task.Delay(50);
        Assert.False(cancelledCalled);
        Assert.Equal(1, dispatcher.PostCount);
    }

    [Fact]
    public void PipelinesDisplayedTruncatesSingleLineLogOverLimit()
    {
        var singleLine = new string('x', PipelinesViewModel.LogDisplayLimit + 200_000);

        var displayed = PipelinesViewModel.Displayed(singleLine);

        Assert.StartsWith("… the first 200,000 characters are left out;", displayed, StringComparison.Ordinal);
        var newline = displayed.IndexOf('\n', StringComparison.Ordinal);
        Assert.True(newline > 0);
        Assert.Equal(PipelinesViewModel.LogDisplayLimit, displayed.Length - newline - 1);
    }

    [Fact]
    public void PipelinesDisplayedSlicesAtNewlineWhenOneExistsAfterCutoff()
    {
        var prefix = new string('a', 250_000) + "\n";
        var tail = new string('b', PipelinesViewModel.LogDisplayLimit - 50_000);
        var log = prefix + tail;

        var displayed = PipelinesViewModel.Displayed(log);

        Assert.EndsWith("\n" + tail, displayed, StringComparison.Ordinal);
    }

    [Fact]
    public void PipelinesSelectedViewChangeNotifiesRunPipelineCommandAfterViewUpdates()
    {
        var host = new StubDevOpsHost();
        using var vm = new PipelinesViewModel(host);
        var recent = new PipelineViewChoice(PipelineView.Recent, "Recent runs");
        var definitions = new PipelineViewChoice(PipelineView.Pipelines, "Pipelines");
        vm.Views.Add(recent);
        vm.Views.Add(definitions);
        vm.SelectedView = recent;
        vm.SelectedDefinition = new DevOpsPipeline("1", "CI", "https://example.com/1");

        Assert.False(vm.RunPipelineCommand.CanExecute(null));

        bool? canExecuteDuringEvent = null;
        vm.RunPipelineCommand.CanExecuteChanged += (_, _) => canExecuteDuringEvent = vm.RunPipelineCommand.CanExecute(null);

        vm.SelectedView = definitions;

        Assert.True(canExecuteDuringEvent);
        Assert.True(vm.RunPipelineCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApiClientSaveResponseAsyncHandlesIoExceptionWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), "WpfStudio-ApiSaveError-" + Guid.NewGuid().ToString("N"));
        var dialogs = new FailingPathDialogs(Path.Combine(root, "missing-dir", "sub", "response.bin"));
        var desktop = new FakeDesktop();
        var proxy = new StudioWebProxy(ProxyConfiguration.Direct);
        var client = new ApiClientViewModel(new ApiStore(root, PlainSecretProtector.Instance), proxy, dialogs, dialogs, desktop);
        try
        {
            await client.InitializeAsync();
            client.NewRequestCommand.Execute(null);
            client.SelectedTab!.Response = ApiResponseViewModel.From(new ApiResponse { StatusCode = 200, ReasonPhrase = "OK", Body = [1, 2, 3] });

            await client.SaveResponseCommand.ExecuteAsync(null);

            Assert.Contains("could not be saved", client.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await client.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private int _posts;
        public int PostCount => Volatile.Read(ref _posts);

        public void Post(Action action)
        {
            Interlocked.Increment(ref _posts);
            action();
        }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            Post(action);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPathDialogs(string savePath) : IFileDialogService, IUserDialogService
    {
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult<string?>(savePath);
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(SaveDecision.Discard);
        public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(defaultValue);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class FakeDesktop : IClipboardService
    {
        public bool TrySetText(string text) => true;
    }

    private sealed class StubDevOpsHost : IDevOpsHost
    {
        public IDevOpsProvider? Provider => null;
        public DevOpsIdentity? User => null;
        public DevOpsSettings Settings { get; } = new();
        public GitViewModel Git => throw new NotSupportedException();
        public GitService GitService => throw new NotSupportedException();
        public int Version => 1;
        public bool CanAct => true;
        public Task RunAsync(string label, Func<IDevOpsProvider, DevOpsIdentity, CancellationToken, Task> action, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void ReportError(Exception exception) { }
        public void ReportStatus(string status) { }
        public void OpenUrl(string url) { }
        public void OpenFile(string path) { }
        public void Copy(string text, string what) { }
        public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public void ShowWorkItem(int id) { }
        public void ShowPullRequest(int number) { }
        public Task SaveSettingsAsync(DevOpsSettings settings) => Task.CompletedTask;
        public void StartCreatePullRequest() { }
        public void NotifyCommands() { }
    }
}
