using WpfStudio.Contracts;
using WpfStudio.Core.DevOps;
using WpfStudio.Core.DevOps.AzureDevOps;
using WpfStudio.Core.DevOps.GitHub;

namespace WpfStudio.Core.Tests.DevOps;

public sealed class DevOpsCachingTests
{
    private static readonly DevOpsTarget AzureTarget = DevOpsTarget.FromRemoteUrl("https://dev.azure.com/contoso/Web/_git/Web.App")!;
    private static readonly DevOpsTarget GitHubTarget = DevOpsTarget.FromRemoteUrl("https://github.com/octo-org/hello.git")!;

    [Fact]
    public async Task AzureDevOpsGetCurrentUserCancellationByCaller1DoesNotPoisonCaller2()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        using var handler = new AsyncStubHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return FakeHttp.Response(200, """{"authenticatedUser":{"id":"u1","providerDisplayName":"Ada Lovelace"}}""");
            }
            return FakeHttp.Response(404);
        });
        using var http = new HttpClient(handler);
        var provider = new AzureDevOpsProvider(http, AzureTarget, "pat-token");

        using var cts1 = new CancellationTokenSource();
        var caller1 = provider.GetCurrentUserAsync(cts1.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var caller2 = provider.GetCurrentUserAsync(CancellationToken.None);
        cts1.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller1);
        release.TrySetResult();

        var user = await caller2.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Ada Lovelace", user.DisplayName);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task AzureDevOpsGetCurrentUserFaultedTaskIsNotReused()
    {
        var calls = 0;
        using var handler = new AsyncStubHandler((_, _) =>
        {
            var attempt = Interlocked.Increment(ref calls);
            if (attempt == 1) return Task.FromResult(FakeHttp.Response(500, """{"message":"temporary failure"}"""));
            return Task.FromResult(FakeHttp.Response(200, """{"authenticatedUser":{"id":"u1","providerDisplayName":"Ada Lovelace"}}"""));
        });
        using var http = new HttpClient(handler);
        var provider = new AzureDevOpsProvider(http, AzureTarget, "pat-token");

        await Assert.ThrowsAsync<DevOpsException>(() => provider.GetCurrentUserAsync(CancellationToken.None));
        var user = await provider.GetCurrentUserAsync(CancellationToken.None);
        Assert.Equal("Ada Lovelace", user.DisplayName);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task AzureDevOpsTokenCheckCancellationByCaller1DoesNotPoisonCaller2()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenChecks = 0;

        using var handler = new AsyncStubHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref tokenChecks);
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return FakeHttp.Response(200, """{"authenticatedUser":{"id":"u1"}}""");
            }
            return FakeHttp.Response(401, """{"message":"missing build scope"}""");
        });
        using var http = new HttpClient(handler);
        var provider = new AzureDevOpsProvider(http, AzureTarget, "pat-token");

        using var cts1 = new CancellationTokenSource();
        var caller1 = provider.ListBranchesAsync(cts1.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var caller2 = provider.ListBranchesAsync(CancellationToken.None);
        cts1.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller1);
        release.TrySetResult();

        // Token is valid (200 on connectionData), so caller 2 must see DevOpsAccessDeniedException (not cancellation).
        await Assert.ThrowsAsync<DevOpsAccessDeniedException>(() => caller2.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref tokenChecks));
    }

    [Fact]
    public async Task GitHubGetCurrentUserCancellationByCaller1DoesNotPoisonCaller2()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        using var handler = new AsyncStubHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return FakeHttp.Response(200, """{"login":"octocat","name":"The Octocat"}""");
            }
            return FakeHttp.Response(404);
        });
        using var http = new HttpClient(handler);
        var provider = new GitHubProvider(http, GitHubTarget, "ghp-token");

        using var cts1 = new CancellationTokenSource();
        var caller1 = provider.GetCurrentUserAsync(cts1.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var caller2 = provider.GetCurrentUserAsync(CancellationToken.None);
        cts1.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller1);
        release.TrySetResult();

        var user = await caller2.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("The Octocat", user.DisplayName);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task GitHubRepositoryCancellationByCaller1DoesNotPoisonCaller2()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        using var handler = new AsyncStubHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/repos/octo-org/hello", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return FakeHttp.Response(200, """{"default_branch":"main","private":false}""");
            }
            return FakeHttp.Response(404);
        });
        using var http = new HttpClient(handler);
        var provider = new GitHubProvider(http, GitHubTarget, "ghp-token");

        using var cts1 = new CancellationTokenSource();
        var caller1 = provider.GetDefaultBranchAsync(cts1.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var caller2 = provider.GetDefaultBranchAsync(CancellationToken.None);
        cts1.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller1);
        release.TrySetResult();

        var branch = await caller2.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("main", branch);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ForgetOnUiMarshalsFaultOntoDispatcherAndIgnoresCancellation()
    {
        var dispatcher = new RecordingDispatcher();
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task.FromException(new InvalidOperationException("fault"))
            .ForgetOnUi(dispatcher, ex => observed.TrySetResult(ex));

        var ex = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("fault", ex.Message);
        Assert.Equal(1, dispatcher.PostCount);

        var cancelledCalled = false;
        Task.FromCanceled(new CancellationToken(canceled: true))
            .ForgetOnUi(dispatcher, _ => cancelledCalled = true);
        await Task.Delay(50);
        Assert.False(cancelledCalled);
        Assert.Equal(1, dispatcher.PostCount);
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private int _posts;
        public int PostCount => Volatile.Read(ref _posts);
        public void Post(Action action) { Interlocked.Increment(ref _posts); action(); }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { Post(action); return Task.CompletedTask; }
    }
}
