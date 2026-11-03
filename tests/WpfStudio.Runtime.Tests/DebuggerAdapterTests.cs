using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

public sealed class DebuggerAdapterTests
{
    private const string Missing = @"Z:\no-such-folder\netcoredbg.exe";

    [Fact]
    public async Task ALocatedAdapterBecomesTheSessionsAdapter()
    {
        var located = Environment.ProcessPath!;
        var session = new DebugSession { AdapterPath = Missing };
        await using var model = new DebuggerViewModel(session, new Inline(), new FakeAdapters { Located = located });
        Assert.Equal(located, session.AdapterPath);
        Assert.False(model.IsAdapterMissing);
    }

    [Fact]
    public async Task EnsureInstallsOnlyAfterTheUserAgrees()
    {
        var adapters = new FakeAdapters { Installed = Environment.ProcessPath! };
        var session = new DebugSession { AdapterPath = Missing };
        await using var model = new DebuggerViewModel(session, new Inline(), adapters);
        Assert.Equal("the fake adapter", model.AdapterDescription);
        Assert.True(model.IsAdapterMissing);

        Assert.False(await model.EnsureAdapterAsync(() => Task.FromResult(false)));
        Assert.Equal(0, adapters.Installs);

        Assert.True(await model.EnsureAdapterAsync(() => Task.FromResult(true)));
        Assert.Equal(1, adapters.Installs);
        Assert.Equal(Environment.ProcessPath, session.AdapterPath);
    }

    private sealed class FakeAdapters : IDebugAdapterProvider
    {
        public string? Located { get; init; }
        public string Installed { get; init; } = "";
        public int Installs { get; private set; }
        public string Description => "the fake adapter";
        public string? Locate() => Located;
        public Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) { Installs++; return Task.FromResult(Installed); }
    }

    private sealed class Inline : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { action(); return Task.CompletedTask; }
    }
}

/// <summary>For tests that never reach the install path.</summary>
internal sealed class NoDebugAdapter : IDebugAdapterProvider
{
    public string Description => "no adapter";
    public string? Locate() => null;
    public Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Tests never install an adapter.");
}
